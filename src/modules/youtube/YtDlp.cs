using System.Text.Json;
using System.Text.RegularExpressions;
using MultiTool.Abstractions;

namespace MultiTool.Modules.YouTube;

/// <param name="Title">The title as the site names it.</param>
/// <param name="Entries">For a playlist its entries, otherwise empty.</param>
public sealed record MediaInfo(
    string Title,
    string? Uploader,
    TimeSpan? Duration,
    string? Thumbnail,
    IReadOnlyList<string> Entries)
{
    public bool IsPlaylist => Entries.Count > 0;

    public string DurationText => Duration is { } duration ? Humanize.Clock(duration) : "";
}

/// <param name="Fraction">0…1, or <c>null</c> while yt-dlp says nothing yet.</param>
/// <param name="Detail">"of 84 MB · 4.2 MB/s · 0:17 left".</param>
public sealed record DownloadProgress(double? Fraction, string Phase, string Detail);

/// <summary>Starts yt-dlp and understands its output.</summary>
internal static partial class YtDlp
{
    /// <summary>Finds yt-dlp.</summary>
    /// <remarks>
    /// Just forwards to <see cref="IToolLocator.Find"/>: finding and downloading belong to the shell,
    /// which knows the URL from <c>module.json</c>, asks <b>before</b> opening and puts the file in the
    /// module's data folder. What remains here is what only the module knows: how to call yt-dlp
    /// and what its output means.
    /// </remarks>
    public static string? Find(IModuleContext context) => context.Tools.Find("yt-dlp");

    /// <summary>Runs yt-dlp's own update command.</summary>
    /// <remarks>
    /// yt-dlp changes weekly because YouTube does; a three-month-old version reliably fails. The update
    /// rewrites its own file, which only works in a writable place, hence the module data folder.
    /// </remarks>
    public static async Task<ProcessResult> UpdateAsync(string exe, CancellationToken cancellationToken) =>
        await ExternalProcess.RunAsync(exe, ["-U"], cancellationToken: cancellationToken);

    public static async Task<string?> VersionAsync(string exe, CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(exe, ["--version"], cancellationToken: cancellationToken);
        return result.Ok ? result.Output.Trim() : null;
    }

    // ------------------------------------------------------------------ inspect

    /// <summary>Asks yt-dlp what is behind a URL, without downloading.</summary>
    public static async Task<MediaInfo?> InspectAsync(
        string exe, string url, string? ffmpegDirectory, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "--dump-single-json",
            "--no-warnings",

            // For a playlist only fetch the title list; resolving every video takes minutes for 200 entries.
            "--flat-playlist",

            // Everything after "--" is a URL, never an option. Otherwise a pasted "--exec=…" would be read as
            // an option, and --exec runs an arbitrary program after downloading.
            "--",
            url,
        ];

        if (ffmpegDirectory is not null) arguments.InsertRange(0, ["--ffmpeg-location", ffmpegDirectory]);

        var result = await ExternalProcess.RunAsync(exe, arguments, cancellationToken: cancellationToken);
        if (!result.Ok || result.Output.Length == 0) return null;

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;

