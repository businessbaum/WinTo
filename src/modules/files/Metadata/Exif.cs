using System.Buffers.Binary;
using System.Text;

namespace MultiTool.Modules.Files.Metadata;

/// <param name="Group">"Camera", "Location", "Person": what someone wanting to delete things looks for.</param>
/// <param name="Sensitive">Whether this entry allows conclusions about a person or place.</param>
public sealed record MetadataEntry(string Group, string Name, string Value, bool Sensitive);

/// <summary>A small EXIF reader for exactly the fields one wants to get rid of.</summary>
/// <remarks>
/// <para>
/// EXIF sits in a JPEG as an APP1 segment containing a full TIFF file: a byte-order header, a
/// directory (IFD0) of 12-byte entries and pointers to two subdirectories, Exif and GPS. Each entry
/// names tag, type and count; a value that fits in four bytes is stored inline, otherwise an offset.
/// </para>
/// <para>
/// <b>Only what is shown is read.</b> Removing EXIF doesn't require understanding it (the whole segment
/// goes). The reader only answers "what's in there", with the twelve tags people care about.
/// </para>
/// <para>A broken or unusual EXIF yields <b>no</b> entries, never an exception; the file is still cleaned.</para>
/// </remarks>
internal static class Exif
{
    private const ushort TagExifSubIfd = 0x8769;
    private const ushort TagGpsSubIfd = 0x8825;

    private static readonly Dictionary<ushort, (string Group, string Name, bool Sensitive)> Ifd0 = new()
    {
        [0x010F] = ("Camera", "Make", false),
        [0x0110] = ("Camera", "Model", false),
        [0x0131] = ("Camera", "Software", false),
        [0x0132] = ("Time", "Modified", true),
        [0x013B] = ("Person", "Photographer", true),
        [0x8298] = ("Person", "Copyright", true),
        [0x010E] = ("Person", "Image description", true),
    };

    private static readonly Dictionary<ushort, (string Group, string Name, bool Sensitive)> ExifIfd = new()
    {
        [0x9003] = ("Time", "Taken", true),
        [0x9004] = ("Time", "Digitized", true),
        [0xA431] = ("Camera", "Serial number", true),
        [0xA434] = ("Camera", "Lens", false),
        [0xA430] = ("Person", "Camera owner", true),
    };

    /// <summary>Reads the interesting fields from a TIFF block (APP1 content after "Exif\0\0").</summary>
    public static List<MetadataEntry> Read(ReadOnlySpan<byte> tiff)
    {
        var found = new List<MetadataEntry>();

        try
        {
            if (tiff.Length < 8) return found;

            var bigEndian = tiff[0] == 'M' && tiff[1] == 'M';
            if (!bigEndian && !(tiff[0] == 'I' && tiff[1] == 'I')) return found;

            var firstIfd = ReadUInt32(tiff[4..], bigEndian);
            if (firstIfd >= tiff.Length) return found;

            var subIfds = ReadIfd(tiff, (int)firstIfd, bigEndian, Ifd0, found);

            if (subIfds.Exif is { } exif && exif < tiff.Length)
                ReadIfd(tiff, exif, bigEndian, ExifIfd, found);

            if (subIfds.Gps is { } gps && gps < tiff.Length)
                ReadGps(tiff, gps, bigEndian, found);
        }
        catch (Exception)
        {
            // Incomplete EXIF is common, e.g. in an image whose transfer was cut off. It costs the preview, nothing more.
        }

        return found;
    }

    /// <summary>Reads a directory and returns the offsets of both subdirectories.</summary>
    private static (int? Exif, int? Gps) ReadIfd(
        ReadOnlySpan<byte> tiff, int offset, bool bigEndian,
        Dictionary<ushort, (string Group, string Name, bool Sensitive)> known,
        List<MetadataEntry> into)
    {
        int? exif = null, gps = null;

        var count = ReadUInt16(tiff[offset..], bigEndian);

        for (var i = 0; i < count; i++)
        {
            var entry = offset + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;

            var tag = ReadUInt16(tiff[entry..], bigEndian);
            var type = ReadUInt16(tiff[(entry + 2)..], bigEndian);
            var length = (int)ReadUInt32(tiff[(entry + 4)..], bigEndian);

            if (tag == TagExifSubIfd) exif = (int)ReadUInt32(tiff[(entry + 8)..], bigEndian);
            else if (tag == TagGpsSubIfd) gps = (int)ReadUInt32(tiff[(entry + 8)..], bigEndian);

            if (!known.TryGetValue(tag, out var meta)) continue;

            var value = ReadValue(tiff, entry + 8, type, length, bigEndian);
            if (value is not null) into.Add(new MetadataEntry(meta.Group, meta.Name, value, meta.Sensitive));
        }

        return (exif, gps);
    }

