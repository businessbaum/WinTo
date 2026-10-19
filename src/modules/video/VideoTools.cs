using System.Globalization;
using System.Text.Json;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Video;

/// <summary>What ffprobe says about a video.</summary>
public sealed record VideoInfo(
    TimeSpan Duration, int Width, int Height, double Fps, string? VideoCodec, string? AudioCodec, long Bytes, long BitRate)
{
    public bool HasAudio => AudioCodec is not null;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Width > 0) parts.Add($"{Width} × {Height}");
            if (Fps > 0) parts.Add($"{Fps:0.##} fps");
            if (VideoCodec is not null) parts.Add(VideoCodec.ToUpperInvariant());
            parts.Add(AudioCodec is null ? "no sound" : AudioCodec.ToUpperInvariant());
            parts.Add(Humanize.Bytes(Bytes));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>What to make of the video.</summary>
public sealed record ExportPlan(
    TimeSpan Start,
    TimeSpan End,
    string Format,
    int Height,
    int TargetMegabytes,
    bool RemoveAudio,
    int Rotation,
    double Speed,
    bool ExactCut);

/// <summary>ffprobe for the facts, ffmpeg for every export.</summary>
/// <remarks>
/// <para>
/// <b>Copying where possible.</b> A trim to the same format with nothing else changed copies the streams: done
/// in a second, no quality loss, but cut at the nearest keyframe before the start. "Exact cut" re-encodes.
/// </para>
/// <para>
/// <b>A target size takes two passes</b>: the first measures, the second spends the bit rate where the video
/// needs it. One pass at a fixed rate misses the size or wastes it. The size counts audio at 128 kbit/s and
/// keeps 3 % for the container.
/// </para>
/// <para>
/// <b>GIF with its own palette</b> (<c>palettegen</c>/<c>paletteuse</c>): the 256 colors are chosen for this
/// clip, not the web palette, and 15 fps at most 640 pixels wide keep it shareable.
/// </para>
/// </remarks>
internal static class VideoTools
{
    public static readonly string[] Extensions =
        ["mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "ts", "m2ts", "mts", "3gp", "ogv"];

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    public static async Task<VideoInfo?> ProbeAsync(string ffprobe, string path, CancellationToken cancel)
    {
        var result = await ExternalProcess.RunAsync(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], cancellationToken: cancel);
        if (!result.Ok) return null;

        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;

        string? videoCodec = null, audioCodec = null;
        int width = 0, height = 0;
        double fps = 0;

        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;

            if (type == "video" && videoCodec is null && codec is not "mjpeg" and not "png")
            {
                videoCodec = codec;
                width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                fps = Rate(stream.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() : null);

                // Phone videos stored sideways with a rotation flag: report them upright.
                if (stream.TryGetProperty("side_data_list", out var side))
                {
                    foreach (var data in side.EnumerateArray())
                        if (data.TryGetProperty("rotation", out var rot) && Math.Abs(rot.GetDouble()) % 180 == 90)
                            (width, height) = (height, width);
                }
            }
            else if (type == "audio" && audioCodec is null)
            {
                audioCodec = codec;
            }
        }

