using System.Buffers;
using System.IO.Pipes;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiTool.Modules.Capture.Recording;

/// <summary>A playback device, what you hear.</summary>
/// <param name="Id">Empty means <see cref="Follows"/>, i.e. no fixed device.</param>
public sealed record RenderDevice(string Id, string Name, bool IsDefault)
{
    /// <summary>The entry that matches no device but follows the Windows setting.</summary>
    /// <remarks>
    /// <para>
    /// <b>The default, for a measured reason.</b> A remembered device id goes stale the moment the output is
    /// switched, and that's only noticed on a silent clip. A recording here captured the monitor output while
    /// the sound was on the headphones.
    /// </para>
    /// <para>
    /// Recording the screen means "my computer's sound", not an endpoint. The device list stays for anyone who
    /// explicitly wants something else.
    /// </para>
    /// </remarks>
    public static readonly RenderDevice Follows = new("", "Windows default output", false);

    public bool IsFollows => Id.Length == 0;
}

/// <summary>System audio: what actually comes out of the speakers.</summary>
/// <remarks>
/// <para>
/// <b>Why this is needed.</b> On Windows ffmpeg knows only DirectShow, and DirectShow knows only
/// <i>recording</i> devices; there is no source for "what is playing". The module could only hope for a
/// driver loopback (Stereo Mix, VB-CABLE, Voicemeeter), and without one it silently recorded the microphone.
/// Measured: a clip with a headset microphone selected had a complete audio track at <b>−67.8 dB</b>.
/// </para>
/// <para>
/// <b>WASAPI can do it.</b> Loopback mode delivers the mix sent to the playback device. NAudio provides it.
/// </para>
/// <para>
/// <b>Why a named pipe and not stdin.</b> stdin belongs to <see cref="Recorder"/>: the <c>q</c> that makes
/// ffmpeg finish the file goes there. ffmpeg on Windows opens <c>\\.\pipe\name</c> as an input file; 6.02 s
/// of audio arrived that way at −15.2 dB, exit code 0.
/// </para>
/// </remarks>
internal sealed class SystemAudio : IDisposable
{
    /// <summary>How many buffers are held before the oldest is dropped.</summary>
    /// <remarks>
    /// With 10 ms buffers that's about two seconds. The WASAPI callback runs on a high-priority thread and
    /// must not block; writing straight to the pipe while ffmpeg is busy would make it wait, and WASAPI then
    /// reports an overrun and drops whole blocks. Better to lose a buffer here than a block there.
    /// </remarks>
    private const int QueueDepth = 200;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<Block> _queue;

    /// <summary>
    /// An audio buffer from the shared pool. Rented instead of allocated: WASAPI reports every 10 ms, and with
    /// eight channels that's about 15 KB per callback, a hundred allocations a second.
    /// </summary>
    private readonly record struct Block(byte[] Buffer, int Length);

    /// <summary>A silent playback stream on the same device.</summary>
    /// <remarks>
    /// <para>
    /// <b>Without it loopback delivers nothing.</b> WASAPI loopback taps an endpoint's mix, which only exists
    /// while something plays there. With no program playing sound, not a single buffer arrives: not silence,
    /// nothing.
    /// </para>
    /// <para>
    /// That's the normal case when a recording starts, and it's not just missing audio: ffmpeg waits for its
    /// second input and therefore emits <b>no video either</b>. Measured: 35 s of buffering without a single
    /// output line, no segment, counter at 0:00.
    /// </para>
    /// <para>
    /// With this keep-alive <b>99 %</b> of the expected bytes arrive, without it <b>0 %</b>. It writes zeros and
    /// is inaudible.
    /// </para>
    /// </remarks>
    private WasapiOut? _keepAlive;

    private Task? _writer;
    private volatile bool _stopping;

