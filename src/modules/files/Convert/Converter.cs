using MultiTool.Abstractions;
using SkiaSharp;

namespace MultiTool.Modules.Files.Convert;

/// <summary>The three formats this Skia build can really write.</summary>
/// <remarks>
/// <c>SKEncodedImageFormat</c> names fourteen values (Bmp, Gif, Ico, Wbmp, Pkm, Ktx, Astc, Dng, Heif,
/// Avif, Jpegxl among them); eleven return <c>null</c> when encoding. The enum says what Skia
/// <i>defines</i>, not what this build <i>ships</i>. Same lesson as <c>ffmpeg -encoders</c> and
/// <c>h264_nvenc</c>: the list is no promise.
/// </remarks>
public enum TargetFormat
{
    Png,
    Jpeg,
    WebP,
}

/// <summary>The container a video is remuxed into.</summary>
/// <remarks>
/// Two entries because there are two sensible ones: MP4 plays everywhere (Discord, WhatsApp,
/// browsers, Windows Media Player); MKV survives an interrupted recording.
/// </remarks>
public enum VideoContainer
{
    Mp4,
    Mkv,
}

public enum ResizeMode
{
    /// <summary>Unchanged.</summary>
    None,

    /// <summary>Longer edge to a maximum; smaller images stay as they are.</summary>
    MaxEdge,

    /// <summary>To a share of the original size.</summary>
    Percent,
}

/// <param name="Quality">1…100. No effect for PNG, which is lossless.</param>
/// <param name="TargetDirectory"><c>null</c> means next to the original, the common case.</param>
public sealed record ConvertSettings(
    TargetFormat Format,
    int Quality,
    ResizeMode Resize,
    int MaxEdge,
    int Percent,
    string? TargetDirectory,
    bool Overwrite,
    VideoContainer Video = VideoContainer.Mp4);

/// <param name="UsedFfmpeg">Skia couldn't read the source; ffmpeg translated it first.</param>
public sealed record ConvertResult(
    bool Ok,
    string? OutputPath,
    long BeforeBytes,
    long AfterBytes,
    int Width,
    int Height,
    string? Problem,
    bool UsedFfmpeg);

/// <summary>Converts images. Skia does the work, ffmpeg steps in for formats Skia can't read.</summary>
/// <remarks>
/// <para>
/// <b>Why Skia:</b> it is already in the process (Avalonia's renderer, shared). Converting costs no
/// process start, no temp file and no dependency; seconds instead of minutes for 200 photos.
/// </para>
/// <para>
/// <b>Why ffmpeg anyway:</b> this Skia build reads PNG, JPEG, WebP, BMP and GIF, <b>not</b> AVIF, TIFF or
/// the iPhone's HEIC, the very formats people look for a converter for. ffmpeg translates them to a
/// temporary PNG, then Skia takes over. Without ffmpeg those formats are missing, hence
/// <c>mandatory: false</c> in the manifest.
/// </para>
/// </remarks>
internal static class Converter
{
    /// <summary>What's worth trying at all, for file picking and dropping.</summary>
    public static readonly string[] KnownExtensions =
    [
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif",
        ".heic", ".heif", ".avif", ".tif", ".tiff",
    ];

    /// <summary>Video containers that can be remuxed.</summary>
    public static readonly string[] VideoExtensions =
    [
        ".mkv", ".mp4", ".mov", ".m4v", ".webm", ".avi", ".ts", ".mpg", ".mpeg", ".wmv",
    ];

    /// <summary>Everything the module accepts, image and video.</summary>
    public static IEnumerable<string> AllExtensions => KnownExtensions.Concat(VideoExtensions);

    public static bool LooksLikeImage(string path) =>
        KnownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool LooksLikeVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool LooksLikeMedia(string path) => LooksLikeImage(path) || LooksLikeVideo(path);

    public static string ExtensionOf(VideoContainer container) =>
        container == VideoContainer.Mkv ? ".mkv" : ".mp4";

