namespace MultiTool.Modules.Files.Metadata;

public enum FileKind { Jpeg, Png, Media, Unknown, WebP, Gif, Raster, Pdf, Office }

public sealed record Inspection(FileKind Kind, IReadOnlyList<MetadataEntry> Entries, string? Problem = null)
{
    public bool HasSensitive => Entries.Any(e => e.Sensitive);
    public bool IsComplete => Problem is null;
}

internal sealed record CleanOptions(bool ReplaceOriginal, bool NeutralName, bool ResetFileTimes, string Suffix);
internal sealed record CleanResult(string? OutputPath, string? Problem, string Verification)
{
    public bool Ok => Problem is null && OutputPath is not null;
}

/// <summary>
/// Build a new file, verify it, then publish it. Cleaning supported containers is
/// distinct from anonymizing their content or securely erasing source storage.
/// </summary>
internal static class Cleaner
{
    public static FileKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => FileKind.Jpeg,
        ".png" or ".apng" => FileKind.Png,
        ".webp" => FileKind.WebP,
        ".gif" => FileKind.Gif,
        ".heic" or ".heif" or ".avif" or ".tif" or ".tiff" or ".bmp" => FileKind.Raster,
        ".pdf" => FileKind.Pdf,
        ".docx" or ".xlsx" or ".pptx" => FileKind.Office,
        ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v" or ".wmv" or ".flv"
            or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".opus" or ".wav" or ".aac" => FileKind.Media,
        _ => FileKind.Unknown,
    };

    public static async Task<Inspection> InspectAsync(string path, string? ffprobe, CancellationToken token)
    {
        var kind = KindOf(path);
        try
        {
            var entries = new List<MetadataEntry>();
            if (IsNativeImage(kind))
                entries.AddRange((await Task.Run(() => ImageContainers.Clean(ReadImage(path), kind, token), token)).Removed);
            else if (kind is FileKind.Media or FileKind.Raster)
            {
                if (ffprobe is null) return new(kind, [], "ffprobe is missing; metadata was not checked.");
                entries.AddRange(await MediaCleaner.InspectAsync(path, ffprobe, token));
            }
            else if (kind == FileKind.Office)
                entries.AddRange(await Task.Run(() => OfficeCleaner.Inspect(path, token), token));
            else if (kind == FileKind.Pdf)
                entries.Add(new("PDF", "Rebuild", "All pages are written as images into a new PDF.", false));
            else return new(kind, [], "File format not supported.");
            entries.AddRange(await Task.Run(() => FilePrivacy.Streams(path), token));
            return new(kind, entries);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(kind, [], $"Check failed: {ex.Message}"); }
    }

    internal static bool IsNativeImage(FileKind kind) => kind is FileKind.Jpeg or FileKind.Png or FileKind.WebP or FileKind.Gif;

    internal static byte[] ReadImage(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > ImageContainers.MaximumBytes) throw new InvalidDataException("Image file larger than 256 MiB.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    internal static string VerificationFor(FileKind kind) => kind switch
    {
        FileKind.Media => "Container and known provenance signatures checked; encoded content stays.",
        FileKind.Pdf => "New PDF from page images; no original objects carried over.",
        FileKind.Office => "Document package checked; visible and hidden content stays.",
        FileKind.Raster => "First image as a new PNG file; image container checked.",
        _ => "Image container checked; compressed image data stays.",
    };

    public static async Task<CleanResult> CleanToFileAsync(string source, CleanOptions options,
        string? ffmpeg, string? ffprobe, CancellationToken token)
    {
        source = Path.GetFullPath(source);
        var kind = KindOf(source);
        if (kind == FileKind.Unknown) return new(null, "File format not supported.", "");
        if (kind == FileKind.Raster && options.ReplaceOriginal)
            return new(null, "This image format is exported as a PNG copy; turn off replacing the original.", "");
        var directory = Path.GetDirectoryName(source)!;
        var extension = kind == FileKind.Raster ? ".png" : Path.GetExtension(source);
        string temporary = Path.Combine(directory, $".multitool-{Guid.NewGuid():N}{extension}");
        try
        {
            var info = new FileInfo(source);
            long length = info.Length;
            var modified = info.LastWriteTimeUtc;
            var problem = await CleanAsync(source, temporary, ffmpeg, token, ffprobe);
            if (problem is not null) return new(null, problem, "");
            token.ThrowIfCancellationRequested();
            if (FilePrivacy.Streams(temporary).Count != 0) throw new IOException("Output contains additional data streams.");
            if (options.ResetFileTimes) FilePrivacy.ResetTimes(temporary);
            token.ThrowIfCancellationRequested();
            string target;
            if (options.ReplaceOriginal)
            {
                info.Refresh();
                if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != modified)
                    throw new IOException("Source file changed during cleaning.");
                target = source;

                // The original goes to the recycle bin before the cleaned version takes its place; everything
                // this app deletes must be restorable. Doubly so for a PDF, whose new version consists of page
                // images without text.
                var recycled = MultiTool.Abstractions.RecycleBin.Delete([source]);
                if (File.Exists(source))
                    throw new IOException($"Original could not be moved to the recycle bin: {recycled ?? "unknown reason"}");

                // File.Replace may preserve destination ADS on Windows. Move the new
                // file instead; it owns only its default stream.
                File.Move(temporary, target, overwrite: false);
            }
            else
            {
                string stem = options.NeutralName ? $"file-{Guid.NewGuid():N}" : Path.GetFileNameWithoutExtension(source) + SafeSuffix(options.Suffix);
                target = Path.Combine(directory, stem + extension);
                for (int i = 2; ; i++)
                {
                    if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
                    { target = Path.Combine(directory, $"{stem} ({i}){extension}"); continue; }
                    try { File.Move(temporary, target, overwrite: false); break; }
                    catch (IOException) when (File.Exists(target)) { target = Path.Combine(directory, $"{stem} ({i}){extension}"); }
                }
            }
            return new(target, null, VerificationFor(kind));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, ex.Message, ""); }
        finally { TryDelete(temporary); }
    }

    private static string SafeSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)) return " (cleaned)";
        if (suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("File suffix contains invalid characters.");
        return suffix;
    }

    public static async Task<string?> CleanAsync(string source, string target, string? ffmpeg,
        CancellationToken token, string? ffprobe = null)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return "Source and target must differ.";
        if (File.Exists(target)) return "Target file already exists.";
        bool owned = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using (new FileStream(target, FileMode.CreateNew, FileAccess.Write)) { }
            owned = true;
            var kind = KindOf(source);
            if (IsNativeImage(kind))
            {
                var rewrite = await Task.Run(() => ImageContainers.Clean(ReadImage(source), kind, token), token);
                await File.WriteAllBytesAsync(target, rewrite.Bytes, token);
                var verified = await Task.Run(() => ImageContainers.Clean(ReadImage(target), kind, token), token);
                if (!verified.Bytes.AsSpan().SequenceEqual(rewrite.Bytes) || verified.Removed.Count != 0)
                    throw new InvalidDataException("Image container still contains extra data after cleaning.");
            }
            else if (kind is FileKind.Media or FileKind.Raster)
            {
                if (ffmpeg is null || ffprobe is null) throw new InvalidOperationException("ffmpeg and ffprobe are required for this format.");
                await MediaCleaner.CleanAsync(source, target, ffmpeg, ffprobe, kind == FileKind.Raster, token);
            }
            else if (kind == FileKind.Office) await Task.Run(() => OfficeCleaner.Clean(source, target, token), token);
            else if (kind == FileKind.Pdf) await Task.Run(() => PdfCleaner.Clean(source, target, token), token);
            else throw new InvalidDataException("File format not supported.");
            token.ThrowIfCancellationRequested();
            if (new FileInfo(target).Length == 0) throw new InvalidDataException("Output is empty.");
            owned = false;
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ex.Message; }
        finally { if (owned) TryDelete(target); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
