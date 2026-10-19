using System.Text.Json;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Files.Metadata;

internal static class MediaCleaner
{
    private static async Task<JsonDocument> Probe(string path, string tool, CancellationToken token)
    {
        var result = await ExternalProcess.RunAsync(tool,
            ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", Path.GetFullPath(path)],
            cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!result.Ok) throw new InvalidDataException($"ffprobe: {result.Problem}");
        var document = JsonDocument.Parse(result.Output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        { document.Dispose(); throw new InvalidDataException("No readable media streams."); }
        return document;
    }

    private static List<MetadataEntry> Tags(JsonElement root)
    {
        List<MetadataEntry> entries = [];
        void Add(JsonElement item, string group)
        {
            if (item.TryGetProperty("tags", out var tags))
                foreach (var tag in tags.EnumerateObject()) entries.Add(new(group, tag.Name, tag.Value.ToString(), true));
        }
        if (root.TryGetProperty("format", out var format)) Add(format, "Container");
        foreach (var stream in root.GetProperty("streams").EnumerateArray()) Add(stream, $"Track {stream.GetProperty("index")}");
        if (root.TryGetProperty("chapters", out var chapters))
            foreach (var chapter in chapters.EnumerateArray()) { Add(chapter, "Chapter"); entries.Add(new("Chapter", "Chapter marker", "Will be removed", true)); }
        return entries;
    }

    public static async Task<IReadOnlyList<MetadataEntry>> InspectAsync(string path, string ffprobe, CancellationToken token)
    {
        using var probe = await Probe(path, ffprobe, token);
        var entries = Tags(probe.RootElement);
        if (await HasProvenanceSignature(path, token)) entries.Add(new("Provenance", "C2PA/JUMBF", "Provenance signature found; no signature validation.", true));
        return entries;
    }

    public static async Task CleanAsync(string source, string target, string ffmpeg, string ffprobe, bool raster, CancellationToken token)
    {
        using var before = await Probe(source, ffprobe, token);
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", Path.GetFullPath(source)];
        if (raster) args.AddRange(["-map", "0:V:0", "-frames:v", "1", "-c:v", "png"]);
        else
        {
            args.AddRange(["-map", "0:V?", "-map", "0:a?", "-c", "copy"]);
            int video = 0;
            foreach (var stream in before.RootElement.GetProperty("streams").EnumerateArray())
            {
                if (stream.GetProperty("codec_type").GetString() != "video") continue;
                if (stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() != 0) continue;
                string? filter = stream.GetProperty("codec_name").GetString() switch
                {
                    "h264" => "filter_units=remove_types=6|12",
                    "hevc" => "filter_units=remove_types=38|39|40",
                    "mpeg2video" => "filter_units=remove_types=178",
                    _ => null,
                };
                if (filter is not null) args.AddRange([$"-bsf:v:{video}", filter]);
                video++;
            }
        }
        args.AddRange(["-map_metadata", "-1", "-map_metadata:s", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-metadata", "encoder=", "-metadata:s", "encoder=", "-metadata:s", "language=und", "-metadata:s", "handler_name=", "-metadata:s", "title=", Path.GetFullPath(target)]);
        var result = await ExternalProcess.RunAsync(ffmpeg, args, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!result.Ok) throw new InvalidDataException($"ffmpeg: {result.Problem}");
        if (raster)
        {
            var clean = ImageContainers.Clean(Cleaner.ReadImage(target), FileKind.Png, token);
            await File.WriteAllBytesAsync(target, clean.Bytes, token);
            return;
        }
        using var after = await Probe(target, ffprobe, token);
        foreach (var entry in Tags(after.RootElement))
        {
            // These are generated structural fields, not source metadata. Values
            // with free text are deliberately not allowed here.
            bool structural = entry.Name switch
            {
                "major_brand" or "minor_version" or "compatible_brands" => entry.Group == "Container",
                "language" => entry.Value == "und",
                "handler_name" => entry.Value is "VideoHandler" or "SoundHandler",
                "vendor_id" => entry.Value is "[0][0][0][0]" or "FFMP",
                "DURATION" => TimeSpan.TryParse(entry.Value, out _),
                _ => false,
            };
            if (!structural) throw new InvalidDataException($"Output contains metadata: {entry.Group}/{entry.Name}.");
        }
        foreach (var stream in after.RootElement.GetProperty("streams").EnumerateArray())
            if (stream.GetProperty("codec_type").GetString() is not ("audio" or "video")) throw new InvalidDataException("Output contains extra tracks.");
        if (await HasProvenanceSignature(target, token)) throw new InvalidDataException("Output contains a C2PA/JUMBF signature; file was not released.");
    }

    // Conservative supplementary scan: false positives refuse publication. This
    // does not claim to recognize arbitrary encoded/steganographic provenance.
    private static async Task<bool> HasProvenanceSignature(string path, CancellationToken token)
    {
        byte[][] signatures = ["c2pa"u8.ToArray(), "C2PA"u8.ToArray(), "jumb"u8.ToArray(), "JUMBF"u8.ToArray()];
        using var input = File.OpenRead(path);
        var buffer = new byte[65536 + 4];
        int carry = 0, count;
        while ((count = await input.ReadAsync(buffer.AsMemory(carry, 65536), token)) != 0)
        {
            int length = carry + count;
            foreach (var signature in signatures) if (buffer.AsSpan(0, length).IndexOf(signature) >= 0) return true;
            carry = Math.Min(4, length);
            buffer.AsSpan(length - carry, carry).CopyTo(buffer);
        }
        return false;
    }
}