    public static string ExtensionOf(TargetFormat format) => format switch
    {
        TargetFormat.Jpeg => ".jpg",
        TargetFormat.WebP => ".webp",
        _ => ".png",
    };

    private static SKEncodedImageFormat SkiaFormat(TargetFormat format) => format switch
    {
        TargetFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        TargetFormat.WebP => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Png,
    };

    /// <summary>Puts a video into another container without re-encoding.</summary>
    /// <remarks>
    /// <para>
    /// The recorder writes Matroska on purpose: an MKV stays playable at any moment, an MP4 only becomes
    /// complete on close because its index is at the end. Right for a recording that may be cut off.
    /// </para>
    /// <para>
    /// Wrong for sharing: Discord shows no preview for MKV, many browsers won't play it.
    /// <c>-c copy</c> writes the same video and audio data into MP4, <b>lossless and in about a second</b>.
    /// </para>
    /// <para>
    /// <c>-movflags +faststart</c> moves the index to the front so browser playback starts before the
    /// whole file has loaded; it costs one more pass over the file.
    /// </para>
    /// <para>
    /// <b>Not every stream fits every container.</b> MP4 rejects VP9, Opus or foreign subtitle tracks; the
    /// call then fails and says so instead of leaving a broken file.
    /// </para>
    /// </remarks>
    private static async Task<ConvertResult> RemuxAsync(
        string source,
        ConvertSettings settings,
        string? ffmpeg,
        long before,
        CancellationToken cancellationToken)
    {
        if (ffmpeg is null)
        {
            return new ConvertResult(false, null, before, 0, 0, 0,
                "Remuxing videos needs ffmpeg.", false);
        }

        var target = TargetPath(source, ExtensionOf(settings.Video), settings);

        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "error",
            "-i", source,

            // All streams, not just the first per type, or a second audio track would silently vanish.
            "-map", "0",
            "-c", "copy",
        ];

        if (settings.Video == VideoContainer.Mp4)
        {
            arguments.AddRange([
                "-movflags", "+faststart",

                // MKV subtitles are usually SRT or ASS; MP4 only knows mov_text. Converting is free and saves the track.
                "-c:s", "mov_text",
            ]);
        }

        arguments.AddRange(["-y", target]);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var result = await ExternalProcess.RunAsync(ffmpeg, arguments, cancellationToken: cancellationToken);

            if (!result.Ok || !File.Exists(target))
            {
                TryDelete(target);

                var text = result.Error.Trim();

                return new ConvertResult(false, null, before, 0, 0, 0,
                    text.Length == 0
                        ? "Remuxing failed."
                        : $"Remuxing failed: {LastLine(text)}", true);
            }

            var after = new FileInfo(target).Length;
            var (width, height) = await SizeAsync(ffmpeg, target, cancellationToken);

