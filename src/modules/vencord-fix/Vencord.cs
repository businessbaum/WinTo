namespace MultiTool.Modules.VencordFix;

/// <summary>The four files Vencord consists of.</summary>
/// <remarks>
/// <para>
/// Vencord is not a program but a set of JavaScript files under <c>%APPDATA%\Vencord\dist</c>. The
/// patch in Discord points to <c>patcher.js</c>, which loads the other three.
/// </para>
/// <para>
/// <b>No background updates:</b> Discord <c>require()</c>s <c>patcher.js</c> in the first seconds after
/// start, and overwriting it then risks a half-read patcher that looks like a Vencord bug. So
/// updating is a button pressed while Discord is closed.
/// </para>
/// </remarks>
internal static class Vencord
{
    private const string BaseUrl = "https://github.com/Vendicated/Vencord/releases/download/devbuild/";

    public static readonly string[] Files = ["patcher.js", "preload.js", "renderer.js", "renderer.css"];

    public static string DistDirectory => Path.Combine(Discord.VencordDataDirectory, "dist");

    /// <summary>When <c>patcher.js</c> was last written, or <c>null</c> if it is missing.</summary>
    public static DateTime? InstalledAt =>
        File.Exists(Discord.PatcherPath) ? new FileInfo(Discord.PatcherPath).LastWriteTime : null;

    /// <summary>Downloads the four files again.</summary>
    /// <returns>One line per file, for display.</returns>
    public static async Task<(bool Ok, List<string> Lines)> FetchAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var directory = DistDirectory;

        Directory.CreateDirectory(directory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Add("User-Agent", "WinTo");

        var ok = true;
        var staged = new List<(string Temporary, string Target)>();

        // Download all four first, then swap all four. Replacing each right after its download left a
        // new patcher.js next to an old renderer.js when the third failed, a mix that never existed
        // as a Vencord version.
        foreach (var name in Files)
        {
            var target = Path.Combine(directory, name);
            var temporary = target + ".download";

            try
            {
                var bytes = await http.GetByteArrayAsync(BaseUrl + name, cancellationToken);

                // An empty file is no file. GitHub occasionally returns a 200 without content during outages;
                // silently putting that over the working patcher would be the worst outcome.
                if (bytes.Length == 0) throw new IOException("The response was empty.");

                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                staged.Add((temporary, target));

                lines.Add($"{name} — {MultiTool.Abstractions.Humanize.Bytes(bytes.Length)}");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
                lines.Add($"{name} — failed: {ex.Message}");
                ok = false;
            }
        }

        if (!ok)
        {
            // Nothing is touched: the previous version stays complete.
            foreach (var (temporary, _) in staged)
            {
                try { File.Delete(temporary); } catch (Exception) { }
            }

            lines.Add("Previous version unchanged.");
            return (false, lines);
        }

        foreach (var (temporary, target) in staged)
        {
            try
            {
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception ex)
            {
                // Only a locked file can fail here: Discord is running.
                lines.Add($"{Path.GetFileName(target)} — not replaced: {ex.Message}");
                ok = false;
            }
        }

        // Without this file Node doesn't treat the folder as a package.
        var package = Path.Combine(directory, "package.json");
        if (!File.Exists(package)) await File.WriteAllTextAsync(package, "{}", cancellationToken);

        return (ok, lines);
    }
}
