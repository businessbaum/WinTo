using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>When a tweak shows: right away, after Explorer restarts, or with the next pointer move.</summary>
public enum TweakEffect
{
    Immediate,
    Explorer,
    Mouse,
}

/// <summary>One value a tweak sets, below <c>HKEY_CURRENT_USER</c>.</summary>
/// <param name="Default">What Windows uses when the value is missing; <c>null</c>: missing is the default.</param>
public sealed record TweakValue(string Key, string Name, RegistryValueKind Kind, object On, object? Default = null);

/// <summary>A tweak: what it sets and how it is described.</summary>
/// <param name="RemoveKey">A key to delete when switched off, if it didn't exist before.</param>
/// <param name="SettingsUri">Where Windows has the same switch.</param>
/// <param name="MachinePolicyId">A <see cref="MachinePolicy"/> that does the same when Windows refuses the
/// registry write from other programs; setting it needs one administrator prompt.</param>
public sealed record TweakDefinition(
    string Id,
    string Name,
    string Description,
    string IconPath,
    TweakEffect Effect,
    IReadOnlyList<TweakValue> Values,
    string? RemoveKey = null,
    string? SettingsUri = null,
    string? MachinePolicyId = null);

/// <summary>A switch that changes a Windows setting, and changes it back.</summary>
/// <remarks>
/// <para>
/// <b>The switch shows the registry, not a stored flag.</b> A tweak made by hand or by another tool shows as
/// on, and one undone in Windows' Settings shows as off.
/// </para>
/// <para>
/// <b>Off restores what was there.</b> Switching on stores the previous values in
/// <see cref="AppSettings.TweakBackups"/>; off writes them back, or Windows' defaults when there is no backup.
/// </para>
/// <para>
/// <b>Only <c>HKEY_CURRENT_USER</c></b>: nothing here needs administrator rights, and every change applies to
/// this account only.
/// </para>
/// </remarks>
public sealed partial class TweakFeature : Feature
{
    private readonly TweakDefinition _definition;
    private bool _pending;

    public TweakFeature(ShellViewModel shell, TweakDefinition definition) : base(shell)
    {
        _definition = definition;
        TweakOptions = definition.Effect == TweakEffect.Explorer || definition.SettingsUri is not null
            ? new TweakOptions(this, definition)
            : null;
        Detail = StateText();
        ExplorerRestart.Restarted += OnExplorerRestarted;
    }

    public override string Id => _definition.Id;
    public override string Name => _definition.Name;
    public override string Description => _definition.Description;
    public override string Group => "Declutter Windows";
    protected override string IconPath => _definition.IconPath;

    public TweakOptions? TweakOptions { get; }

    public override object? Options => TweakOptions;

    protected override bool ReadEnabled()
    {
        try
        {
            return _definition.Values.All(v => Same(Read(v.Key, v.Name), v.On))
                || (_definition.MachinePolicyId is { } policy && MachinePolicy.IsSet(policy));
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
            if (value) SwitchOn();
            else SwitchOff();

            Shell.SaveSettings();
            Announce();

            _pending = _definition.Effect == TweakEffect.Explorer;
            Detail = StateText();
        }
        catch (UnauthorizedAccessException) when (_definition.MachinePolicyId is { } policy)
        {
            // Windows protects some values against other programs (TaskbarDa since 2024); the policy is the
            // documented way and needs rights.
            _ = SetPolicyAsync(policy, value);
        }
        catch (UnauthorizedAccessException)
        {
            Detail = "Windows blocks this change.";
        }
        catch (Exception ex)
        {
            Detail = $"Not changed: {ex.Message}";
            new ModuleLog("features").Warn($"Tweak {Id}: {ex.Message}");
        }
    }

    /// <summary>Marker in <see cref="AppSettings.TweakBackups"/>: WinTo set the machine policy and may remove it.</summary>
    private string PolicyMarker => Id + ":policy";

    private async Task SetPolicyAsync(string policy, bool on)
    {
        Detail = "Waiting for the administrator prompt…";
        var problem = await MachinePolicy.SetAsync(policy, on);

        if (problem is null)
        {
            if (on) Settings.TweakBackups[PolicyMarker] = [];
            else Settings.TweakBackups.Remove(PolicyMarker);
            Shell.SaveSettings();
            Announce();
        }

        Detail = problem ?? StateText();
        OnPropertyChanged(nameof(IsEnabled));
        Apply();
    }

