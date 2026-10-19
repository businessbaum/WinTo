using System.Globalization;
using System.Text.RegularExpressions;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Capture.Recording;

/// <param name="Name">The name DirectShow reports, exactly as it must appear on the command line.</param>
public sealed record AudioDevice(string Name)
{
    /// <summary>Whether the name sounds like a loopback, i.e. what the computer plays.</summary>
    /// <remarks>A guess: DirectShow doesn't say whether a device is a loopback. Only used for preselection.</remarks>
    public bool LooksLikeLoopback =>
        Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Stereo", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Stereomix", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("What U Hear", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An encoder as listed in the picker.</summary>
/// <param name="Id">The name ffmpeg understands.</param>
/// <param name="Label">The human-readable name.</param>
/// <param name="Hint">One sentence on when it's the right choice.</param>
/// <remarks>
/// Public and not nested in <c>Capture</c>: the view model is public, <c>Capture</c> internal, and
/// [ObservableProperty] generates a public property of this type.
/// </remarks>
public sealed record EncoderChoice(string Id, string Label, string Hint);

/// <param name="Bounds">Pixels in the virtual desktop. Empty means everything.</param>
public sealed record CaptureArea(string Name, int X, int Y, int Width, int Height)
{
    public bool IsWholeDesktop => Width == 0 && Height == 0;

    public static readonly CaptureArea WholeDesktop = new("All screens", 0, 0, 0, 0);
}

/// <summary>System audio as an ffmpeg input.</summary>
/// <remarks>
/// Not a DirectShow device but raw audio from a named pipe, filled by <see cref="SystemAudio"/> via WASAPI
/// loopback. So ffmpeg needs rate, channels and format explicitly; a raw stream reveals none of them.
/// </remarks>
public sealed record SystemAudioInput(
    string PipePath, string SampleFormat, int SampleRate, int Channels, string DeviceName);

/// <summary>How audio sources are leveled against each other.</summary>
public enum LevelMode
{
    /// <summary>Each source as loud as it arrives.</summary>
    Off,

    /// <summary>Each source brought to the same loudness before mixing.</summary>
    Match,
}

public sealed record CaptureSettings
{
    public CaptureArea Area { get; init; } = CaptureArea.WholeDesktop;
    public int FramesPerSecond { get; init; } = 30;

    /// <summary>Constant quality: lower is better. 23 is the usual compromise.</summary>
    public int Quality { get; init; } = 23;

    public string Encoder { get; init; } = "libx264";
    public bool CaptureCursor { get; init; } = true;

    /// <summary>DirectShow recording devices: microphones and driver loopbacks.</summary>
    public IReadOnlyList<string> AudioDevices { get; init; } = [];

    /// <summary><c>null</c> if system audio isn't recorded.</summary>
    public SystemAudioInput? SystemAudio { get; init; }

    /// <summary>How many audio inputs end up mixed.</summary>
    public int AudioInputCount => AudioDevices.Count + (SystemAudio is null ? 0 : 1);

    /// <summary>Whether microphone and system audio are brought to the same loudness.</summary>
    public LevelMode Levels { get; init; } = LevelMode.Off;

    /// <summary>Gain adjustment for recording devices in dB.</summary>
    public double DeviceGainDb { get; init; }

    /// <summary>Gain adjustment for system audio in dB.</summary>
    public double SystemGainDb { get; init; }
}

/// <summary>Builds the ffmpeg command lines and queries what the computer can do.</summary>
/// <remarks>
/// <para>
/// <b>Why ffmpeg alone doesn't get system audio.</b> Windows has no DirectShow source for "what comes out
/// of the speakers", only recording devices plus whatever loopbacks a driver adds (Stereo Mix on some
/// Realtek drivers, VB-CABLE, Voicemeeter). Without one you record your microphone and notice only on
/// playback; measured here as a complete track at <b>−67.8 dB</b>.
/// </para>
/// <para>
/// <b>So system audio doesn't come from ffmpeg.</b> <see cref="SystemAudio"/> captures it via WASAPI
/// loopback and feeds it through a named pipe as another input: a raw stream without timestamps whose
/// rate and channels must be declared, and whose output format must be pinned, or the playback device
/// decides what the file looks like.
/// </para>
/// <para>
/// <b>Why gdigrab and not ddagrab.</b> ddagrab is much faster (Desktop Duplication straight from GPU memory)
/// but needs a D3D11 device and either a hardware encoder or an explicit download to system memory; if
/// either fails there's no image instead of a slow one. gdigrab works everywhere.
/// </para>
/// </remarks>
internal static partial class Capture
{
    /// <summary>Asks ffmpeg for the DirectShow recording devices.</summary>
    /// <remarks>
    /// The list goes to <c>stderr</c> and the call ends with an error code; both are normal for
    /// <c>-list_devices</c> because ffmpeg can't open the "dummy" input afterwards. The exit code is ignored.
    /// </remarks>
    public static async Task<List<AudioDevice>> ListAudioDevicesAsync(
        string ffmpeg, CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(ffmpeg,
            ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"],
            cancellationToken: cancellationToken);

        var devices = new List<AudioDevice>();
        var inAudioSection = false;

        foreach (var line in result.Error.Split('\n'))
        {
            // Two output forms depending on ffmpeg version: older ones group under a heading, newer
            // ones append the kind to each line. Both are read.
            if (line.Contains("DirectShow audio devices", StringComparison.Ordinal)) { inAudioSection = true; continue; }
            if (line.Contains("DirectShow video devices", StringComparison.Ordinal)) { inAudioSection = false; continue; }

            // The alternative name is the internal id. It works too but is an unreadable GUID string.
            if (line.Contains("Alternative name", StringComparison.Ordinal)) continue;

            var match = DeviceLine().Match(line);
            if (!match.Success) continue;

            var kind = match.Groups["kind"];

            // Newer ffmpeg names the kind per line: "(audio)", "(video)", and "(none)" for OBS's
            // virtual camera. Older versions group under the heading instead.
            var isAudio = kind.Success
                ? kind.Value.Equals("audio", StringComparison.OrdinalIgnoreCase)
                : inAudioSection;

            if (!isAudio) continue;

            var name = match.Groups["name"].Value;
            if (devices.All(d => d.Name != name)) devices.Add(new AudioDevice(name));
        }

        return devices;
    }

    /// <summary>The encoders this computer can actually use.</summary>
    /// <remarks>
    /// <para>
    /// <b>Why tried instead of queried.</b> <c>ffmpeg -encoders</c> lists what is <i>compiled in</i>, not what the
    /// hardware can do. h264_nvenc was listed here and the first recording ended with "No capable devices
    /// found" and an empty file.
    /// </para>
    /// <para>
    /// Encoding a single synthetic frame to <c>null</c> answers correctly and costs a few hundred ms per candidate.
    /// </para>
    /// </remarks>
    public static async Task<List<string>> WorkingEncodersAsync(
        string ffmpeg, CancellationToken cancellationToken, bool refresh = false)
    {
        // Once per session per ffmpeg file and timestamp. The check starts up to four ffmpeg
        // processes and used to rerun on every view build; the answer only changes with ffmpeg or
        // the graphics driver. "Rescan" covers the latter.
        var key = CacheKey(ffmpeg);
        if (!refresh && key is not null && EncoderCache.TryGetValue(key, out var cached)) return [.. cached];

        var working = await ProbeEncodersAsync(ffmpeg, cancellationToken);
        if (key is not null) EncoderCache[key] = [.. working];
        return working;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> EncoderCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string? CacheKey(string ffmpeg)
    {
        try
        {
            return $"{ffmpeg}|{File.GetLastWriteTimeUtc(ffmpeg).Ticks}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<List<string>> ProbeEncodersAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        var listed = await ExternalProcess.RunAsync(ffmpeg,
            ["-hide_banner", "-encoders"], cancellationToken: cancellationToken);

        var text = listed.Output + listed.Error;
        var working = new List<string>();

        foreach (var candidate in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            if (!text.Contains(candidate, StringComparison.Ordinal)) continue;
            if (await CanEncodeAsync(ffmpeg, candidate, 640, 480, cancellationToken)) working.Add(candidate);
        }

        // The software encoder is always available and always last: the fallback, not the first choice.
        working.Add("libx264");
        return working;
    }

    /// <summary>Encodes a single black frame at this size and checks whether it works.</summary>
    /// <remarks>
    /// <para>
    /// <b>The size matters, not just the encoder name.</b> Measured: h264_nvenc passes at 640×480 and rejects
    /// the same recording across two 1440p screens side by side, wider than H.264 supports on that card.
    /// </para>
    /// <para>
    /// Without this check the module fell back to libx264, which can't come close to that size at 30 fps on
    /// the CPU. The result was a recording that runs and produces nothing: no finished frame, counter at
    /// 0:00, and only "real-time buffer too full" in the log.
    /// </para>
    /// </remarks>
    public static async Task<bool> CanEncodeAsync(
        string ffmpeg, string encoder, int width, int height, CancellationToken cancellationToken)
    {
        // yuv420p can't do odd dimensions; that would be a different error than the one tested.
        var w = Math.Max(2, width - (width % 2));
        var h = Math.Max(2, height - (height % 2));

        try
        {
            var result = await ExternalProcess.RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", $"color=c=black:s={w}x{h}:r=30",
                "-frames:v", "1",
                "-c:v", encoder,
                "-pix_fmt", "yuv420p",

                // To null: the question is whether the encoder starts, not a file.
                "-f", "null", "-",
            ], cancellationToken: cancellationToken);

            return result.Ok;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether ffmpeg reports that the computer can't keep up.</summary>
    /// <remarks>
    /// The dshow input fills a ring buffer and drops once the encoder is too slow. If that's in the log, the
    /// recording runs but produces nothing usable, indistinguishable from a hang from the outside.
    /// </remarks>
    public static bool LooksLikeOverload(string line) =>
        line.Contains("too full", StringComparison.OrdinalIgnoreCase)
        || line.Contains("frame dropped", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether error output looks like the encoder itself refused.</summary>
    /// <remarks>
    /// For when the preflight passed but the real run fails anyway: a GPU taken by a game meanwhile, a driver
    /// that just reloaded. Then it switches to the CPU instead of leaving an empty file.
    /// </remarks>
    public static bool LooksLikeEncoderProblem(IEnumerable<string> lines) =>
        lines.Any(line =>
            line.Contains("No capable devices", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Cannot load nvcuda", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Could not open encoder", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Error initializing output stream", StringComparison.OrdinalIgnoreCase));

    /// <summary>When each encoder is the right choice. One sentence, not three.</summary>
    public static string EncoderHint(string encoder) => encoder switch
    {
        "h264_nvenc" => "Dedicated chip on the graphics card. Minimal load, slightly larger file. First choice in games.",
        "h264_qsv" => "Dedicated chip on Intel graphics. Minimal load, slightly larger file. First choice in games.",
        "h264_amf" => "Dedicated chip on AMD graphics. Minimal load, slightly larger file. First choice in games.",
        _ => "Runs on the CPU. Smallest file at equal quality, but high load.",
    };


    /// <summary>Builds the picker entry.</summary>
    public static EncoderChoice ChoiceFor(string id) => new(id, EncoderLabel(id), EncoderHint(id));

    public static string EncoderLabel(string encoder) => encoder switch
    {
        "h264_nvenc" => "NVIDIA (Hardware)",
        "h264_qsv" => "Intel Quick Sync (Hardware)",
        "h264_amf" => "AMD (Hardware)",
        _ => "libx264 (CPU)",
    };

    // ------------------------------------------------------------------ Arguments

    /// <summary>The shared input and encoder part.</summary>
    private static List<string> Common(CaptureSettings settings)
    {
        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "error",

            // Without -stats ffmpeg writes no progress line at -loglevel error, and the display
            // would have nothing to show.
            "-stats",

            // The buffer between reader and encoder, per input.
            //
            // With several live sources (screen, microphone, system audio) three readers feed one
            // encoder. When its queue is full ffmpeg reports "Thread message queue blocking" and drops
            // packets: dropouts in audio, stutter in video. The default is 8 packets. The memory cost
            // is fractions of a second and well spent here.
            "-thread_queue_size", "1024",

            "-f", "gdigrab",
            "-framerate", settings.FramesPerSecond.ToString(),
            "-draw_mouse", settings.CaptureCursor ? "1" : "0",
        ];

        if (!settings.Area.IsWholeDesktop)
        {
            arguments.AddRange([
                "-offset_x", settings.Area.X.ToString(),
                "-offset_y", settings.Area.Y.ToString(),
                "-video_size", $"{settings.Area.Width}x{settings.Area.Height}",
            ]);
        }

        arguments.AddRange(["-i", "desktop"]);

        foreach (var device in settings.AudioDevices)
            arguments.AddRange(["-thread_queue_size", "1024", "-f", "dshow", "-i", $"audio={EscapeDevice(device)}"]);

        // System audio is a raw stream from a named pipe and comes after the DirectShow devices;
        // the input numbers below count in this order.
        if (settings.SystemAudio is { } system)
        {
            arguments.AddRange([
                "-thread_queue_size", "1024",
                "-f", system.SampleFormat,
                "-ar", system.SampleRate.ToString(),
                "-ac", system.Channels.ToString(),
                "-i", system.PipePath,
            ]);
        }

        var audioCount = settings.AudioInputCount;

        // Several audio sources (microphone and system audio) are mixed into one track. Two
        // separate tracks would be cleaner, but most players only play the first, losing half the audio.
        if (audioCount > 1)
        {
            // Every input is brought to the same target BEFORE mixing.
            //
            // The reason is a measured bug: without these steps libavfilter negotiates the common
            // format itself, and the first input wins. DirectShow devices come before the system audio
            // pipe, so the microphone always decided how system audio sounded. A 44.1 kHz headset mic
            // downsampled the 48 kHz system audio live, and a mono microphone would have folded it to
            // one channel. None of this reports an error.
            var chain = string.Concat(Enumerable.Range(1, audioCount).Select(i =>
                $"[{i}:a]{AudioChain(settings, isSystem: i == audioCount && settings.SystemAudio is not null)}[a{i}];"));

            var inputs = string.Concat(Enumerable.Range(1, audioCount).Select(i => $"[a{i}]"));

            // dropout_transition=0: otherwise amix fades a source that briefly delivers nothing down
            // and up again over seconds; a microphone in a pause sounds like the volume drifts by itself.
            // normalize=0: otherwise amix divides every source by their count, and two sources make
            // everything half as loud.
            arguments.AddRange([
                "-filter_complex",
                $"{chain}{inputs}amix=inputs={audioCount}:duration=longest:dropout_transition=0:normalize=0"
                + $"{MixTail(settings)}[a]",
                "-map", "0:v", "-map", "[a]",
            ]);
        }
        else if (audioCount == 1)
        {
            // Even with a single source: the gain adjustment should apply, and without a filter graph
            // there's nowhere for it to act.
            var single = AudioChain(settings, isSystem: settings.SystemAudio is not null);

            if (single.Length > 0)
            {
                arguments.AddRange([
                    "-filter_complex", $"[1:a]{single}{MixTail(settings)}[a]",
                    "-map", "0:v", "-map", "[a]",
                ]);
            }
            else
            {
                arguments.AddRange(["-map", "0:v", "-map", "1:a"]);
            }
        }

        arguments.AddRange(["-c:v", settings.Encoder]);

        // Every encoder names its quality knob differently, and none understands the others'.
        arguments.AddRange(settings.Encoder switch
        {
            "h264_nvenc" => ["-preset", "p4", "-rc", "vbr", "-cq", settings.Quality.ToString()],
            "h264_qsv" => ["-preset", "veryfast", "-global_quality", settings.Quality.ToString()],
            "h264_amf" => ["-quality", "speed", "-rc", "cqp", "-qp_i", settings.Quality.ToString(),
                           "-qp_p", settings.Quality.ToString()],
            _ => new[] { "-preset", "veryfast", "-crf", settings.Quality.ToString() },
        });

        // yuv420p although the screen offers more: Windows Media Player plays nothing else and
        // Discord won't upload it.
        arguments.AddRange(["-pix_fmt", "yuv420p"]);

        if (audioCount > 0)
        {
            // Stereo and 48 kHz are pinned, not decided by the device.
            //
            // <b>Measured too.</b> WASAPI delivers the mix in the endpoint's format, and the default
            // playback device here reported <b>8 channels</b>. Without this line the result was a 7.1
            // track at 160 kbit/s, 20 kbit/s per channel: poor sound, and unwelcome exactly where
            // recordings go. Same reasoning as -pix_fmt yuv420p above.
            //
            // The first attempt missed this because it was tested against the virtual cable, which has
            // two channels.
            arguments.AddRange(["-c:a", "aac", "-b:a", "160k", "-ac", "2", "-ar", "48000"]);
        }

        return arguments;
    }

    /// <summary>Escapes the characters at which the dshow input splits a device name.</summary>
    /// <remarks>
    /// The name is passed as ONE argument (ArgumentList), but ffmpeg splits the string again: a colon
    /// separates options, a backslash escapes. A device called "Line In (2:0)" would be unfindable, reported
    /// as "Could not find audio device", which looks like an unplugged device and isn't.
    /// </remarks>
    private static string EscapeDevice(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

    /// <summary>A plain recording to a file.</summary>
    public static List<string> ForRecording(CaptureSettings settings, string outputPath)
    {
        var arguments = Common(settings);

        // A keyframe every two seconds. Otherwise libx264 uses its default interval of 250 frames,
        // over eight seconds at 30 fps, and seeking jumps that far.
        arguments.AddRange(["-g", (settings.FramesPerSecond * 2).ToString()]);

        // Matroska, not MP4. An MP4 is only complete when closed, its index is at the end; a hard
        // abort leaves it unusable. Matroska writes continuously and stays playable at any moment.
        arguments.AddRange(["-y", outputPath]);
        return arguments;
    }

    /// <summary>Loudness target in LUFS.</summary>
    /// <remarks>
    /// <b>Lower than the target for the final mix by what mixing adds back.</b> Two equally loud sources sum to
    /// about 3 dB more; with <c>normalize=0</c> <c>amix</c> adds and doesn't divide. At −21 per source the mix
    /// lands near −18, the usual value for game recordings.
    /// </remarks>
    private const string TargetLufs = "-21";

    /// <summary>The filter steps of a single audio input.</summary>
    /// <remarks>
    /// <para>
    /// <b>Why <c>loudnorm</c> and not <c>dynaudnorm</c>.</b> Measured on two sources 28.6 dB apart:
    /// <c>loudnorm</c> to a common target leaves <b>0.0 dB difference</b>, <c>dynaudnorm</c> leaves <b>19.3 dB</b>.
    /// <c>dynaudnorm</c> evens out swings <i>within</i> a source and has no absolute target.
    /// </para>
    /// <para>
    /// <c>loudnorm</c> has a lookahead buffer but doesn't shift the time axis: an onset at 2.000042 s was at
    /// 2.000021 s afterwards; ffmpeg compensates via timestamps. Essential when audio and video must match.
    /// </para>
    /// </remarks>
    private static string AudioChain(CaptureSettings settings, bool isSystem)
    {
        var parts = new List<string>
        {
            "aresample=48000",
            "aformat=sample_fmts=fltp:channel_layouts=stereo",
        };

        if (settings.Levels == LevelMode.Match)
            parts.Add($"loudnorm=I={TargetLufs}:TP=-2:LRA=11");

        // The gain adjustment comes AFTER leveling: it shifts the result, not the input, or
        // loudnorm would cancel it out.
        var gain = isSystem ? settings.SystemGainDb : settings.DeviceGainDb;

        if (Math.Abs(gain) >= 0.05)
            parts.Add($"volume={gain.ToString("0.#", CultureInfo.InvariantCulture)}dB");

        return string.Join(',', parts);
    }

    /// <summary>What follows the mix.</summary>
    /// <remarks>
    /// A limiter, but only with leveling on: each source is raised to its target, and two simultaneous peaks
    /// can exceed the limit together. Without leveling the audio stays untouched; an unrequested limiter
    /// changes the sound.
    /// </remarks>
    private static string MixTail(CaptureSettings settings) =>
        settings.Levels == LevelMode.Match ? ",alimiter=limit=0.95:level=disabled" : "";

    /// <summary>The ring buffer: continuous short segments that overwrite each other.</summary>
    /// <param name="segmentSeconds">Length of one segment.</param>
    /// <param name="keyframeSeconds">
    /// Keyframe interval. Must divide <paramref name="segmentSeconds"/>, otherwise none falls on the
    /// segment boundary and segments get too long.
    /// </param>
    /// <param name="wrap">How many segments run in the circle.</param>
    /// <remarks>
    /// How recording apps build instant replay. A growing file trimmed at the end would fill the disk
    /// when left running for an hour.
    /// </remarks>
    public static List<string> ForBuffer(
        CaptureSettings settings, string directory, int segmentSeconds, int keyframeSeconds, int wrap)
    {
        var arguments = Common(settings);

        // The segment muxer can only split at a keyframe. libx264 sets one every 250 frames by
        // default, over eight seconds at 30 fps. Without the next two lines "-segment_time 2"
        // silently becomes something else: measured segments of 16.7 s, a ring holding a multiple
        // of the set time, and clips wrong at both ends. None of it reports an error.
        //
        // The interval is smaller than a segment and divides it. Second purpose: when trimming
        // with -c copy ffmpeg can only start at a keyframe and delivers up to one interval too
        // much. Smaller intervals mean more precise clips at a slight size cost.
        //
        // -g and the expression together: -g is only a hint for some hardware encoders, while
        // -force_key_frames acts before the encoder and works everywhere.
        arguments.AddRange([
            "-g", (settings.FramesPerSecond * keyframeSeconds).ToString(),
            "-force_key_frames", $"expr:gte(t,n_forced*{keyframeSeconds})",
        ]);

        arguments.AddRange([
            "-f", "segment",
            "-segment_time", segmentSeconds.ToString(),
            "-segment_wrap", wrap.ToString(),
            "-segment_format", "matroska",

            // Without this timestamps continue over the whole run, and a segment joined later would
            // start at minute 47.
            "-reset_timestamps", "1",

            "-y", Path.Combine(directory, "buffer%03d.mkv"),
        ]);

        return arguments;
    }

    /// <summary>Joins segments and cuts out the last minutes.</summary>
    public static List<string> ForConcat(string listFile, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-stats",
        "-f", "concat",

        // Without -safe 0 the concat demuxer rejects absolute paths.
        "-safe", "0",
        "-i", listFile,
        "-c", "copy",
        "-y", outputPath,
    ];

    /// <remarks>
    /// The result is rather <b>longer</b> than requested, never shorter: with <c>-c copy</c> the cut can only be
    /// at a keyframe, and ffmpeg takes the one before. Measured overhang is about one keyframe interval
    /// (1.1 s at a one-second interval). Re-encoding would be more precise but turn a two-second copy into
    /// minutes of work.
    /// </remarks>
    public static List<string> ForTail(string input, int seconds, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-stats",

        // -sseof counts back from the end of file and needs a known total duration, so join first,
        // then cut.
        "-sseof", $"-{seconds}",
        "-i", input,
        "-c", "copy",
        "-avoid_negative_ts", "make_zero",
        "-y", outputPath,
    ];

    // ------------------------------------------------------------------ measure

    /// <param name="MaxDb">The loudest point. <c>0</c> is full scale.</param>
    public sealed record AudioLevel(bool HasStream, double MeanDb, double MaxDb)
    {
        /// <summary>Practically silent.</summary>
        /// <remarks>
        /// The −50 dB threshold is on the <b>peak</b>, not the mean: a recording where someone coughs once isn't
        /// silent, yet its mean is low. Measured: a clip with an accidentally recorded headset microphone at
        /// −67.8 dB mean and −38.2 dB peak; a clip with proper audio at −21.1 dB mean and −18.0 dB peak.
        /// </remarks>
        public bool IsSilent => HasStream && MaxDb < -50;

        public string Text => !HasStream
            ? "no audio track"
            : IsSilent
                ? $"practically silent ({MaxDb:0} dB peak)"
                : $"audio present ({MaxDb:0} dB peak)";
    }

    /// <summary>Checks whether there really is audio in the file.</summary>
    /// <remarks>
    /// <para>
    /// <b>Measured case.</b> A clip came out with a complete AAC track, technically fine, and was silent
    /// because the headset microphone was selected instead of system audio. From the outside the file
    /// looked correct.
    /// </para>
    /// <para>
    /// Measuring afterwards costs almost nothing: <c>-vn</c> skips video, and minutes of audio take fractions
    /// of a second.
    /// </para>
    /// </remarks>
    public static async Task<AudioLevel> MeasureAudioAsync(
        string ffmpeg, string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ExternalProcess.RunAsync(ffmpeg,
            [
                "-hide_banner", "-nostats", "-i", path,

                // Without -vn the whole video would go through the decoder only to be discarded.
                "-vn", "-af", "volumedetect", "-f", "null", "-",
            ], cancellationToken: cancellationToken);

            var text = result.Error + result.Output;

            var mean = VolumeField("mean_volume").Match(text);
            var max = VolumeField("max_volume").Match(text);

            // No volumedetect result means there was no audio stream. ffmpeg reports "does not
            // contain any stream" and exits with an error; an answer here, not a problem.
            if (!mean.Success || !max.Success) return new AudioLevel(false, 0, 0);

            return new AudioLevel(
                true,
                double.Parse(mean.Groups["db"].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(max.Groups["db"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            // A failed measurement must not cast doubt on a successful recording.
            return new AudioLevel(false, 0, 0);
        }
    }

    private static Regex VolumeField(string name) =>
        new($@"\b{name}:\s*(?<db>-?\d+(?:\.\d+)?)\s*dB", RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ parse output

    /// <summary>Parses ffmpeg's progress line.</summary>
    /// <returns><c>null</c> if the line isn't one.</returns>
    /// <summary>A progress line of any kind, also one without a time yet (<c>time=N/A</c>): never a log line.</summary>
    public static bool IsProgress(string line) =>
        line.Contains("time=", StringComparison.Ordinal)
        && (line.Contains("frame=", StringComparison.Ordinal) || line.Contains("size=", StringComparison.Ordinal));

    public static (TimeSpan Time, long Bytes)? ReadStats(string line)
    {
        var match = TimeField().Match(line);
        if (!match.Success) return null;

        // Two separate patterns instead of one long one. In a combined pattern the size would be
        // an optional part, and an optional part at the start of an unanchored pattern is skipped
        // once the search succeeds further on. The size never arrived, without anything failing.
        var size = SizeField().Match(line) is { Success: true } sizeMatch
            ? long.Parse(sizeMatch.Groups["size"].Value) * 1024
            : 0;

        var time = new TimeSpan(
            0,
            int.Parse(match.Groups["h"].Value),
            int.Parse(match.Groups["m"].Value),
            int.Parse(match.Groups["s"].Value),
            (int)(double.Parse("0." + match.Groups["frac"].Value,
                System.Globalization.CultureInfo.InvariantCulture) * 1000));

        // At the very start ffmpeg sometimes reports "time=-00:00:00.03". It's a progress line
        // like any other; until 0.9.0 it didn't match and landed in the log as a message.
        if (match.Groups["neg"].Success) time = TimeSpan.Zero;

        return (time, size);
    }

    /// <remarks>
    /// Deliberately without "dshow" in the pattern. Up to ffmpeg 7 the prefix named the module, since 8 it's
    /// <c>[in#0 @ …]</c>; a pattern on <c>[dshow</c> found no device with ffmpeg 8.1.1, and the list stayed empty.
    /// Detection relies on the kind in parentheses at the end of the line.
    /// </remarks>
    [GeneratedRegex(
        @"^\s*\[[^\]]*\]\s+""(?<name>[^""]+)""\s*(?:\((?<kind>\w+)\))?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    [GeneratedRegex(
        @"\btime=(?<neg>-)?(?<h>\d+):(?<m>\d{2}):(?<s>\d{2})\.(?<frac>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimeField();

    /// <remarks>ffmpeg writes <c>Lsize=</c> instead of <c>size=</c> in the final report.</remarks>
    [GeneratedRegex(@"\bL?size=\s*(?<size>\d+)\s*(?:KiB|kB)", RegexOptions.CultureInvariant)]
    private static partial Regex SizeField();
}