            return new ConvertResult(true, target, before, after, width, height, null, true);
        }
        catch (OperationCanceledException)
        {
            return new ConvertResult(false, null, before, 0, 0, 0, "Cancelled.", true);
        }
        catch (Exception ex)
        {
            return new ConvertResult(false, null, before, 0, 0, 0, ex.Message, true);
        }
    }

    /// <summary>Frame size of a video, for its row in the list.</summary>
    private static async Task<(int Width, int Height)> SizeAsync(
        string ffmpeg, string path, CancellationToken cancellationToken)
    {
        try
        {
            // ffprobe sits next to ffmpeg. Without it the size stays empty: one detail less, not an error.
            var probe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
            if (!File.Exists(probe)) probe = "ffprobe";

            var result = await ExternalProcess.RunAsync(probe,
            [
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-of", "csv=p=0:s=x", path,
            ], cancellationToken: cancellationToken);

            var parts = result.Output.Trim().Split('x');

            return parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
                ? (w, h)
                : (0, 0);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    /// <summary>The last non-empty line; that's where ffmpeg states the actual reason.</summary>
    private static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? text;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }

    public static async Task<ConvertResult> ConvertAsync(
        string source,
        ConvertSettings settings,
        string? ffmpeg,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        long before = 0;
        try { before = new FileInfo(source).Length; }
        catch (Exception) { }

        // Videos take their own path: remux instead of re-encode. Size and quality from the image
        // settings don't apply; they would require re-encoding and turn a second into minutes.
        if (LooksLikeVideo(source))
            return await RemuxAsync(source, settings, ffmpeg, before, cancellationToken);

        string? scratch = null;

        try
        {
            var usedFfmpeg = false;

            var (bitmap, origin) = Load(source);

            if (bitmap is null)
            {
                if (ffmpeg is null)
                {
                    return new ConvertResult(false, null, before, 0, 0, 0,
                        $"\"{Path.GetExtension(source).TrimStart('.').ToUpperInvariant()}\" is not readable by Skia, "
                        + "ffmpeg is missing. Without ffmpeg only PNG, JPEG, WebP, BMP and GIF.", false);
                }

                scratch = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.png");
                Directory.CreateDirectory(tempDirectory);

                var result = await ExternalProcess.RunAsync(ffmpeg,
                    ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-frames:v", "1", scratch],
                    cancellationToken: cancellationToken);

                if (!File.Exists(scratch))
                {
                    return new ConvertResult(false, null, before, 0, 0, 0,
                        $"ffmpeg could not read the file: {result.Error.Trim()}", true);
                }

                usedFfmpeg = true;

                // ffmpeg already applied the orientation while translating; the intermediate comes out upright.
                (bitmap, origin) = (SKBitmap.Decode(scratch), SKEncodedOrigin.TopLeft);

                if (bitmap is null)
                {
                    return new ConvertResult(false, null, before, 0, 0, 0,
                        "ffmpeg's intermediate file is unreadable.", true);
                }
            }

            using (bitmap)
            {
                using var upright = Upright(bitmap, origin);
                using var sized = Scale(upright, settings);

                using var image = SKImage.FromBitmap(sized);
                using var data = image.Encode(SkiaFormat(settings.Format), Math.Clamp(settings.Quality, 1, 100));

                if (data is null || data.Size == 0)
                {
                    return new ConvertResult(false, null, before, 0, 0, 0,
                        "Target format not writable. Skia encoded nothing.",
                        usedFfmpeg);
                }

                var target = TargetPath(source, ExtensionOf(settings.Format), settings);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, data.ToArray(), cancellationToken);

                long after = 0;
                try { after = new FileInfo(target).Length; }
                catch (Exception) { }

                return new ConvertResult(true, target, before, after, sized.Width, sized.Height, null, usedFfmpeg);
            }
        }
        catch (OperationCanceledException)
        {
            return new ConvertResult(false, null, before, 0, 0, 0, "Cancelled.", false);
        }
        catch (Exception ex)
        {
            return new ConvertResult(false, null, before, 0, 0, 0, ex.Message, false);
        }
        finally
        {
            if (scratch is not null)
            {
                try { File.Delete(scratch); } catch (Exception) { }
            }
        }
    }

    /// <summary>Reads the image and how it was meant to be rotated.</summary>
    /// <remarks>
    /// <b><c>SKBitmap.Decode</c> doesn't apply the EXIF orientation</b>, hence <see cref="SKCodec"/>:
    /// otherwise every portrait phone photo would come out sideways, after conversion, when the
    /// orientation tag is gone. The codec reports it; <see cref="Upright"/> applies it.
    /// </remarks>
    private static (SKBitmap? Bitmap, SKEncodedOrigin Origin) Load(string path)
    {
        try
        {
            var origin = SKEncodedOrigin.TopLeft;

            using (var codec = SKCodec.Create(path))
            {
                if (codec is not null) origin = codec.EncodedOrigin;
            }

            return (SKBitmap.Decode(path), origin);
        }
        catch (Exception)
        {
            return (null, SKEncodedOrigin.TopLeft);
        }
    }

    /// <summary>Rotates and mirrors as the EXIF orientation means.</summary>
    /// <remarks>
    /// <para>
    /// The eight cases as matrices: <c>x' = ScaleX·x + SkewX·y + TransX</c>,
    /// <c>y' = SkewY·x + ScaleY·y + TransY</c>; width and height swap in the four rotated cases.
    /// </para>
    /// <para>
    /// All eight, not just the three common ones: cases 5 and 7 are rare, but when they occur the result
    /// is mirrored, noticed only after 200 images are written.
    /// </para>
    /// </remarks>
    private static SKBitmap Upright(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return source.Copy();

        float w = source.Width;
        float h = source.Height;

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                          or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight     => new SKMatrix(-1, 0, w,  0,  1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight  => new SKMatrix(-1, 0, w,  0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft   => new SKMatrix( 1, 0, 0,  0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop      => new SKMatrix( 0, 1, 0,  1,  0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop     => new SKMatrix( 0, -1, h, 1,  0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom  => new SKMatrix( 0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom   => new SKMatrix( 0, 1, 0, -1,  0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var target = new SKBitmap(
            swap ? source.Height : source.Width,
            swap ? source.Width : source.Height,
            source.ColorType, source.AlphaType);

        using var canvas = new SKCanvas(target);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);

        return target;
    }

    /// <summary>Shrinks if requested.</summary>
    /// <remarks>
    /// <c>MaxEdge</c> deliberately does <b>not</b> enlarge: scaling an 800 px image to 1920 makes the file
    /// bigger and the image no better. "At most 1920" is a limit, not a target.
    /// </remarks>
    private static SKBitmap Scale(SKBitmap source, ConvertSettings settings)
    {
        var (width, height) = settings.Resize switch
        {
            ResizeMode.MaxEdge when Math.Max(source.Width, source.Height) > settings.MaxEdge
                => Fit(source, settings.MaxEdge),

            ResizeMode.Percent when settings.Percent is > 0 and < 100
                => (Math.Max(1, source.Width * settings.Percent / 100),
                    Math.Max(1, source.Height * settings.Percent / 100)),

            _ => (source.Width, source.Height),
        };

        if (width == source.Width && height == source.Height) return source.Copy();

        var info = new SKImageInfo(width, height, source.ColorType, source.AlphaType);

        // Linear with mipmaps: smooth instead of jagged when shrinking by more than half.
        // SKFilterQuality is obsolete in SkiaSharp 3, and warnings are errors here.
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        return source.Resize(info, sampling) ?? source.Copy();
    }

    private static (int Width, int Height) Fit(SKBitmap source, int maxEdge)
    {
        var factor = (double)maxEdge / Math.Max(source.Width, source.Height);
        return (Math.Max(1, (int)Math.Round(source.Width * factor)),
                Math.Max(1, (int)Math.Round(source.Height * factor)));
    }

    /// <summary>Where to write.</summary>
    /// <remarks>
    /// <b>The source file is never overwritten, not even on request.</b> PNG to PNG in the same folder with
    /// resizing would otherwise hit the original, and a shrunk image can't be restored. That happens
    /// with the most obvious settings.
    /// </remarks>
    private static string TargetPath(string source, string extension, ConvertSettings settings)
    {
        var directory = settings.TargetDirectory is { Length: > 0 } chosen
            ? chosen
            : Path.GetDirectoryName(source) ?? ".";

        var name = Path.GetFileNameWithoutExtension(source);

        var candidate = Path.Combine(directory, name + extension);

        if (string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase))
            candidate = Path.Combine(directory, $"{name} (converted){extension}");

        if (settings.Overwrite) return candidate;

        var counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name} ({counter}){extension}");
            counter++;
        }

        return candidate;
    }
}
