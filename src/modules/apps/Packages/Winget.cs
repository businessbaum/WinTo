using System.Text.RegularExpressions;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Apps.Packages;

/// <param name="Available">For an update the new version, otherwise empty.</param>
/// <param name="Source">The winget source the package comes from. Empty means none.</param>
public sealed record Package(string Name, string Id, string Version, string Available, string Source)
{
    /// <summary>Whether winget can manage this package.</summary>
    /// <remarks>
    /// <c>winget list</c> shows <b>everything</b> installed, including what came via its own installer, Steam
    /// or Uplay. Those have no source and ids like <c>ARP\Machine\X86\Uplay Install 5059</c>; they're
    /// listed because they're true, but winget can neither update nor remove them.
    /// </remarks>
    public bool IsManaged => Source.Length > 0;

    public bool HasUpdate => Available.Length > 0;

    public string VersionText => HasUpdate ? $"{Version}  →  {Available}" : Version;
}

/// <summary>A state from winget's progress line.</summary>
/// <remarks>
/// <b>Both numbers, not just the fraction:</b> 90 % of 3 MB versus 220 MB is the difference between
/// "almost done" and "this will take a while".
/// </remarks>
public readonly record struct DownloadProgress(double Done, double Total)
{
    public double Fraction => Total > 0 ? Math.Clamp(Done / Total, 0, 1) : 0;

    public string Text => $"{Humanize.Bytes(Done)} of {Humanize.Bytes(Total)}";
}

/// <summary>Working with <c>winget</c>: building commands, reading tables.</summary>
/// <remarks>
/// <para>
/// <b>Why the output is parsed.</b> winget has no machine-readable output for <c>list</c>, <c>search</c>
/// and <c>upgrade</c>; the COM API needs WinRT projections, and <c>winget export</c> only lists packages
/// with a source and without versions.
/// </para>
/// <para>
/// <b>Why that still works:</b> the table has fixed column widths. Headers are localized, their
/// <i>positions</i> aren't, so column boundaries are computed (see <c>ColumnStarts</c>) and each row cut
/// there. Splitting at spaces would fail: <c>ARP\Machine\X86\Uplay Install 5059</c> contains some.
/// </para>
/// <para>
/// Column meaning follows from position, not name: the first three are always name, id and
/// version, the last is always the source. In between is the available version (<c>list</c>,
/// <c>upgrade</c>) or the match reason (<c>search</c>).
/// </para>
/// </remarks>
internal static partial class Winget
{
    /// <summary>Arguments added to every call.</summary>
    /// <remarks>
    /// <c>--disable-interactivity</c> removes prompts and the spinner; without it a call without console
    /// eventually waits for input that never comes. The two <c>--accept-*</c> confirm the license and
    /// source notices, the same as answering <em>Y</em> in a console.
    /// </remarks>
    private static readonly string[] Common =
    [
        "--disable-interactivity",
        "--accept-source-agreements",
    ];

    public static string? Find(IModuleContext context) => context.Tools.Find("winget");

    public static async Task<string?> VersionAsync(string exe, CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(exe, ["--version"], cancellationToken: cancellationToken);
        return result.Ok ? result.Output.Trim() : null;
    }

    // ------------------------------------------------------------------ read

    public static Task<List<Package>> ListAsync(string exe, CancellationToken cancellationToken) =>
        TableAsync(exe, ["list", .. Common], cancellationToken);

    public static Task<List<Package>> UpgradableAsync(string exe, CancellationToken cancellationToken) =>
        TableAsync(exe, ["upgrade", .. Common], cancellationToken);

    public static Task<List<Package>> SearchAsync(string exe, string query, CancellationToken cancellationToken) =>
        TableAsync(exe, ["search", query, .. Common], cancellationToken);

    private static async Task<List<Package>> TableAsync(
        string exe, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        // Not RunAsync: winget draws its spinner with carriage returns, which shouldn't arrive as one
        // giant line. StreamAsync splits at \r and \n.
        await ExternalProcess.StreamAsync(exe, arguments,
            onOutput: line => { lock (lines) lines.Add(line); },
            cancellationToken: cancellationToken);

        lock (lines) return Parse(lines);
    }

    /// <summary>Reads a winget table. No header: empty list, no exception.</summary>
    public static List<Package> Parse(IReadOnlyList<string> lines)
    {
        var result = new List<Package>();

        // The header is the line above the separator. Everything before is spinner, source notices
        // or an ad for a newer winget version.
        var header = -1;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (IsRule(lines[i + 1]) && lines[i].Trim().Length > 0)
            {
                header = i;
                break;
            }
        }

