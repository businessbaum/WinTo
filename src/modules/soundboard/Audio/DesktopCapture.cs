using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>How desktop audio is captured.</summary>
public enum DesktopCaptureKind
{
    /// <summary>
    /// Process loopback: all system audio <b>except</b> WinTo itself. The clean way; your own sounds don't
    /// arrive twice.
    /// </summary>
    ExcludingSelf,

    /// <summary>Process loopback on exactly one app and its child processes. Everything else stays out.</summary>
    SingleApp,

    /// <summary>Device loopback: everything reaching the playback device, your own sounds included. Fallback only.</summary>
    WholeDevice,
}

/// <summary>What to capture.</summary>
/// <param name="ProcessId">
/// The app whose audio is sent, with its child processes. <c>null</c> means everything except WinTo.
/// </param>
public sealed record DesktopCaptureTarget(int? ProcessId, string Name)
{
    public static DesktopCaptureTarget WholeDesktop { get; } = new(null, "Whole desktop");
}

/// <summary>
/// Captures the running desktop audio so it can be mixed into the virtual cable. To a listener in Discord
/// it sounds as if music or game audio came from the microphone.
/// </summary>
/// <remarks>
/// <para>
/// The obvious way, <c>WasapiLoopbackCapture</c> on the headphones, also captures your own output. Every sound
/// would arrive in the cable twice, once directly and once delayed by the loopback latency: an audible echo
/// or comb filter.
/// </para>
/// <para>
/// Windows 10 (build 20348) and Windows 11 solve this with <b>process loopback</b>:
/// <c>ActivateAudioInterfaceAsync</c> on the pseudo device <c>VAD\Process_Loopback</c> with
/// <c>EXCLUDE_TARGET_PROCESS_TREE</c> on the own process id. Windows then delivers everything <i>other</i>
/// programs play and leaves WinTo out.
/// </para>
/// <para>
/// The interface is documented but not wrapped by NAudio, so the COM interop is hand-written. If activation
/// fails, the class falls back to device loopback and reports it via <see cref="Kind"/>.
/// </para>
/// </remarks>
public sealed class DesktopCapture : IDisposable
{
    private readonly ProcessLoopbackCapture? _process;
    private readonly WasapiLoopbackCapture? _device;

    private DesktopCapture(ProcessLoopbackCapture process, DesktopCaptureTarget target)
    {
        _process = process;
        Kind = target.ProcessId is null ? DesktopCaptureKind.ExcludingSelf : DesktopCaptureKind.SingleApp;
        WaveFormat = process.WaveFormat;
        SourceName = target.Name;
        process.DataAvailable += (_, e) => DataAvailable?.Invoke(this, e);
    }

    private DesktopCapture(WasapiLoopbackCapture device, string deviceName)
    {
        _device = device;
        Kind = DesktopCaptureKind.WholeDevice;
        WaveFormat = device.WaveFormat;
        SourceName = deviceName;
        device.DataAvailable += (_, e) => DataAvailable?.Invoke(this, e);
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public WaveFormat WaveFormat { get; }

    public DesktopCaptureKind Kind { get; }

    /// <summary>What is captured, for display.</summary>
    public string SourceName { get; }

    /// <summary>Opens the capture. Tries process loopback first and falls back to device loopback.</summary>
    /// <param name="target">A single app or the whole desktop.</param>
    /// <param name="fallbackDeviceId">Device for the fallback (usually the headphones).</param>
    /// <param name="note">Hint for the user when only the fallback worked.</param>
    public static DesktopCapture Open(DesktopCaptureTarget target, string? fallbackDeviceId, out string? note)
    {
        note = null;

        try
        {
            return new DesktopCapture(new ProcessLoopbackCapture(AudioFormat.Engine, target.ProcessId), target);
        }
        catch (Exception ex)
        {
            note = ex.Message;
        }

        // A single app can only be captured via process loopback. The fallback only knows whole
        // devices and would be wrong here.
        if (target.ProcessId is not null)
            throw new InvalidOperationException(note);

        // Fallback: capture the whole device. Works everywhere but includes your own output.
        var device = AudioDevices.Resolve(fallbackDeviceId, capture: false)
                     ?? throw new InvalidOperationException("No playback device found for capture.");

        try
        {
            var capture = new WasapiLoopbackCapture(device);
            capture.StartRecording();
            note = $"Process loopback unavailable ({note}). \"{device.FriendlyName}\" is captured completely; " +
                   "your own sounds arrive twice.";
            return new DesktopCapture(capture, device.FriendlyName);
        }
        catch (Exception)
        {
            device.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _process?.Dispose();

        if (_device is not null)
        {
            try { _device.StopRecording(); } catch (Exception) { }
            try { _device.Dispose(); } catch (Exception) { }
        }
    }
}

/// <summary>
/// WASAPI process loopback. Captures either exactly one app's audio or all apps except this one; same
/// interface, different mode.
/// <para>
/// Runs entirely on its own MTA thread: activation calls its completion handler from another thread, and
/// without MTA COM would marshal the call into the message loop being waited on. A deadlock.
/// </para>
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualDevicePath = "VAD\\Process_Loopback";

    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const int BufferFlagsSilent = 0x1;

    /// <summary>Capture client buffer size. Pure reserve, no latency.</summary>
    private const long BufferDuration = 200 * 10_000L;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly WaveFormat _format;
    private readonly int? _targetProcessId;

    private volatile bool _stop;
    private Exception? _startError;
    private byte[] _scratch = [];

    /// <param name="targetProcessId">
    /// Process tree whose audio is sent. <c>null</c> inverts the selection: everything <b>except</b> the own
    /// process tree.
    /// </param>
    public ProcessLoopbackCapture(WaveFormat format, int? targetProcessId)
    {
        _format = format;
        _targetProcessId = targetProcessId;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WinTo desktop loopback",
            // Slightly above normal: if the thread stalls, the audio gets holes.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _stop = true;
            throw new TimeoutException("Windows did not provide the system audio capture in time.");
        }

        if (_startError is not null)
        {
            _stop = true;
            throw _startError;
        }
    }

