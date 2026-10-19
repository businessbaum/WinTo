using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MultiTool.Abstractions;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;
using MultiTool.Host.Views;
using MultiTool.UI;

namespace MultiTool.Host.Features;

/// <summary>A card when a fixed drive runs low on space, once per drive and day.</summary>
/// <remarks>
/// Checked an hour apart and two minutes after start; a drive that recovers and runs low again is said again.
/// A click opens the storage tool's cleanup section. Removable and network drives are left out: they fill
/// up on purpose.
/// </remarks>
public sealed class LowDiskFeature(ShellViewModel shell) : Feature(shell)
{
    private const string DrivePath =
        "M4 5a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5Zm2 0v9h12V5H6Zm0 11v3h12v-3H6Zm9 1h2v1h-2v-1Z";

    private readonly Dictionary<string, DateTime> _said = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _timer;

    public override string Id => "low-disk";
    public override string Name => "Low disk space notice";
    public override string Description => "A card when a drive falls below 10 % free, with a way to clean up.";
    public override string Group => "Windows";
    protected override string IconPath => DrivePath;

    protected override bool ReadEnabled() => !Settings.Switches.TryGetValue(Id, out var on) || on;

    protected override void StoreEnabled(bool value) => Settings.Switches[Id] = value;

    protected override void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _timer.Tick += (_, _) => _ = CheckAsync();
        _timer.Start();
        DispatcherTimer.RunOnce(() => _ = CheckAsync(), TimeSpan.FromMinutes(2));
        Detail = "";
    }

    protected override void Stop()
    {
        _timer?.Stop();
        _timer = null;
        Detail = "";
    }

    private async Task CheckAsync()
    {
        if (_timer is null) return;

        var threshold = Math.Clamp(Settings.LowDiskPercent, 1, 50) / 100.0;
        var low = await Task.Run(() => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(d =>
            {
                try
                {
                    return d.IsReady && d.TotalSize > 0 ? (Drive: d.Name.TrimEnd('\\'), Free: d.AvailableFreeSpace, Total: d.TotalSize) : default;
                }
                catch (Exception)
                {
                    return default;
                }
            })
            .Where(d => d.Drive is not null && (double)d.Free / d.Total < threshold)
            .ToList());

        if (_timer is null) return;

        // Recovered drives may be said again later.
        foreach (var drive in _said.Keys.Except(low.Select(l => l.Drive)).ToList()) _said.Remove(drive);

        Detail = low.Count == 0 ? "" : $"Low: {string.Join(", ", low.Select(l => $"{l.Drive} {Humanize.Bytes(l.Free)} free"))}";

        foreach (var (drive, free, total) in low)
        {
            if (_said.TryGetValue(drive, out var at) && DateTime.Now - at < TimeSpan.FromDays(1)) continue;
            _said[drive] = DateTime.Now;

            Toast.Show(NoticeKind.System, $"Drive {drive} is almost full",
                $"{Humanize.Bytes(free)} of {Humanize.Bytes(total)} free · click to clean up",
                IconCache.Get(DrivePath),
                () =>
                {
                    App.ShowMainWindow();
                    Shell.OpenModule(ModuleIds.Storage, argument: "cleanup");
                });
        }
    }
}

/// <summary>Entries in Explorer's right-click menu that open a WinTo tool with the file or folder.</summary>
/// <remarks>
/// <para>
/// Written to <c>HKEY_CURRENT_USER\Software\Classes</c>, for this account only and without rights. The switch
/// shows whether the entries exist. On Windows 11 they appear under "Show more options", or directly with
/// the "Full right-click menu" tweak.
/// </para>
/// <para>
/// Each entry starts <c>WinTo.exe --open &lt;tool&gt; --with &lt;argument&gt;</c>; a running WinTo
/// receives it through the single-instance pipe.
/// </para>
/// <para>
/// <b>Default program (0.13.0).</b> The same switch registers WinTo as an application for pictures and videos
/// (ProgIDs, <c>Capabilities</c>, <c>RegisteredApplications</c>), so it appears in Settings → Default apps.
/// Making it the default is the user's choice there: since Windows 8 the choice is stored with a hash only
/// Windows writes, and a program that forges it gets reset by Windows on the next open.
/// </para>
/// </remarks>
public sealed partial class ExplorerMenuFeature(ShellViewModel shell) : Feature(shell)
{
    private const string MenuPath =
        "M4 4h16v2H4V4Zm0 5h10v2H4V9Zm0 5h16v2H4v-2Zm0 5h10v2H4v-2Zm14-10 4 2-4 2V9Z";

    private const string Classes = @"Software\Classes";

