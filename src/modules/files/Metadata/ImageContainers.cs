using System.Buffers.Binary;
using System.Text;

namespace MultiTool.Modules.Files.Metadata;

/// <summary>
/// Strict, bounded container rewriting. Keep only image/animation data and the small
/// Adobe colour-transform descriptor required for CMYK JPEG decoding. In particular,
/// unknown application/private blocks are not silently trusted. Compressed pixels are
/// not an anonymity boundary: watermarks and steganography in pixels remain.
/// </summary>
internal static class ImageContainers
{
    internal sealed record Rewrite(byte[] Bytes, IReadOnlyList<MetadataEntry> Removed);
    internal const int MaximumBytes = 256 * 1024 * 1024;

    internal static Rewrite Clean(byte[] bytes, FileKind kind, CancellationToken token)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Image file larger than 256 MiB.");
        return kind switch
        {
            FileKind.Jpeg => Jpeg(bytes, token),
            FileKind.Png => Png(bytes, token),
            FileKind.WebP => WebP(bytes, token),
            FileKind.Gif => Gif(bytes, token),
            _ => throw new InvalidDataException("Image format not supported."),
        };
    }

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Removed(List<MetadataEntry> entries, string name, int size) =>
        entries.Add(new MetadataEntry(name.Contains("C2PA", StringComparison.Ordinal) ? "Provenance" : "Metadata",
            name, $"{size} Bytes", true));

    private static Rewrite Jpeg(byte[] b, CancellationToken token)
    {
        Need(b.Length >= 4 && b[0] == 0xff && b[1] == 0xd8, "Invalid JPEG signature.");
        using var output = new MemoryStream();
        var entries = new List<MetadataEntry>();
        output.Write(b, 0, 2);
        int at = 2;
        bool scan = false, sawScan = false, sawFrame = false;
        while (at < b.Length)
        {
            token.ThrowIfCancellationRequested();
            if (scan)
            {
                // Entropy-coded scans contain stuffed FF00 and restart markers.
                // Stop at every other marker, not only the first SOS. Progressive
                // JPEGs can carry APP/COM metadata between their scans.
                int start = at;
                while (at < b.Length)
                {
                    if ((at & 0xffff) == 0) token.ThrowIfCancellationRequested();
                    if (b[at] != 0xff) { at++; continue; }
                    int end = at + 1;
                    while (end < b.Length && b[end] == 0xff) end++;
                    Need(end < b.Length, "JPEG image data is truncated.");
                    if (b[end] == 0 || b[end] is >= 0xd0 and <= 0xd7) { at = end + 1; continue; }
                    break;
                }
                output.Write(b, start, at - start);
                scan = false;
            }
            Need(at < b.Length && b[at] == 0xff, "Invalid JPEG segment sequence.");
            while (at < b.Length && b[at] == 0xff) at++;
            Need(at < b.Length, "JPEG marker is truncated.");
            byte marker = b[at++];
            if (marker == 0xd9)
            {
                Need(sawScan && sawFrame, "JPEG contains no complete image data.");
                output.WriteByte(0xff); output.WriteByte(marker);
                if (at < b.Length) Removed(entries, "Data after JPEG end", b.Length - at);
                return new(output.ToArray(), entries);
            }
            if (marker == 0x01) { output.WriteByte(0xff); output.WriteByte(marker); continue; }
            Need(marker != 0 && marker != 0xd8 && marker is not (>= 0xd0 and <= 0xd7), "Invalid JPEG marker.");
            Need(at <= b.Length - 2, "JPEG segment length missing.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
            Need(length >= 2 && length <= b.Length - at, "JPEG segment is truncated.");
            var payload = b.AsSpan(at + 2, length - 2);
            bool application = marker is >= 0xe0 and <= 0xef;
            if (application || marker == 0xfe)
            {
                // Only the interpretation of CMYK/YCCK is retained, never arbitrary
                // bytes from APP14. Flags/version are generated, not copied.
                if (marker == 0xee && payload.Length == 12 && payload[..5].SequenceEqual("Adobe"u8) && payload[11] <= 2)
                {
                    byte[] adobe = [.."Adobe"u8, 0, 100, 0, 0, 0, 0, payload[11]];
                    if (!payload.SequenceEqual(adobe)) Removed(entries, "Adobe extra data", payload.Length);
                    output.Write([0xff, 0xee, 0, 14]); output.Write(adobe);
                }
                else
                {
                    var name = marker switch
                    {
                        0xeb => "C2PA/JUMBF (APP11)",
                        0xe1 => "EXIF/XMP (APP1)",
                        0xe2 => "ICC/MPF (APP2)",
                        0xed => "IPTC/Photoshop (APP13)",
                        0xfe => "JPEG comment",
                        _ => $"JPEG APP{marker - 0xe0}",
                    };
                    Removed(entries, name, payload.Length);
                    if (marker == 0xe1 && payload.StartsWith("Exif\0\0"u8))
                        entries.AddRange(Exif.Read(payload[6..]));
                }
            }
            else
            {
                Need(marker is >= 0xc0 and <= 0xcf && marker != 0xc8 || marker is >= 0xda and <= 0xdf,
                    $"JPEG marker 0x{marker:X2} is not supported.");
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    Need(payload.Length >= 6 && payload.Length == 6 + 3 * payload[5], "Invalid JPEG frame header.");
                    sawFrame = true;
                }
                if (marker == 0xda)
                {
                    Need(sawFrame && payload.Length >= 4 && payload.Length == 4 + 2 * payload[0], "Invalid JPEG scan header.");
                    scan = sawScan = true;
                }
                output.WriteByte(0xff); output.WriteByte(marker);
                output.Write(b, at, length);
            }
            at += length;
        }
        throw new InvalidDataException("JPEG end marker missing.");
    }

    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static Rewrite Png(byte[] b, CancellationToken token)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Need(b.AsSpan().StartsWith(signature), "Invalid PNG signature.");
        using var output = new MemoryStream();
        output.Write(signature);
        var entries = new List<MetadataEntry>();
        int at = 8;
        bool header = false, data = false, palette = false;
        byte color = 0;
        while (at < b.Length)
        {
            token.ThrowIfCancellationRequested();
            Need(b.Length - at >= 12, "PNG chunk is truncated.");
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
            Need(rawLength <= b.Length - at - 12, "Invalid PNG chunk length.");
            int length = (int)rawLength;
            var typeBytes = b.AsSpan(at + 4, 4);
            foreach (byte value in typeBytes) Need(value is >= 65 and <= 90 or >= 97 and <= 122, "Invalid PNG chunk type.");
            string type = Encoding.ASCII.GetString(typeBytes);
            Need(Crc(b.AsSpan(at + 4, length + 4)) == BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + length + 8)),
                $"PNG checksum wrong: {type}.");
            Need(header || type == "IHDR", "PNG header missing.");
            if (type == "IHDR")
            {
                Need(!header && length == 13, "Invalid PNG header.");
                var h = b.AsSpan(at + 8, length);
                Need(BinaryPrimitives.ReadUInt32BigEndian(h) is > 0 and <= int.MaxValue
                    && BinaryPrimitives.ReadUInt32BigEndian(h[4..]) is > 0 and <= int.MaxValue,
                    "Invalid PNG image size.");
                color = h[9];
                Need(color switch { 0 => h[8] is 1 or 2 or 4 or 8 or 16, 3 => h[8] is 1 or 2 or 4 or 8,
                    2 or 4 or 6 => h[8] is 8 or 16, _ => false }, "Invalid PNG color format.");
                Need(h[10] == 0 && h[11] == 0 && h[12] <= 1, "PNG compression not supported.");
                header = true;
            }
            if (type == "PLTE") { Need(!palette && !data && length is > 0 and <= 768 && length % 3 == 0, "Invalid PNG palette."); palette = true; }
            if (type == "IDAT") { Need(color != 3 || palette, "PNG palette missing."); data = true; }
            bool keep = type is "IHDR" or "PLTE" or "IDAT" or "IEND" or "tRNS" or "acTL" or "fcTL" or "fdAT";
            if (type == "tRNS") Need(!data && (color switch { 0 => length == 2, 2 => length == 6, 3 => palette && length is > 0 and <= 256, _ => false }), "Invalid PNG transparency.");
            if (type == "acTL") Need(!data && length == 8, "Invalid APNG header.");
            if (type == "fcTL") Need(length == 26, "Invalid APNG frame header.");
            if (type == "fdAT") Need(length >= 4, "Invalid APNG image data.");
            if (keep) output.Write(b, at, length + 12);
            else
            {
                Need((typeBytes[0] & 32) != 0, $"Unknown critical PNG chunk: {type}.");
                Removed(entries, type == "caBX" ? "C2PA/JUMBF (caBX)" : $"PNG {type}", length);
                if (type == "eXIf") entries.AddRange(Exif.Read(b.AsSpan(at + 8, length)));
            }
            at += length + 12;
            if (type == "IEND")
            {
                Need(data && length == 0, "PNG contains no complete image data.");
                if (at < b.Length) Removed(entries, "Data after PNG end", b.Length - at);
                return new(output.ToArray(), entries);
            }
        }
        throw new InvalidDataException("PNG end marker missing.");
    }

    private static Rewrite WebP(byte[] b, CancellationToken token)
    {
        Need(b.Length >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8), "Invalid WebP signature.");
        long declared = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) + 8L;
        Need(declared >= 12 && declared <= b.Length, "WebP file is truncated.");
        var entries = new List<MetadataEntry>();
        using var output = new MemoryStream();
        output.Write("RIFF\0\0\0\0WEBP"u8);
        bool picture = false;
        Chunks(12, (int)declared, output, false);
        Need(picture, "WebP contains no image data.");
        if (declared < b.Length) Removed(entries, "Data after WebP end", b.Length - (int)declared);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return new(bytes, entries);

        void Chunks(int at, int end, Stream destination, bool frame)
        {
            while (at < end)
            {
                token.ThrowIfCancellationRequested();
                Need(end - at >= 8, "WebP chunk is truncated.");
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 4));
                Need(raw <= end - at - 8, "Invalid WebP chunk length.");
                int length = (int)raw, padded = length + (length & 1);
                Need(padded <= end - at - 8, "WebP padding byte missing.");
                string type = Encoding.ASCII.GetString(b, at, 4);
                var payload = b.AsSpan(at + 8, length);
                byte[]? replacement = null;
                bool keep = type is "VP8 " or "VP8L" or "ALPH" || !frame && type is "VP8X" or "ANIM" or "ANMF";
                if (type is "VP8 " or "VP8L") { Need(length > 0, "Empty WebP image data."); picture = true; }
                if (type == "VP8X" && !frame)
                {
                    Need(length == 10, "Invalid WebP header.");
                    replacement = payload.ToArray();
                    replacement[0] &= 0x12; // Only animation and alpha flags survive.
                    replacement[1] = replacement[2] = replacement[3] = 0;
                    if (!payload.SequenceEqual(replacement)) Removed(entries, "WebP metadata flags", 4);
                }
                if (type == "ANIM" && !frame) Need(length == 6, "Invalid WebP animation header.");
                if (type == "ANMF" && !frame)
                {
                    Need(length >= 16, "Invalid WebP frame.");
                    using var nested = new MemoryStream();
                    nested.Write(payload[..16]);
                    Chunks(at + 24, at + 8 + length, nested, true);
                    replacement = nested.ToArray();
                }
                if (keep)
                {
                    var content = replacement is null ? payload : replacement.AsSpan();
                    destination.Write(b, at, 4);
                    byte[] size = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)content.Length);
                    destination.Write(size); destination.Write(content);
                    if ((content.Length & 1) != 0) destination.WriteByte(0);
                }
                else Removed(entries, type == "C2PA" ? "C2PA/JUMBF (WebP)" : $"WebP {type}", length);
                at += padded + 8;
            }
        }
    }

    private static Rewrite Gif(byte[] b, CancellationToken token)
    {
        Need(b.Length >= 13 && (b.AsSpan(0, 6).SequenceEqual("GIF89a"u8) || b.AsSpan(0, 6).SequenceEqual("GIF87a"u8)), "Invalid GIF signature.");
        Need(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)) > 0 && BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)) > 0, "Invalid GIF image size.");
        using var output = new MemoryStream();
        var entries = new List<MetadataEntry>();
        int at = 13;
        if ((b[10] & 0x80) != 0) at += 3 * (1 << ((b[10] & 7) + 1));
        Need(at <= b.Length, "GIF color table is truncated.");
        output.Write(b, 0, at);
        bool picture = false;
        while (at < b.Length)
        {
            token.ThrowIfCancellationRequested();
            int start = at;
            byte type = b[at++];
            if (type == 0x3b)
            {
                Need(picture, "GIF contains no image data.");
                output.WriteByte(type);
                if (at < b.Length) Removed(entries, "Data after GIF end", b.Length - at);
                return new(output.ToArray(), entries);
            }
            if (type == 0x2c)
            {
                Need(b.Length - at >= 9, "GIF image header is truncated.");
                byte flags = b[at + 8];
                at += 9;
                if ((flags & 0x80) != 0) at += 3 * (1 << ((flags & 7) + 1));
                Need(at < b.Length && b[at] is >= 2 and <= 8, "Invalid GIF compression.");
                at++;
                Blocks();
                output.Write(b, start, at - start);
                picture = true;
                continue;
            }
            Need(type == 0x21 && at < b.Length, "Invalid GIF block.");
            byte label = b[at++];
            int blocksStart = at;
            Blocks();
            if (label == 0xf9)
            {
                Need(at - blocksStart == 6 && b[blocksStart] == 4, "Invalid GIF graphic control.");
                output.Write(b, start, at - start);
            }
            else if (label == 0xff && at - blocksStart >= 17 && b[blocksStart] == 11
                && (b.AsSpan(blocksStart + 1, 11).SequenceEqual("NETSCAPE2.0"u8) || b.AsSpan(blocksStart + 1, 11).SequenceEqual("ANIMEXTS1.0"u8)))
            {
                Need(b[blocksStart + 12] == 3 && b[blocksStart + 13] == 1, "Invalid GIF loop count.");
                output.Write([0x21, 0xff, 11]); output.Write("NETSCAPE2.0"u8);
                output.Write([3, 1, b[blocksStart + 14], b[blocksStart + 15], 0]);
                if (at - blocksStart != 17) Removed(entries, "Extra data in GIF animation block", at - blocksStart - 17);
            }
            else
            {
                Need(label != 1, "GIF with plain text blocks is not supported.");
                bool c2pa = label == 0xff && at - blocksStart >= 12 && b.AsSpan(blocksStart + 1, 8).SequenceEqual("C2PA_GIF"u8);
                Removed(entries, c2pa ? "C2PA/JUMBF (GIF)" : $"GIF extension 0x{label:X2}", at - start);
            }
        }
        throw new InvalidDataException("GIF end marker missing.");

        void Blocks()
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Need(at < b.Length, "GIF data is truncated.");
                int size = b[at++];
                if (size == 0) return;
                Need(size <= b.Length - at, "GIF data block is truncated.");
                at += size;
            }
        }
    }
}