    public WaveFormat WaveFormat => _format;

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public void Dispose()
    {
        _stop = true;
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _ready.Dispose();
    }

    // ================= The capture thread =================

    private void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        AutoResetEvent? pulse = null;

        try
        {
            client = Activate(_targetProcessId);

            var sessionGuid = Guid.Empty;
            var hr = client.Initialize(
                ShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                BufferDuration,
                0,
                _format,
                ref sessionGuid);

            if (hr < 0) throw new COMException($"The system audio capture rejected the format (0x{hr:X8}).", hr);

            pulse = new AutoResetEvent(false);
            Check(client.SetEventHandle(pulse.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Check(client.GetService(IidAudioCaptureClient, out var service), "GetService");

            capture = (IAudioCaptureClient)service;
            Check(client.Start(), "Start");
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            Release(ref capture, ref client, pulse);
            return;
        }

        _ready.Set();

        try
        {
            while (!_stop)
            {
                // A short wait instead of infinite: with no program playing, the event never comes, and
                // Stop must still be handled.
                pulse!.WaitOne(100);
                Drain(capture!);
            }
        }
        catch (Exception)
        {
            // Device change while running; the capture ends quietly.
        }
        finally
        {
            try { client!.Stop(); } catch (Exception) { }
            Release(ref capture, ref client, pulse);
        }
    }

    /// <summary>Fetches all pending packets. One event can mean several.</summary>
    private void Drain(IAudioCaptureClient capture)
    {
        var blockAlign = _format.BlockAlign;

        while (!_stop)
        {
            if (capture.GetNextPacketSize(out var pending) < 0 || pending == 0) return;
            if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0) return;

            try
            {
                var bytes = (int)frames * blockAlign;
                if (bytes <= 0) continue;

                if (_scratch.Length < bytes) _scratch = new byte[bytes];

                // When Windows reports silence, the pointer isn't necessarily valid.
                if ((flags & BufferFlagsSilent) != 0) Array.Clear(_scratch, 0, bytes);
                else Marshal.Copy(data, _scratch, 0, bytes);

                DataAvailable?.Invoke(this, new WaveInEventArgs(_scratch, bytes));
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
        }
    }

    /// <summary>
    /// Activates an <c>IAudioClient</c> on the process loopback pseudo device. Activation is asynchronous; it's
    /// reduced to a synchronous result here because the thread exists only for this.
    /// </summary>
    private static IAudioClient Activate(int? targetProcessId)
    {
        // Two modes of the same interface: capture exactly this process tree, or exclude exactly
        // this one and capture everything else.
        var parameters = new ActivationParams
        {
            ActivationType = 1,                          // PROCESS_LOOPBACK
            TargetProcessId = (uint)(targetProcessId ?? Environment.ProcessId),
            LoopbackMode = targetProcessId is null
                ? 1                                      // EXCLUDE_TARGET_PROCESS_TREE
                : 0,                                     // INCLUDE_TARGET_PROCESS_TREE
        };

        var blob = IntPtr.Zero;
        var variant = IntPtr.Zero;
        try
        {
            var size = Marshal.SizeOf<ActivationParams>();
            blob = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(parameters, blob, false);

            variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            Marshal.StructureToPtr(
                new PropVariantBlob { VarType = 0x41 /* VT_BLOB */, Size = size, Data = blob },
                variant,
                false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevicePath, IidAudioClient, variant, handler, out var operation);

            if (!handler.Done.Wait(TimeSpan.FromSeconds(4)))
                throw new TimeoutException("Windows did not confirm the system audio capture.");

            var hr = operation.GetActivateResult(out var activateResult, out var activated);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            if (activateResult < 0)
            {
                // Most common case: the system is older than Windows 10 build 20348.
                throw new COMException(
                    $"System audio capture unavailable (0x{activateResult:X8}). " +
                    "Requires Windows 10 build 20348 or later, or Windows 11.",
                    activateResult);
            }

            return (IAudioClient)activated;
        }
        finally
        {
            if (variant != IntPtr.Zero) Marshal.FreeHGlobal(variant);
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"System audio capture: {what} failed (0x{hr:X8}).", hr);
    }

    private static void Release(ref IAudioCaptureClient? capture, ref IAudioClient? client, AutoResetEvent? pulse)
    {
        if (capture is not null)
        {
            try { Marshal.ReleaseComObject(capture); } catch (Exception) { }
            capture = null;
        }

        if (client is not null)
        {
            try { Marshal.ReleaseComObject(client); } catch (Exception) { }
            client = null;
        }

        pulse?.Dispose();
    }

    // ================= COM interop =================

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS; the union is reduced to its only case here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    /// <summary>PROPVARIANT, cut to the BLOB variant.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public int Size;
        public IntPtr Data;
    }

    /// <summary>Marker without methods. Tells COM the object may be called directly across threads; otherwise COM tries to marshal.</summary>
    [ComImport]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Done.Set();
            return 0;
        }
    }

    /// <remarks>The method order mirrors the vtable and is binding.</remarks>
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            [In] WaveFormat format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, [In] WaveFormat format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
