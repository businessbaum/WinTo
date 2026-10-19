using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiTool.Modules.TextTools;

/// <summary>The computing part, without UI and without state.</summary>
/// <remarks>
/// <para>
/// Every one of these conversions exists as a website, and that is the objection: pasting a JWT,
/// a config file or a hash into someone else's form means sending it away. Here nothing leaves the
/// computer.
/// </para>
/// <para>Pure functions only, so each can be checked against a known value without a UI.</para>
/// </remarks>
internal static class TextOps
{
    // ------------------------------------------------------------------ Checksums

    public static string Hash(string text, HashKind kind) =>
        Hash(Encoding.UTF8.GetBytes(text), kind);

    public static string Hash(byte[] data, HashKind kind)
    {
        var digest = kind switch
        {
            HashKind.Md5 => MD5.HashData(data),
            HashKind.Sha1 => SHA1.HashData(data),
            HashKind.Sha512 => SHA512.HashData(data),
            _ => SHA256.HashData(data),
        };

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Checksum of a file without loading it into memory.</summary>
    /// <remarks>A stream, not <c>File.ReadAllBytes</c>: an 8 GB disk image would otherwise be 8 GB in memory.</remarks>
    public static async Task<string> HashFileAsync(string path, HashKind kind, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);

        using HashAlgorithm algorithm = kind switch
        {
            HashKind.Md5 => MD5.Create(),
            HashKind.Sha1 => SHA1.Create(),
            HashKind.Sha512 => SHA512.Create(),
            _ => SHA256.Create(),
        };

        var digest = await algorithm.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    // ------------------------------------------------------------------ Base64

    public static string ToBase64(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Base64 back to text.</summary>
    /// <remarks>
    /// Missing padding is added. Base64 from a JWT or URL comes without it, and
    /// <c>FromBase64String</c> rejects that with "invalid length", which sounds like a broken value.
    /// </remarks>
    public static string FromBase64(string text)
    {
        var cleaned = text.Trim().Replace('-', '+').Replace('_', '/');
        var padding = cleaned.Length % 4;

        if (padding is 2 or 3) cleaned += new string('=', 4 - padding);

        return Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
    }

    // ------------------------------------------------------------------ URL

    public static string UrlEncode(string text) => Uri.EscapeDataString(text);

    public static string UrlDecode(string text) => Uri.UnescapeDataString(text);

    // ------------------------------------------------------------------ JSON

    /// <summary>Indents JSON. Throws with line and column if it isn't JSON.</summary>
    /// <remarks>
    /// <c>WriteIndented</c> alone is not enough: without
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> .NET writes non-ASCII characters and
    /// brackets as <c>\uXXXX</c>, the wrong output for a person to read.
    /// </remarks>
    public static string FormatJson(string text, bool indented)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    // ------------------------------------------------------------------ Time

    /// <summary>A point in time in both directions.</summary>
    /// <remarks>
    /// <para>
    /// <b>Seconds or milliseconds is decided by size.</b> A timestamp in seconds has ten digits today,
    /// one in milliseconds thirteen. Above 100000000000 it is milliseconds.
    /// </para>
    /// <para>Always prints both local time and UTC, so "which one?" never comes up.</para>
    /// </remarks>
    public static string DescribeTime(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return "";

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            var moment = Math.Abs(number) > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number);

            var unit = Math.Abs(number) > 100_000_000_000L ? "milliseconds" : "seconds";

            return $"""
                read as       {unit} since 1970
                local time    {moment.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.StandardName})
                UTC           {moment.UtcDateTime:yyyy-MM-dd HH:mm:ss}
                ISO 8601      {moment.ToLocalTime():o}
                """;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return $"""
                seconds       {parsed.ToUnixTimeSeconds()}
                milliseconds  {parsed.ToUnixTimeMilliseconds()}
                UTC           {parsed.UtcDateTime:yyyy-MM-dd HH:mm:ss}
                ISO 8601      {parsed:o}
                """;
        }

        return "Neither a number nor a readable date.";
    }

    public static string Now() => $"""
        seconds       {DateTimeOffset.Now.ToUnixTimeSeconds()}
        milliseconds  {DateTimeOffset.Now.ToUnixTimeMilliseconds()}
        local time    {DateTime.Now:yyyy-MM-dd HH:mm:ss}
        UTC           {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}
        ISO 8601      {DateTimeOffset.Now:o}
        """;

    // ------------------------------------------------------------------ IDs

    public static string Uuids(int count) =>
        string.Join(Environment.NewLine, Enumerable.Range(0, Math.Clamp(count, 1, 100))
            .Select(_ => Guid.NewGuid().ToString()));

    // ------------------------------------------------------------------ Count

    /// <summary>What is in a text.</summary>
    /// <remarks>
    /// <b>Characters and bytes separately.</b> "naïve" is five characters and six bytes in UTF-8, and a
    /// length limit on a field almost always means bytes.
    /// </remarks>
    public static string Count(string text)
    {
        var lines = text.Length == 0 ? 0 : text.Replace("\r\n", "\n").Split('\n').Length;
        var words = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        var withoutSpace = text.Count(c => !char.IsWhiteSpace(c));

        return $"""
            characters        {text.Length}
            without spaces    {withoutSpace}
            words             {words}
            lines             {lines}
            bytes in UTF-8    {Encoding.UTF8.GetByteCount(text)}
            """;
    }
}

public enum HashKind
{
    Md5,
    Sha1,
    Sha256,
    Sha512,
}