    public SystemAudio(string? deviceId)
    {
        try
        {
            _device = Resolve(deviceId);
            DeviceName = _device.FriendlyName;

            _capture = new WasapiLoopbackCapture(_device);

            var format = _capture.WaveFormat;
            SampleRate = format.SampleRate;
            Channels = format.Channels;

            // WASAPI mixes in floating point; the mix nearly always comes out as 32-bit float. Integer
            // is handled anyway: an exotic driver could differ, and ffmpeg would then assume the wrong
            // format, which doesn't fail but produces noise.
            SampleFormat = format.Encoding == WaveFormatEncoding.IeeeFloat
                ? "f32le"
                : format.BitsPerSample == 16 ? "s16le" : "s32le";

            // The raw stream's time axis depends on bytes per sample. A silence buffer with a fixed
            // 4 bytes would be twice as long as intended for s16le and push the audio a quarter second
            // ahead of the video at every dropout.
            BytesPerSample = SampleFormat == "s16le" ? 2 : 4;

            // The name must be unique per process and run: with two recordings in a row the old pipe
            // may not be cleaned up yet.
            PipeName = $"multitool-audio-{Environment.ProcessId}-{Guid.NewGuid():N}";

            _pipe = new NamedPipeServerStream(
                PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 1 << 20);

            // DropWrite, not DropOldest, and it matters:
            //
            // With DropOldest TryWrite always succeeds, so its result is worthless and a loss counter
            // would stay at zero forever. More importantly, this is a RAW audio stream without
            // timestamps; ffmpeg derives time solely from the byte count. A lost buffer shifts
            // everything after it forward for good, and audio and video drift apart.
            //
            // So: drop the new buffer, remember its length and pad it later as silence. The time axis
            // stays correct; you hear a gap instead of an offset that lasts to the end.
            _queue = Channel.CreateBounded<Block>(new BoundedChannelOptions(QueueDepth)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });

            _capture.DataAvailable += OnData;

            StartKeepAlive();
        }
        catch (Exception)
        {
            // Whatever was opened before the error goes back. The caller gets no object to dispose,
            // so an opened endpoint would otherwise stay until the app exits.
            try { _capture?.Dispose(); } catch (Exception) { }
            try { _pipe?.Dispose(); } catch (Exception) { }
            try { _device?.Dispose(); } catch (Exception) { }
            try { _enumerator.Dispose(); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Keeps the endpoint awake so loopback delivers anything.</summary>
    /// <remarks>
    /// Shared mode, so it takes the sound from nobody. If it fails nothing is aborted: audio may be missing,
    /// but recording continues; <see cref="WriteLoopAsync"/> pads silence in that case.
    /// </remarks>
    private void StartKeepAlive()
    {
        try
        {
            var output = new WasapiOut(_device, AudioClientShareMode.Shared, false, 100);
            output.Init(new SilenceProvider(_device.AudioClient.MixFormat));
            output.Play();

            _keepAlive = output;
        }
        catch (Exception)
        {
            // A device in exclusive mode allows no second stream. No reason to refuse recording.
            _keepAlive = null;
        }
    }

    public string DeviceName { get; }

    public string PipeName { get; }

    /// <summary>As it must appear on the ffmpeg command line.</summary>
    public string PipePath => $@"\\.\pipe\{PipeName}";

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Bytes per sample, matching <see cref="SampleFormat"/>.</summary>
    public int BytesPerSample { get; }

    /// <summary>"f32le" and relatives, the value for <c>-f</c>.</summary>
    public string SampleFormat { get; }

    /// <summary>How many buffers were dropped because ffmpeg couldn't keep up.</summary>
    public long Dropped { get; private set; }

    /// <summary>The playback devices currently present.</summary>
    /// <remarks>
    /// <b>Belongs on a background thread.</b> Core Audio is COM, and each enumeration costs several
    /// cross-apartment calls (487 ms in one tile click in the soundboard).
    /// </remarks>
    public static List<RenderDevice> Devices()
    {
        var result = new List<RenderDevice>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var standard = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = standard.ID;
            }
            catch (Exception)
            {
                // A computer without a playback device has no default device.
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new RenderDevice(device.ID, device.FriendlyName, device.ID == defaultId));
                }
            }
        }
        catch (Exception)
        {
            // Without an audio driver there are no devices. An answer, not an error.
        }

        return [.. result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCulture)];
    }

    private MMDevice Resolve(string? deviceId)
    {
        if (deviceId is { Length: > 0 })
        {
            try { return _enumerator.GetDevice(deviceId); }
            catch (Exception)
            {
                // The remembered device was unplugged. Fall back to the default device rather than not
                // recording at all.
            }
        }

        return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>Waits until ffmpeg has opened the pipe, then streams audio into it.</summary>
    /// <remarks>
    /// <b>The order is mandatory:</b> the pipe must exist before ffmpeg starts, since ffmpeg is the client.
    /// So the server is created in the constructor, the process started afterwards, and this method waits
    /// for its connection.
    /// </remarks>
    /// <returns><c>false</c> if ffmpeg didn't connect.</returns>
    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // <b>Capture starts before waiting for ffmpeg</b>, on purpose. A raw stream has no timestamps;
        // ffmpeg assumes it begins at zero. Capturing only after the connection would lose exactly
        // the time ffmpeg needs to open its inputs, and since video is already running the whole
        // recording would be offset.
        //
        // The queue holds about two seconds; opening takes milliseconds.
        _capture.StartRecording();

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { _capture.StopRecording(); } catch (Exception) { }
            return false;
        }

        _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
        return true;
    }

    /// <summary>Bytes still owed as silence.</summary>
    private int _debt;

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_stopping || e.BytesRecorded == 0) return;

        // Copy, don't pass through: NAudio reuses the same buffer on the next callback; a queued
        // buffer would be overwritten before it's written.
        // Debt first, then the new buffer: the order is the time axis.
        // Zero bytes are silence for both f32le and s16le; rented buffers aren't necessarily
        // zeroed, so they're cleared explicitly.
        if (_debt > 0)
        {
            var silence = ArrayPool<byte>.Shared.Rent(_debt);
            Array.Clear(silence, 0, _debt);

            if (!_queue.Writer.TryWrite(new Block(silence, _debt)))
            {
                ArrayPool<byte>.Shared.Return(silence);
                Dropped++;
                _debt += e.BytesRecorded;
                return;
            }

            _debt = 0;
        }

        var block = ArrayPool<byte>.Shared.Rent(e.BytesRecorded);
        Buffer.BlockCopy(e.Buffer, 0, block, 0, e.BytesRecorded);

        if (_queue.Writer.TryWrite(new Block(block, e.BytesRecorded))) return;

        ArrayPool<byte>.Shared.Return(block);
        Dropped++;
        _debt += e.BytesRecorded;
    }

    /// <summary>How long to wait for a buffer before padding silence.</summary>
    private static readonly TimeSpan SilenceAfter = TimeSpan.FromMilliseconds(250);

    /// <summary>Pushes what arrives into the pipe, and silence when nothing does.</summary>
    /// <remarks>
    /// <para>
    /// <b>Audio must not stall video.</b> ffmpeg only emits a frame when all inputs progress. If this one stays
    /// silent, the whole recording stops: no segment, no output line, counter at 0:00.
    /// </para>
    /// <para>
    /// <see cref="StartKeepAlive"/> normally prevents that. This loop is the second safeguard in case the
    /// keep-alive fails: after a quarter second without a buffer, the matching amount of silence is written.
    /// The audio then has a gap, but recording continues.
    /// </para>
    /// </remarks>
    private async Task WriteLoopAsync()
    {
        // The byte count for a quarter second. The time axis depends on it: a raw stream has no
        // timestamps, ffmpeg computes time from the amount.
        var silence = new byte[(int)(SampleRate * Channels * BytesPerSample * SilenceAfter.TotalSeconds)];

        // One timer for the whole loop instead of one per buffer. After expiry it's spent and
        // replaced; otherwise TryReset resets it.
        var wait = new CancellationTokenSource();

        try
        {
            while (!_stopping)
            {
                if (!wait.TryReset())
                {
                    wait.Dispose();
                    wait = new CancellationTokenSource();
                }

                wait.CancelAfter(SilenceAfter);

                Block block;
                try
                {
                    block = await _queue.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Nothing arrived. Time passed anyway.
                    if (_stopping) return;

                    await _pipe.WriteAsync(silence).ConfigureAwait(false);
                    continue;
                }
                catch (ChannelClosedException)
                {
                    return;
                }

                try
                {
                    if (_stopping) return;
                    await _pipe.WriteAsync(block.Buffer.AsMemory(0, block.Length)).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(block.Buffer);
                }
            }
        }
        catch (Exception)
        {
            // ffmpeg is done and closed the pipe. The normal case at the end of every recording.
        }
        finally
        {
            wait.Dispose();
        }
    }

    /// <summary>Stops audio and closes the pipe.</summary>
    /// <remarks>
    /// Must run <b>before</b> waiting for ffmpeg: while the pipe is open ffmpeg sees no end of file on this
    /// input and waits for more data instead of exiting on the <c>q</c>.
    /// </remarks>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;

        try { _capture.StopRecording(); } catch (Exception) { }

        _queue.Writer.TryComplete();

        try { if (_pipe.IsConnected) _pipe.Disconnect(); } catch (Exception) { }
        try { _pipe.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        Stop();

        _capture.DataAvailable -= OnData;

        // Everything holding COM open must be released: a non-collectible load context frees nothing
        // by itself, and a lingering MMDevice holds the endpoint.
        try { _keepAlive?.Stop(); } catch (Exception) { }
        try { _keepAlive?.Dispose(); } catch (Exception) { }
        _keepAlive = null;

        try { _capture.Dispose(); } catch (Exception) { }
        try { _device.Dispose(); } catch (Exception) { }
        try { _enumerator.Dispose(); } catch (Exception) { }
    }
}