    /// <summary>The GPS block, resolved into something readable.</summary>
    /// <remarks>
    /// The location is stored as three fractions (degrees, minutes, seconds) plus a direction letter.
    /// Showing "47/1, 33/1, 2145/100" would be correct and useless: the point of this line is that someone
    /// recognizes <i>their home address is in there</i>.
    /// </remarks>
    private static void ReadGps(ReadOnlySpan<byte> tiff, int offset, bool bigEndian, List<MetadataEntry> into)
    {
        double? latitude = null, longitude = null;
        string latitudeRef = "N", longitudeRef = "E";

        var count = ReadUInt16(tiff[offset..], bigEndian);

        for (var i = 0; i < count; i++)
        {
            var entry = offset + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;

            var tag = ReadUInt16(tiff[entry..], bigEndian);
            var type = ReadUInt16(tiff[(entry + 2)..], bigEndian);
            var length = (int)ReadUInt32(tiff[(entry + 4)..], bigEndian);

            switch (tag)
            {
                case 1: latitudeRef = ReadValue(tiff, entry + 8, type, length, bigEndian) ?? "N"; break;
                case 3: longitudeRef = ReadValue(tiff, entry + 8, type, length, bigEndian) ?? "E"; break;
                case 2: latitude = ReadDegrees(tiff, entry + 8, length, bigEndian); break;
                case 4: longitude = ReadDegrees(tiff, entry + 8, length, bigEndian); break;
            }
        }

        if (latitude is not { } lat || longitude is not { } lon) return;

        if (latitudeRef.StartsWith('S')) lat = -lat;
        if (longitudeRef.StartsWith('W')) lon = -lon;

        into.Add(new MetadataEntry("Location", "GPS coordinates",
            $"{lat:0.000000}, {lon:0.000000}", Sensitive: true));
    }

    private static double? ReadDegrees(ReadOnlySpan<byte> tiff, int valueField, int count, bool bigEndian)
    {
        // Three 8-byte fractions never fit into the entry's four bytes, so it's always an offset.
        if (count != 3) return null;

        var at = (int)ReadUInt32(tiff[valueField..], bigEndian);
        if (at + 24 > tiff.Length) return null;

        var degrees = Rational(tiff, at, bigEndian);
        var minutes = Rational(tiff, at + 8, bigEndian);
        var seconds = Rational(tiff, at + 16, bigEndian);

        if (degrees is null || minutes is null || seconds is null) return null;

        return degrees + minutes / 60 + seconds / 3600;
    }

    private static double? Rational(ReadOnlySpan<byte> tiff, int at, bool bigEndian)
    {
        var numerator = ReadUInt32(tiff[at..], bigEndian);
        var denominator = ReadUInt32(tiff[(at + 4)..], bigEndian);

        return denominator == 0 ? null : (double)numerator / denominator;
    }

    private static string? ReadValue(
        ReadOnlySpan<byte> tiff, int valueField, ushort type, int count, bool bigEndian)
    {
        // Type 2 is ASCII; nothing else matters for display. Numeric EXIF fields (aperture, exposure,
        // focal length) reveal nothing about a person.
        if (type != 2 || count <= 0) return null;

        // Up to four bytes the value is in the entry itself, above that an offset.
        var at = count <= 4 ? valueField : (int)ReadUInt32(tiff[valueField..], bigEndian);
        if (at < 0 || at + count > tiff.Length) return null;

        var text = Encoding.Latin1.GetString(tiff.Slice(at, count)).TrimEnd('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(span)
        : BinaryPrimitives.ReadUInt16LittleEndian(span);

    private static uint ReadUInt32(ReadOnlySpan<byte> span, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(span)
        : BinaryPrimitives.ReadUInt32LittleEndian(span);
}