        if (header < 0) return result;

        // The table ends at the first empty line. Footers like "38 upgrades available" follow and
        // aren't packages.
        var rows = new List<string>();
        for (var i = header + 2; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0) break;
            if (IsRule(lines[i])) break;
            rows.Add(lines[i]);
        }

        var columns = ColumnStarts(lines[header]);
        if (columns.Count < 4) return result;

        foreach (var line in rows)
        {
            if (line.Length <= columns[1]) continue;

            var fields = Slice(line, columns);

            var name = fields[0];
            var id = fields[1];
            if (name.Length == 0 || id.Length == 0) continue;

            // The last column is always the source; the one before is the available version or the
            // match reason. Not needed when searching ("Tag: 7zip", no version).
            var source = fields[^1];
            var middle = fields.Count >= 5 ? fields[3] : "";
            var available = middle.Contains(':') ? "" : middle;

            result.Add(new Package(name, id, fields[2], available, source));
        }

        return result;
    }

    private static bool IsRule(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 8 && trimmed.All(c => c == '-');
    }

    /// <summary>The column starts: the word starts of the header line.</summary>
    /// <remarks>
    /// <para>Two approaches were wrong, each in its own way, both measured:</para>
    /// <para>
    /// <b>Splitting the header at two or more spaces</b> works for <c>list</c> and <c>upgrade</c>. For
    /// <c>search</c> a localized header can have only <b>one</b> space between two columns; both merged, and
    /// the source read "Moniker: 7zip   winget".
    /// </para>
    /// <para>
    /// <b>Boundaries where every row has a space</b> sounds watertight but isn't: a package is called
    /// "Advanced Archive Password Recovery", and the spaces inside are empty in all other rows too. The
    /// name was split into three columns.
    /// </para>
    /// <para>
    /// What's left is the simplest: <b>every column starts where a header word starts.</b> That requires no
    /// two-word header, which holds for winget in every language.
    /// </para>
    /// </remarks>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();

        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] == ' ') continue;
            if (i == 0 || header[i - 1] == ' ') starts.Add(i);
        }

        return starts;
    }

    private static List<string> Slice(string line, List<int> starts)
    {
        var fields = new List<string>(starts.Count);

        for (var i = 0; i < starts.Count; i++)
        {
            var from = Math.Min(starts[i], line.Length);
            var to = i + 1 < starts.Count ? Math.Min(starts[i + 1], line.Length) : line.Length;

            fields.Add(line[from..to].Trim());
        }

        return fields;
    }

    // ------------------------------------------------------------------ act

    public static List<string> InstallArguments(string id) =>
    [
        "install", "--id", id,

        // Without --exact "7zip.7zip" also matches "7zip.7zip.Beta" and winget asks back, which means
        // nothing happens in a call without console.
        "--exact",
        "--accept-package-agreements",
        .. Common,
    ];

    public static List<string> UpgradeArguments(string id) =>
        ["upgrade", "--id", id, "--exact", "--accept-package-agreements", .. Common];

    public static List<string> UpgradeAllArguments() =>
        ["upgrade", "--all", "--accept-package-agreements", .. Common];

    public static List<string> UninstallArguments(string id) =>
        ["uninstall", "--id", id, "--exact", .. Common];

    /// <summary>Reads winget's progress line.</summary>
    /// <returns>0…1, or <c>null</c> if the line isn't one.</returns>
    /// <remarks>
    /// winget draws a bar of block characters followed by "12.3 MB / 99.0 MB". The bar itself is
    /// unreadable; the two numbers aren't.
    /// </remarks>
    public static DownloadProgress? ReadProgress(string line)
    {
        var match = ProgressLine().Match(line);
        if (!match.Success) return null;

        var done = Size(match.Groups["done"].Value, match.Groups["du"].Value);
        var total = Size(match.Groups["total"].Value, match.Groups["tu"].Value);

        return total > 0 ? new DownloadProgress(done, total) : null;

        static double Size(string number, string unit)
        {
            // winget writes the number in the system language, with a comma on some locales. Accepting
            // both is cheaper than picking one and landing in the wrong one.
            if (!double.TryParse(number.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            return unit.ToUpperInvariant() switch
            {
                "KB" => value * 1024,
                "MB" => value * 1024 * 1024,
                "GB" => value * 1024 * 1024 * 1024,
                _ => value,
            };
        }
    }

    [GeneratedRegex(
        @"(?<done>[\d.,]+)\s*(?<du>[KMG]?B)\s*/\s*(?<total>[\d.,]+)\s*(?<tu>[KMG]?B)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();
}