    /// <summary>The name under RegisteredApplications; the Settings deep link uses it.</summary>
    internal const string RegisteredName = "WinTo";

    /// <summary>Where, the verb key, the menu text, the tool and its argument (<c>%1</c> is the path).</summary>
    private static readonly (string Parent, string Verb, string Text, string Tool, string Argument)[] Entries =
    [
        (@"SystemFileAssociations\image\shell", "WinTo.Open", "Open in WinTo", ModuleIds.Photos, "%1"),
        (@"SystemFileAssociations\video\shell", "WinTo.Open", "Open in WinTo", ModuleIds.Video, "%1"),
        (@"SystemFileAssociations\image\shell", "WinTo.Convert", "Convert with WinTo", ModuleIds.Files, "convert:%1"),
        (@"SystemFileAssociations\image\shell", "WinTo.Metadata", "Remove metadata with WinTo", ModuleIds.Files, "metadata:%1"),
        (@"SystemFileAssociations\.pdf\shell", "WinTo.Metadata", "Remove metadata with WinTo", ModuleIds.Files, "metadata:%1"),
        (@"SystemFileAssociations\.docx\shell", "WinTo.Metadata", "Remove metadata with WinTo", ModuleIds.Files, "metadata:%1"),
        (@"SystemFileAssociations\.xlsx\shell", "WinTo.Metadata", "Remove metadata with WinTo", ModuleIds.Files, "metadata:%1"),
        (@"SystemFileAssociations\.pptx\shell", "WinTo.Metadata", "Remove metadata with WinTo", ModuleIds.Files, "metadata:%1"),
        (@"Directory\shell", "WinTo.DiskUsage", "Show disk usage in WinTo", ModuleIds.Storage, "%1"),
        (@"Drive\shell", "WinTo.DiskUsage", "Show disk usage in WinTo", ModuleIds.Storage, "%1"),
    ];

    /// <summary>Keys written under the former name (until 0.13.0), relative to <see cref="Classes"/>.</summary>
    private static readonly string[] Former =
    [
        @"SystemFileAssociations\image\shell\MultiTool.Edit",
        @"SystemFileAssociations\image\shell\MultiTool.Open",
        @"SystemFileAssociations\video\shell\MultiTool.Open",
        @"SystemFileAssociations\image\shell\MultiTool.Convert",
        @"SystemFileAssociations\image\shell\MultiTool.Metadata",
        @"SystemFileAssociations\.pdf\shell\MultiTool.Metadata",
        @"SystemFileAssociations\.docx\shell\MultiTool.Metadata",
        @"SystemFileAssociations\.xlsx\shell\MultiTool.Metadata",
        @"SystemFileAssociations\.pptx\shell\MultiTool.Metadata",
        @"Directory\shell\MultiTool.DiskUsage",
        @"Drive\shell\MultiTool.DiskUsage",
        @"Applications\MultiTool.exe",
    ];

    private readonly ExplorerMenuOptions _options = new();

    public override string Id => "explorer-menu";
    public override string Name => "Explorer menu and default apps";
    public override string Description =>
        "Right-click a picture or video to open it in WinTo, a folder to see what fills it. WinTo can then be chosen as the default app for pictures and videos.";
    public override string Group => "Windows";
    protected override string IconPath => MenuPath;

    public override object? Options => _options;

