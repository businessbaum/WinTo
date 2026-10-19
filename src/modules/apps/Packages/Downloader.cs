using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiTool.Modules.Apps.Packages;

/// <summary>How winget downloads its packages.</summary>
/// <remarks>
/// <para>
/// By default winget downloads via <i>Delivery Optimization</i>, the Windows service that also
/// distributes updates. When it stalls, winget doesn't notice: there is no timeout, and the tile
/// waits forever.
/// </para>
/// <para>
/// Measured on a 220 MB package: <b>17 minutes</b> at 89.9 %, no open connection, 0.6 s CPU in winget,
/// 0 s in the service, while the same server delivered <b>6.96 MB/s</b>.
/// </para>
/// <para>
/// <c>wininet</c> bypasses the service. The setting belongs to winget, so it is <b>offered, not set</b>.
/// </para>
/// </remarks>
internal static class Downloader
{
    /// <summary>The file winget itself opens via <c>winget settings</c>.</summary>
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "settings.json");

    /// <summary>Whether winget downloads directly.</summary>
    /// <remarks>A missing file and a missing field both mean <c>false</c>: the default is Delivery Optimization.</remarks>
    public static bool IsDirect()
    {
        try
        {
            return File.Exists(SettingsPath) && ReadsDirect(File.ReadAllText(SettingsPath));
        }
        catch (Exception)
        {
            // A broken or locked file means "not switched", the cautious answer: it offers the switch.
            return false;
        }
    }

    /// <summary>Whether this settings text names the direct downloader.</summary>
    public static bool ReadsDirect(string? json)
    {
        if (json is null || json.Trim().Length == 0) return false;

        try
        {
            return Parse(json)?["network"]?["downloader"]?.GetValue<string>() is "wininet";
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Adds the direct downloader to a settings text.</summary>
    /// <remarks>
    /// <b>Adds, doesn't replace:</b> winget keeps progress style, sources and telemetry in the same file;
    /// the existing tree is read and only this one field set.
    /// </remarks>
    public static string WithDirectDownloader(string? json)
    {
        var root = (json is { } text && text.Trim().Length > 0 ? Parse(text) : null) as JsonObject ?? [];

        if (root["network"] is not JsonObject network)
        {
            network = [];
            root["network"] = network;
        }

        network["downloader"] = "wininet";

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <remarks>
    /// Comments and trailing commas allowed: winget writes its file with explanatory lines, and users
    /// often leave a stray comma. Neither makes the file broken.
    /// </remarks>
    private static JsonNode? Parse(string json) => JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    });

    /// <summary>Switches winget to direct downloading.</summary>
    /// <returns>The reason if it failed, <c>null</c> on success.</returns>
    public static string? MakeDirect()
    {
        try
        {
            var previous = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;

            var folder = Path.GetDirectoryName(SettingsPath);
            if (folder is { Length: > 0 }) Directory.CreateDirectory(folder);

            File.WriteAllText(SettingsPath, WithDirectDownloader(previous));
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            // Don't overwrite: a broken file may hold settings the user wants to keep. They get the path.
            return $"winget's settings file is unreadable: {SettingsPath}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