            var entries = new List<string>();
            if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    var title = Text(entry, "title") ?? Text(entry, "id");
                    if (title is not null) entries.Add(title);
                }
            }

            var seconds = root.TryGetProperty("duration", out var duration)
                && duration.ValueKind == JsonValueKind.Number
                    ? duration.GetDouble()
                    : (double?)null;

            return new MediaInfo(
                Title: Text(root, "title") ?? url,
                Uploader: Text(root, "uploader") ?? Text(root, "channel"),
                Duration: seconds is { } value ? TimeSpan.FromSeconds(value) : null,
                Thumbnail: Text(root, "thumbnail"),
                Entries: entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ------------------------------------------------------------------ download

    public sealed record DownloadRequest(
        string Url,
        string TargetDirectory,
        bool AudioOnly,
        string AudioFormat,
        int MaxHeight,
        bool WholePlaylist,
        bool EmbedThumbnail,
        string? FfmpegDirectory);

    public static List<string> BuildArguments(DownloadRequest request)
    {
        List<string> arguments =
        [
            // Progress line by line instead of \r on one line, so the log file keeps a usable history.
            "--newline",
            "--progress",
            "--no-warnings",

            // Otherwise a playlist stops at the first blocked video and the other 199 stay undownloaded.
            "--ignore-errors",

            "-P", request.TargetDirectory,
            "-o", "%(title)s.%(ext)s",

            // Windows forbids : ? * " < > | in file names, and video titles are full of colons.
            "--windows-filenames",
        ];

        arguments.AddRange(request.WholePlaylist ? ["--yes-playlist"] : ["--no-playlist"]);

        if (request.FfmpegDirectory is not null)
            arguments.AddRange(["--ffmpeg-location", request.FfmpegDirectory]);

        if (request.AudioOnly)
        {
            arguments.AddRange([
                "-x",
                "--audio-format", request.AudioFormat,

                // 0 is the best level, not the worst; yt-dlp counts like LAME here.
                "--audio-quality", "0",
                "--embed-metadata",
            ]);

            if (request.EmbedThumbnail) arguments.Add("--embed-thumbnail");
        }
        else
        {
            // The wish first, then two fallbacks: separate MP4/M4A streams, else a finished MP4, else
            // anything. Without the chain every video not offering the wish fails.
            var height = request.MaxHeight;
            var limit = height > 0 ? $"[height<={height}]" : "";

            arguments.AddRange([
                "-f", $"bv*{limit}[ext=mp4]+ba[ext=m4a]/bv*{limit}+ba/b{limit}/b",
                "--merge-output-format", "mp4",
                "--embed-metadata",
            ]);
        }

        // See InspectAsync: after "--" only the URL follows.
        arguments.Add("--");
        arguments.Add(request.Url);
        return arguments;
    }

    /// <summary>Reads an output line of yt-dlp.</summary>
    /// <returns><c>null</c> if the line says nothing about progress.</returns>
    public static DownloadProgress? ReadProgress(string line)
    {
        var download = DownloadLine().Match(line);

        if (download.Success)
        {
            var percent = double.Parse(download.Groups["pct"].Value,
                System.Globalization.CultureInfo.InvariantCulture);

            var detail = new List<string>();
            if (download.Groups["size"].Success) detail.Add($"of {download.Groups["size"].Value}");
            if (download.Groups["speed"].Success) detail.Add(download.Groups["speed"].Value);
            if (download.Groups["eta"].Success) detail.Add($"{download.Groups["eta"].Value} left");

            return new DownloadProgress(percent / 100, "Downloading", string.Join("  ·  ", detail));
        }

        // Post-processing reports no progress but takes noticeable time for a long video; without these
        // lines the display would sit at 100 % for minutes.
        if (line.StartsWith("[Merger]", StringComparison.Ordinal))
            return new DownloadProgress(null, "Merging video and audio", "");

        if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal))
            return new DownloadProgress(null, "Converting audio", "");

        if (line.StartsWith("[EmbedThumbnail]", StringComparison.Ordinal))
            return new DownloadProgress(null, "Embedding thumbnail", "");

        return null;
    }

    /// <summary>Extracts the target path from the lines yt-dlp prints while writing.</summary>
    public static string? ReadDestination(string line)
    {
        if (line.StartsWith("[download] Destination: ", StringComparison.Ordinal))
            return line["[download] Destination: ".Length..].Trim();

        var merger = MergerLine().Match(line);
        if (merger.Success) return merger.Groups["path"].Value;

        if (line.StartsWith("[ExtractAudio] Destination: ", StringComparison.Ordinal))
            return line["[ExtractAudio] Destination: ".Length..].Trim();

        return null;
    }

    [GeneratedRegex(
        @"^\[download\]\s+(?<pct>\d+(?:\.\d+)?)%\s+of\s+~?\s*(?<size>[\d.]+\s*\w+)(?:\s+at\s+(?<speed>\S+))?(?:\s+ETA\s+(?<eta>[\d:]+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex DownloadLine();

    [GeneratedRegex(@"^\[Merger\] Merging formats into ""(?<path>.+)""", RegexOptions.CultureInvariant)]
    private static partial Regex MergerLine();
}