    protected override bool ReadEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{Entries[0].Parent}\{Entries[0].Verb}\command");
            return key?.GetValue(null) is string command && command.Contains(Exe(), StringComparison.OrdinalIgnoreCase)
                || FormerPresent();
        }
        catch (Exception)
        {
            return false;
        }
    }

    protected override void StoreEnabled(bool value)
    {
        // The registry is the state; see WriteEnabled.
    }

    protected override void WriteEnabled(bool value)
    {
        try
        {
            if (value) Register();
            else Unregister();
            Detail = "";
        }
        catch (Exception ex)
        {
            Detail = $"Not changed: {ex.Message}";
        }
    }

    // Nothing runs: the entries are in the registry. A copy in another folder shows the switch as off,
    // and switching it on points the entries at that copy. Entries from the MultiTool days count as on and
    // are rewritten once under the new name, or they would start an EXE that no longer exists.
    protected override void Start()
    {
        if (!FormerPresent()) return;

        try
        {
            Register();
        }
        catch (Exception ex)
        {
            Detail = $"Not updated: {ex.Message}";
        }
    }

    protected override void Stop()
    {
    }

    private static string Exe() => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinTo.exe");

    /// <summary>Under "Open with" for pictures and videos.</summary>
    private const string Application = @"Applications\WinTo.exe";

    private const string PictureProgId = "WinTo.Picture";
    private const string VideoProgId = "WinTo.Video";
    private const string Capabilities = @"Software\WinTo\Capabilities";
    private const string RegisteredApplications = @"Software\RegisteredApplications";

    private static readonly string[] PictureTypes =
    [
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
    ];

    private static readonly string[] VideoTypes =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".mpg", ".mpeg", ".ts", ".3gp",
    ];

    private static IEnumerable<(string Type, string ProgId)> Associations() =>
        PictureTypes.Select(t => (t, PictureProgId)).Concat(VideoTypes.Select(t => (t, VideoProgId)));

    private static bool FormerPresent()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{Former[1]}");
        return key is not null;
    }

    private static void Register()
    {
        var exe = Exe();
        var open = $"\"{exe}\" --open-file \"%1\"";
        var icon = $"\"{exe}\",0";

        foreach (var former in Former)
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{former}", throwOnMissingSubKey: false);

        foreach (var (parent, verb, text, tool, argument) in Entries)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{Classes}\{parent}\{verb}", writable: true);
            key.SetValue("MUIVerb", text);
            key.SetValue("Icon", icon);

            using var command = key.CreateSubKey("command", writable: true);
            command.SetValue(null, $"\"{exe}\" --open {tool} --with \"{argument}\"");
        }

        // "Open with": the program picks Photos or Video by the file's type (--open-file).
        using (var app = Registry.CurrentUser.CreateSubKey($@"{Classes}\{Application}", writable: true))
        {
            app.SetValue("FriendlyAppName", "WinTo");
            using (var appIcon = app.CreateSubKey("DefaultIcon", writable: true)) appIcon.SetValue(null, icon);
            using (var command = app.CreateSubKey(@"shell\open\command", writable: true)) command.SetValue(null, open);
            using var types = app.CreateSubKey("SupportedTypes", writable: true);
            foreach (var (type, _) in Associations()) types.SetValue(type, "");
        }

        // A file type per kind, which Settings → Default apps can assign.
        foreach (var (progId, name) in new[] { (PictureProgId, "Picture"), (VideoProgId, "Video") })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{Classes}\{progId}", writable: true);
            key.SetValue(null, $"{name} (WinTo)");
            key.SetValue("FriendlyTypeName", $"{name} (WinTo)");
            using (var typeIcon = key.CreateSubKey("DefaultIcon", writable: true)) typeIcon.SetValue(null, icon);
            using var command = key.CreateSubKey(@"shell\open\command", writable: true);
            command.SetValue(null, open);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(Capabilities, writable: true))
        {
            capabilities.SetValue("ApplicationName", "WinTo");
            capabilities.SetValue("ApplicationDescription", "Views and edits pictures and videos.");
            capabilities.SetValue("ApplicationIcon", icon);
            using var associations = capabilities.CreateSubKey("FileAssociations", writable: true);
            foreach (var (type, progId) in Associations()) associations.SetValue(type, progId);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredApplications, writable: true))
            registered.SetValue(RegisteredName, Capabilities);

        // Listed under "Open with" of each type; the current default stays untouched.
        foreach (var (type, progId) in Associations())
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{Classes}\{type}\OpenWithProgids", writable: true);
            key.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Explorer reads the associations once; this tells it they changed.
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
    }

    private static void Unregister()
    {
        foreach (var (parent, verb, _, _, _) in Entries)
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{parent}\{verb}", throwOnMissingSubKey: false);

        foreach (var former in Former)
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{former}", throwOnMissingSubKey: false);

        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{Application}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{PictureProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{VideoProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);

        using (var vendor = Registry.CurrentUser.OpenSubKey(@"Software\WinTo"))
            if (vendor is { SubKeyCount: 0, ValueCount: 0 })
                Registry.CurrentUser.DeleteSubKey(@"Software\WinTo", throwOnMissingSubKey: false);

        using (var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplications, writable: true))
            registered?.DeleteValue(RegisteredName, throwOnMissingValue: false);

        foreach (var (type, progId) in Associations())
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{type}\OpenWithProgids", writable: true);
            key?.DeleteValue(progId, throwOnMissingValue: false);
        }

        SHChangeNotify(0x08000000, 0, 0, 0);
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

/// <summary>Opens WinTo's page under Settings → Default apps, where its file types can be assigned.</summary>
public sealed partial class ExplorerMenuOptions : ObservableObject
{
    [ObservableProperty]
    public partial string? Message { get; set; }

    [RelayCommand]
    private void OpenDefaultApps()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={ExplorerMenuFeature.RegisteredName}")
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }
}