    protected override void Start() => Detail = StateText();

    protected override void Stop() => Detail = StateText();

    private string StateText() =>
        _pending ? "Applies after Explorer restarts. Options → Restart Explorer." : "";

    private void OnExplorerRestarted()
    {
        _pending = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Detail = StateText());
    }

    private void SwitchOn()
    {
        var hadBackup = Settings.TweakBackups.ContainsKey(Id);
        if (!hadBackup)
        {
            Settings.TweakBackups[Id] = [.. _definition.Values.Select(v =>
            {
                var (kind, value) = ReadRaw(v.Key, v.Name);
                return new RegistryBackup(v.Key, v.Name, kind?.ToString(), value);
            })];

            // A key created only for this tweak goes again when it is switched off.
            if (_definition.RemoveKey is { } key && !KeyExists(key))
                Settings.TweakBackups[Id].Add(new RegistryBackup(key, "*", null, null));
        }

        try
        {
            foreach (var value in _definition.Values) Write(value.Key, value.Name, value.Kind, value.On);
        }
        catch (Exception)
        {
            // Nothing was changed, so nothing is to be restored later.
            if (!hadBackup) Settings.TweakBackups.Remove(Id);
            throw;
        }
    }

    private void SwitchOff()
    {
        // Set through the policy earlier: that is what goes now.
        if (_definition.MachinePolicyId is { } policy && Settings.TweakBackups.ContainsKey(PolicyMarker))
        {
            _ = SetPolicyAsync(policy, false);
            Settings.TweakBackups.Remove(Id);
            return;
        }

        if (Settings.TweakBackups.Remove(Id, out var backup))
        {
            foreach (var entry in backup.Where(b => b.Name != "*"))
            {
                if (entry.Kind is null || !Enum.TryParse<RegistryValueKind>(entry.Kind, out var kind))
                    Delete(entry.Key, entry.Name);
                else
                    Write(entry.Key, entry.Name, kind, Parse(kind, entry.Value));
            }

            foreach (var created in backup.Where(b => b.Name == "*")) DeleteKey(created.Key);
            return;
        }

        foreach (var value in _definition.Values)
        {
            if (value.Default is null) Delete(value.Key, value.Name);
            else Write(value.Key, value.Name, value.Kind, value.Default);
        }
    }

    /// <summary>Tells Explorer and running programs that settings changed, as the Settings app does.</summary>
    private void Announce()
    {
        try
        {
            switch (_definition.Effect)
            {
                case TweakEffect.Mouse:
                    ApplyMouse();
                    break;
                default:
                    Broadcast("TraySettings");
                    Broadcast("Policy");
                    Broadcast(null);
                    SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
                    break;
            }
        }
        catch (Exception)
        {
            // The values are written; showing them may wait for the next sign-in.
        }
    }

    /// <summary>Mouse values only take effect through <c>SystemParametersInfo</c>.</summary>
    private static unsafe void ApplyMouse()
    {
        const string key = @"Control Panel\Mouse";
        var values = stackalloc int[3];
        values[0] = int.TryParse(Read(key, "MouseThreshold1") as string, out var t1) ? t1 : 6;
        values[1] = int.TryParse(Read(key, "MouseThreshold2") as string, out var t2) ? t2 : 10;
        values[2] = int.TryParse(Read(key, "MouseSpeed") as string, out var speed) ? speed : 1;
        SystemParametersInfoW(0x0004 /* SPI_SETMOUSE */, 0, values, 0x02 /* SPIF_SENDCHANGE */);
    }

    private static unsafe void Broadcast(string? area)
    {
        fixed (char* text = area)
        {
            SendMessageTimeoutW(0xFFFF, 0x001A /* WM_SETTINGCHANGE */, 0, (nint)text, 0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);
        }
    }

    // --------------------------------------------------------------- Registry

    private static object? Read(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static (RegistryValueKind? Kind, string? Value) ReadRaw(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        if (k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value) return (null, null);

        return (k.GetValueKind(name), Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static bool KeyExists(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k is not null;
    }

    private static void Write(string key, string name, RegistryValueKind kind, object value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
        k.SetValue(name, value, kind);
    }

    private static void Delete(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void DeleteKey(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

    private static object Parse(RegistryValueKind kind, string? value) => kind switch
    {
        RegistryValueKind.DWord => int.TryParse(value, CultureInfo.InvariantCulture, out var d) ? d : unchecked((int)uint.Parse(value ?? "0", CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(value ?? "0", CultureInfo.InvariantCulture),
        _ => value ?? "",
    };

    private static bool Same(object? current, object wanted) => current switch
    {
        null => false,
        int i when wanted is int w => i == w,
        string s when wanted is string w => string.Equals(s, w, StringComparison.Ordinal),
        _ => string.Equals(Convert.ToString(current, CultureInfo.InvariantCulture),
            Convert.ToString(wanted, CultureInfo.InvariantCulture), StringComparison.Ordinal),
    };

    [LibraryImport("user32.dll")]
    private static unsafe partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SystemParametersInfoW(uint action, uint param, void* data, uint winIni);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

/// <summary>Below a tweak that needs Explorer restarted, or that Windows may refuse.</summary>
public sealed partial class TweakOptions(TweakFeature feature, TweakDefinition definition) : ObservableObject
{
    public bool NeedsExplorer => definition.Effect == TweakEffect.Explorer;

    public bool HasSettings => definition.SettingsUri is not null;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool Busy { get; set; }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        Busy = true;
        Message = "Explorer is restarting…";
        Message = await ExplorerRestart.RunAsync() ?? "Explorer restarted.";
        Busy = false;
    }

    internal TweakFeature Feature => feature;

    [RelayCommand]
    private void OpenSettings()
    {
        if (definition.SettingsUri is not { } uri) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }
}

/// <summary>The tweaks offered, in the order of the Features page.</summary>
/// <remarks>
/// Picked from what people complain about most on Windows 11: web results and ads in Start, tips everywhere,
/// AI features nobody asked for, the shortened context menu, hidden file extensions.
/// </remarks>
internal static class TweakCatalog
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Content = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ExplorerPolicy = @"Software\Policies\Microsoft\Windows\Explorer";

    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public static IReadOnlyList<TweakDefinition> All { get; } =
    [
        new("tweak-web-search", "No web results in Start",
            "Start searches this computer only: no Bing results, no suggestions from the web.",
            "M10.5 3a7.5 7.5 0 1 0 4.55 13.46l4.24 4.25a1 1 0 0 0 1.42-1.42l-4.25-4.24A7.5 7.5 0 0 0 10.5 3Zm0 2a5.5 5.5 0 1 1 0 11 5.5 5.5 0 0 1 0-11Z",
            TweakEffect.Explorer,
            [
                new(ExplorerPolicy, "DisableSearchBoxSuggestions", RegistryValueKind.DWord, 1),
                new(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", RegistryValueKind.DWord, 0),
            ]),

        new("tweak-start-ads", "No recommendations in Start",
            "Start shows no suggested apps, tips or account reminders.",
            "M4 4h6v6H4V4Zm10 0h6v6h-6V4ZM4 14h6v6H4v-6Zm10 0h6v6h-6v-6Z",
            TweakEffect.Immediate,
            [
                new(Advanced, "Start_IrisRecommendations", RegistryValueKind.DWord, 0, 1),
                new(Advanced, "Start_AccountNotifications", RegistryValueKind.DWord, 0, 1),
                new(Content, "SubscribedContent-338388Enabled", RegistryValueKind.DWord, 0, 1),
                new(Content, "SystemPaneSuggestionsEnabled", RegistryValueKind.DWord, 0, 1),
            ],
            SettingsUri: "ms-settings:personalization-start"),

        new("tweak-tips", "No tips and nagging",
            "No tips in notifications, no \"finish setting up your device\" screen after updates, no welcome tour.",
            "M12 2a6 6 0 0 0-6 6v4l-2 3v1h16v-1l-2-3V8a6 6 0 0 0-6-6Zm-2 16a2 2 0 0 0 4 0h-4Z",
            TweakEffect.Immediate,
            [
                new(Content, "SubscribedContent-338389Enabled", RegistryValueKind.DWord, 0, 1),
                new(Content, "SubscribedContent-310093Enabled", RegistryValueKind.DWord, 0, 1),
                new(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", RegistryValueKind.DWord, 0, 1),
            ]),

        new("tweak-lock-screen", "No tips on the lock screen",
            "The lock screen shows the picture and the time, no facts and tips on top.",
            "M12 2a5 5 0 0 0-5 5v3H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7a5 5 0 0 0-5-5Zm-3 5a3 3 0 0 1 6 0v3H9V7Z",
            TweakEffect.Immediate,
            [
                new(Content, "RotatingLockScreenOverlayEnabled", RegistryValueKind.DWord, 0, 1),
                new(Content, "SubscribedContent-338387Enabled", RegistryValueKind.DWord, 0, 1),
            ]),

        new("tweak-copilot", "No Copilot button",
            "Removes the Copilot button from the taskbar.",
            "M12 2l2.2 5.8L20 10l-5.8 2.2L12 18l-2.2-5.8L4 10l5.8-2.2L12 2Zm7 12 1 2.5 2.5 1-2.5 1L19 21l-1-2.5-2.5-1 2.5-1L19 14Z",
            TweakEffect.Immediate,
            [
                new(Advanced, "ShowCopilotButton", RegistryValueKind.DWord, 0, 1),
                new(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", RegistryValueKind.DWord, 1),
            ]),

        new("tweak-recall", "No Recall snapshots",
            "Windows takes no snapshots of the screen for Recall on Copilot+ PCs.",
            "M3.3 2.3a1 1 0 0 0-1.4 1.4l3 3A11.6 11.6 0 0 0 1.5 11.6a1 1 0 0 0 0 .8C3.4 16.6 7.4 19 12 19c1.6 0 3.2-.3 4.6-.9l3.7 3.6a1 1 0 0 0 1.4-1.4L3.3 2.3ZM12 5c-1 0-2 .1-2.9.4l2.1 2.1a4.5 4.5 0 0 1 5.3 5.3l3.2 3.2a11.6 11.6 0 0 0 2.8-3.6 1 1 0 0 0 0-.8C20.6 7.4 16.6 5 12 5Zm-4.4 5.8a4.5 4.5 0 0 0 5.6 5.6l-5.6-5.6Z",
            TweakEffect.Explorer,
            [
                new(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", RegistryValueKind.DWord, 1),
            ]),

        new("tweak-widgets", "No widgets on the taskbar",
            "Removes the weather and news button from the taskbar.",
            "M4 4h7v9H4V4Zm9 0h7v5h-7V4Zm0 7h7v9h-7v-9ZM4 15h7v5H4v-5Z",
            TweakEffect.Immediate,
            [
                new(Advanced, "TaskbarDa", RegistryValueKind.DWord, 0, 1),
            ],
            SettingsUri: "ms-settings:taskbar",
            MachinePolicyId: MachinePolicy.NoWidgets),

        new("tweak-classic-menu", "Full right-click menu",
            "Explorer shows the full context menu right away, without \"Show more options\".",
            "M4 4h16v2H4V4Zm0 5h16v2H4V9Zm0 5h10v2H4v-2Zm0 5h10v2H4v-2Z",
            TweakEffect.Explorer,
            [
                new(ClassicMenuKey + @"\InprocServer32", "", RegistryValueKind.String, ""),
            ],
            RemoveKey: ClassicMenuKey),

        new("tweak-extensions", "Show file extensions",
            "Explorer shows \"report.pdf.exe\" instead of \"report.pdf\".",
            "M6 2h8l6 6v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Zm7 1.5V9h5.5L13 3.5ZM7 14h10v2H7v-2Zm0 4h7v2H7v-2Z",
            TweakEffect.Immediate,
            [
                new(Advanced, "HideFileExt", RegistryValueKind.DWord, 0, 1),
            ]),

        new("tweak-end-task", "End task in the taskbar",
            "Right-clicking a program in the taskbar offers \"End task\". Windows' own option, off by default.",
            "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm-3.3 5.3L12 10.6l3.3-3.3 1.4 1.4-3.3 3.3 3.3 3.3-1.4 1.4-3.3-3.3-3.3 3.3-1.4-1.4 3.3-3.3-3.3-3.3 1.4-1.4Z",
            TweakEffect.Immediate,
            [
                new(Advanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", RegistryValueKind.DWord, 1),
            ],
            SettingsUri: "ms-settings:developers"),

        new("tweak-mouse", "No mouse acceleration",
            "The pointer moves as far as the mouse, whatever the speed (\"Enhance pointer precision\" off).",
            "M12 2a6 6 0 0 0-6 6v8a6 6 0 0 0 12 0V8a6 6 0 0 0-6-6Zm-1 2.1V9H8V8a4 4 0 0 1 3-3.9Zm2 0A4 4 0 0 1 16 8v1h-3V4.1ZM8 11h8v5a4 4 0 0 1-8 0v-5Z",
            TweakEffect.Mouse,
            [
                new(@"Control Panel\Mouse", "MouseSpeed", RegistryValueKind.String, "0", "1"),
                new(@"Control Panel\Mouse", "MouseThreshold1", RegistryValueKind.String, "0", "6"),
                new(@"Control Panel\Mouse", "MouseThreshold2", RegistryValueKind.String, "0", "10"),
            ]),
    ];
}

/// <summary>Restarts Explorer the way installers do: Restart Manager asks it to close, then starts it again.</summary>
/// <remarks>
/// <para>
/// <b>Asked, never forced.</b> <c>RmShutdown</c> without the force flag; Explorer saves its state and closes,
/// and <c>RmRestart</c> starts it with the open folder windows. If it doesn't close, nothing else is tried
/// and signing out applies the change instead.
/// </para>
/// <para>
/// <b>Not while WinTo runs as administrator:</b> the restarted Explorer would run elevated too, and drag
/// and drop between it and normal programs stops working.
/// </para>
/// </remarks>
internal static unsafe partial class ExplorerRestart
{
    /// <summary>Raised after a successful restart, on a background thread.</summary>
    public static event Action? Restarted;

    /// <returns><c>null</c> on success, otherwise what happened.</returns>
    public static Task<string?> RunAsync() => Task.Run(Run);

    private static string? Run()
    {
        if (Elevation.IsElevated) return "Not restarted: WinTo runs as administrator. Signing out and in applies the change.";

        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return "Explorer is not running.";

        _ = GetWindowThreadProcessId(tray, out var pid);
        if (pid == 0) return "Explorer is not running.";

        long started;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            started = process.StartTime.ToFileTime();
        }
        catch (Exception ex)
        {
            return $"Explorer could not be read: {ex.Message}";
        }

        var key = stackalloc char[33];
        if (RmStartSession(out var session, 0, key) != 0) return "Restart Manager is not available.";

        try
        {
            var target = new UniqueProcess { ProcessId = pid, StartLow = (uint)started, StartHigh = (uint)(started >> 32) };
            if (RmRegisterResources(session, 0, 0, 1, &target, 0, 0) != 0) return "Explorer could not be registered for a restart.";

            if (RmShutdown(session, 0, 0) != 0) return "Explorer did not close. Signing out and in applies the change.";

            var restarted = RmRestart(session, 0, 0) == 0;
            if (!restarted || FindWindowW("Shell_TrayWnd", null) == 0)
            {
                // Restart Manager closed it but didn't bring it back: start the shell directly.
                Thread.Sleep(1500);
                if (FindWindowW("Shell_TrayWnd", null) == 0) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
            }

            Restarted?.Invoke();
            return null;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    /// <summary>RM_UNIQUE_PROCESS: a DWORD and a FILETIME, 12 bytes, aligned to 4.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct UniqueProcess
    {
        public uint ProcessId;
        public uint StartLow;
        public uint StartHigh;
    }

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmStartSession(out uint session, int flags, char* key);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmRegisterResources(uint session, uint files, nint fileNames, uint applications,
        UniqueProcess* processes, uint services, nint serviceNames);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmShutdown(uint session, uint flags, nint callback);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmRestart(uint session, int reserved, nint callback);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint session);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