        var format = root.GetProperty("format");
        var duration = Seconds(format, "duration");
        var bytes = format.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out var b) ? b : new FileInfo(path).Length;
        var bitRate = format.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var bits) ? bits : 0;

        return new VideoInfo(TimeSpan.FromSeconds(duration), width, height, fps, videoCodec, audioCodec, bytes, bitRate);
    }

    private static double Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;

    private static double Rate(string? fraction)
    {
        if (fraction?.Split('/') is not [var n, var d]) return 0;
        return double.TryParse(n, CultureInfo.InvariantCulture, out var num) && double.TryParse(d, CultureInfo.InvariantCulture, out var den) && den > 0
            ? num / den
            : 0;
    }

    /// <summary>The file extension a format writes.</summary>
    public static string ExtensionFor(string format, string source) => format switch
    {
        "mp4" => ".mp4",
        "webm" => ".webm",
        "gif" => ".gif",
        "mp3" => ".mp3",
        "m4a" => ".m4a",
        _ => Path.GetExtension(source),
    };

    /// <summary>Whether the plan can copy the streams: only cutting, same container.</summary>
    public static bool CanCopy(ExportPlan plan) =>
        plan.Format == "same" && plan.Height == 0 && plan.TargetMegabytes == 0 && plan.Rotation == 0
        && Math.Abs(plan.Speed - 1) < 0.001 && !plan.ExactCut;

    /// <summary>One or two ffmpeg runs (two for a target size). Every run writes progress to stdout.</summary>
    public static List<List<string>> Commands(ExportPlan plan, VideoInfo info, string source, string target, string passLog)
    {
        var length = plan.End - plan.Start;
        var head = new List<string> { "-y", "-hide_banner", "-nostats", "-progress", "pipe:1" };

        // Both before -i: seeking there is fast and frame accurate when re-encoding, and -t limits what is read.
        // After -i, -t would limit the output, which at double speed is half as long as what it shows.
        if (plan.Start > TimeSpan.Zero) head.AddRange(["-ss", Format(plan.Start)]);
        if (length < info.Duration - TimeSpan.FromMilliseconds(50)) head.AddRange(["-t", Format(length)]);
        head.AddRange(["-i", source]);

        if (CanCopy(plan))
        {
            var copy = new List<string>(head) { "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero" };
            if (plan.RemoveAudio) copy.Add("-an");
            copy.Add(target);
            return [copy];
        }

        var video = new List<string>();
        var audio = new List<string>();

        if (plan.Rotation == 90) video.Add("transpose=1");
        else if (plan.Rotation == 270) video.Add("transpose=2");
        else if (plan.Rotation == 180) video.Add("transpose=1,transpose=1");

        if (Math.Abs(plan.Speed - 1) > 0.001)
        {
            video.Add($"setpts={(1 / plan.Speed).ToString("0.####", CultureInfo.InvariantCulture)}*PTS");
            audio.Add($"atempo={plan.Speed.ToString("0.####", CultureInfo.InvariantCulture)}");
        }

        switch (plan.Format)
        {
            case "mp3":
                return [[.. head, "-vn", .. AudioFilter(audio), "-c:a", "libmp3lame", "-q:a", "2", target]];

            case "m4a":
                return [[.. head, "-vn", .. AudioFilter(audio), "-c:a", "aac", "-b:a", "192k", target]];

            case "gif":
            {
                var width = Math.Min(640, info.Width > 0 ? info.Width : 640);
                var chain = string.Join(",", video.Append("fps=15").Append($"scale={width}:-2:flags=lanczos"));
                return [[.. head, "-an", "-filter_complex", $"[0:v]{chain},split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0", target]];
            }
        }

        if (plan.Height > 0 && plan.Height < info.Height) video.Add($"scale=-2:{plan.Height}");

        var webm = plan.Format == "webm" || (plan.Format == "same" && Path.GetExtension(source).Equals(".webm", StringComparison.OrdinalIgnoreCase));
        var codec = webm
            ? new List<string> { "-c:v", "libvpx-vp9", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2" }
            : new List<string> { "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p" };
        var sound = plan.RemoveAudio || !info.HasAudio
            ? new List<string> { "-an" }
            : webm ? ["-c:a", "libopus", "-b:a", "128k"] : ["-c:a", "aac", "-b:a", "128k"];
        var filters = video.Count > 0 ? new List<string> { "-vf", string.Join(",", video) } : [];
        var tail = webm ? new List<string>() : ["-movflags", "+faststart"];

        if (plan.TargetMegabytes <= 0)
        {
            var quality = webm ? new List<string> { "-crf", "32", "-b:v", "0" } : ["-crf", "20"];
            return [[.. head, .. filters, .. AudioFilter(audio), .. codec, .. quality, .. sound, .. tail, target]];
        }

        // Bit rate for the size: 97 % of the budget, minus the sound, spread over the output's length.
        var seconds = Math.Max(1, length.TotalSeconds / plan.Speed);
        var budget = plan.TargetMegabytes * 8.0 * 1024 * 1024 * 0.97 / seconds;
        var audioRate = plan.RemoveAudio || !info.HasAudio ? 0 : 128_000;
        var videoRate = Math.Max(100_000, (long)(budget - audioRate));
        var rate = new List<string> { "-b:v", videoRate.ToString(CultureInfo.InvariantCulture) };

        var first = new List<string>(head);
        first.AddRange(filters);
        first.AddRange(codec);
        first.AddRange(rate);
        first.AddRange(["-pass", "1", "-passlogfile", passLog, "-an", "-f", "null", "NUL"]);

        var second = new List<string>(head);
        second.AddRange(filters);
        second.AddRange(AudioFilter(audio));
        second.AddRange(codec);
        second.AddRange(rate);
        second.AddRange(["-pass", "2", "-passlogfile", passLog]);
        second.AddRange(sound);
        second.AddRange(tail);
        second.Add(target);

        return [first, second];
    }

    private static List<string> AudioFilter(List<string> audio) => audio.Count > 0 ? ["-af", string.Join(",", audio)] : [];

    public static string Format(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>"00:01:23.456" from a progress line like <c>out_time_us=83456000</c>; <c>null</c> for other lines.</summary>
    public static TimeSpan? ProgressTime(string line)
    {
        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return null;
        return long.TryParse(line.AsSpan(12), out var micro) && micro >= 0 ? TimeSpan.FromTicks(micro * 10) : null;
    }
}
