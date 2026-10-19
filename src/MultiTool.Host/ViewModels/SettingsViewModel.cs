using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Host.Services;
using MultiTool.UI;

namespace MultiTool.Host.ViewModels;

/// <summary>The shell's settings page. Switches for features are on the Features page.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _loading = true;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;

        OpenInTab = shell.Settings.OpenMode == ModuleOpenMode.Tab;
        ReleaseOnClose = shell.Settings.ReleaseOnClose;
        ScanStatusOnStart = shell.Settings.ScanStatusOnStart;
        RunInBackground = shell.Settings.RunInBackground;
        OpenWindowOnStart = shell.Settings.OpenWindowOnStart;
        StartWithWindows = Autostart.IsEnabled;
        RunElevated = shell.Settings.RunElevated;
        RunElevatedText = DescribeElevated();
        if (RunElevated) _ = VerifyElevatedAsync();

        Overrides = [.. shell.Catalog.Modules.Select(m => new ModuleOpenModeEntry(shell, m.Id, m.Name))];

        Accents = [.. Accent.Presets.Select(p => new AccentChoice(p.Hex, p.Name, Color.Parse(p.Hex)))];
        if (Accent.WindowsColor() is { } windows) Accents.Add(new AccentChoice(Accent.Windows, "Windows accent color", windows));
        MarkAccent(shell.Settings.Accent);

        var notices = shell.Settings.Notifications;
        CornerIndex = (int)notices.Corner;
        var duration = Array.IndexOf(Durations, notices.Seconds);
        DurationIndex = duration >= 0 ? duration : 1;
        NoticeFollowPointer = notices.FollowPointer;
        NoticeLarge = notices.Large;
        NoticeAccentEdge = notices.AccentEdge;
        NoticeSound = notices.Sound;
        NoticeQuiet = notices.QuietInFullScreen;
        NoticeKinds =
        [
            new NoticeKindChoice(NoticeKind.Results, "Results from tools", "Screenshots, saved clips, copied text", this),
            new NoticeKindChoice(NoticeKind.Devices, "Devices and sound", "Headphones, sound output, microphones", this),
            new NoticeKindChoice(NoticeKind.Shortcuts, "Shortcut confirmations", "Always on top", this),
            new NoticeKindChoice(NoticeKind.System, "Warnings about this PC", "Low disk space, pending restart", this),
            new NoticeKindChoice(NoticeKind.App, "WinTo messages", "While the window is closed", this),
        ];

        _loading = false;
    }

    /// <summary>Open as tab or in its own window.</summary>
    /// <remarks>A <c>bool</c> because two radio buttons bind to it directly.</remarks>
    [ObservableProperty]
    public partial bool OpenInTab { get; set; }

    /// <summary>
    /// The other side of <see cref="OpenInTab"/> for the second radio button. Its setter writes only
    /// one field; the feedback goes through OnOpenInTabChanged, so the two bindings don't ping-pong.
    /// </summary>
    public bool OpenInWindow
    {
        get => !OpenInTab;
        set { if (value) OpenInTab = false; }
    }

    [ObservableProperty]
    public partial bool ReleaseOnClose { get; set; }

    [ObservableProperty]
    public partial bool ScanStatusOnStart { get; set; }

    public ObservableCollection<ModuleOpenModeEntry> Overrides { get; }

    public string DataDirectory => AppPaths.DataDirectory;

    public string VersionText
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            var elevated = Elevation.IsElevated ? " · as administrator" : "";
            return $"WinTo {version?.ToString(3) ?? "?"} · .NET {Environment.Version}{elevated}";
        }
    }

    partial void OnOpenInTabChanged(bool value)
    {
        OnPropertyChanged(nameof(OpenInWindow));

        // The guard comes first: the constructor sets OpenInTab before Overrides exists, and a
        // callback from a half-built object must not touch what is created later.
        if (_loading) return;

        Apply(s => s.OpenMode = value ? ModuleOpenMode.Tab : ModuleOpenMode.Window);

        // The rows below say "follows the default (…)" and must update.
        foreach (var entry in Overrides) entry.RefreshDefaultText();
    }

    partial void OnReleaseOnCloseChanged(bool value) => Apply(s => s.ReleaseOnClose = value);

    partial void OnScanStatusOnStartChanged(bool value) => Apply(s => s.ScanStatusOnStart = value);

    [ObservableProperty]
    public partial bool RunInBackground { get; set; }

    partial void OnRunInBackgroundChanged(bool value) => Apply(s => s.RunInBackground = value);

    [ObservableProperty]
    public partial bool OpenWindowOnStart { get; set; }

    partial void OnOpenWindowOnStartChanged(bool value) => Apply(s => s.OpenWindowOnStart = value);

    /// <summary>The swatches: presets, then the Windows color if it can be read.</summary>
    public ObservableCollection<AccentChoice> Accents { get; }

    /// <summary>The hex field: shows a custom color, takes one when it reads as <c>#RRGGBB</c>.</summary>
    [ObservableProperty]
    public partial string CustomAccent { get; set; } = "";

    [RelayCommand]
    private void ChooseAccent(AccentChoice? choice)
    {
        if (choice is null) return;

        // The default violet is stored as empty, so a later change of the default reaches it.
        SetAccent(choice.Key == Accent.Presets[0].Hex ? "" : choice.Key);
    }

    partial void OnCustomAccentChanged(string value)
    {
        if (_loading || _marking) return;

        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7 || !Color.TryParse(text, out var color)) return;

        SetAccent(AccentTheme.ToHex(color));
    }

    private void SetAccent(string setting)
    {
        Apply(s => s.Accent = setting);
        Accent.Apply(setting);
        MarkAccent(setting);
    }

    // --------------------------------------------------------------- Notifications

    /// <summary>In the order of <see cref="NoticeCorner"/>.</summary>
    public IReadOnlyList<string> Corners { get; } = ["Top right", "Top left", "Top center", "Bottom right", "Bottom left"];

    private static readonly int[] Durations = [3, 5, 8, 12, 20];

    public IReadOnlyList<string> DurationChoices { get; } = [.. Durations.Select(s => $"{s} seconds")];

    [ObservableProperty]
    public partial int CornerIndex { get; set; }

    [ObservableProperty]
    public partial int DurationIndex { get; set; }

    [ObservableProperty]
    public partial bool NoticeFollowPointer { get; set; }

    [ObservableProperty]
    public partial bool NoticeLarge { get; set; }

    [ObservableProperty]
    public partial bool NoticeAccentEdge { get; set; }

    [ObservableProperty]
    public partial bool NoticeSound { get; set; }

    [ObservableProperty]
    public partial bool NoticeQuiet { get; set; }

    public IReadOnlyList<NoticeKindChoice> NoticeKinds { get; } = [];

    partial void OnCornerIndexChanged(int value) =>
        Apply(s => s.Notifications.Corner = (NoticeCorner)Math.Clamp(value, 0, Corners.Count - 1));

    partial void OnDurationIndexChanged(int value) =>
        Apply(s => s.Notifications.Seconds = Durations[Math.Clamp(value, 0, Durations.Length - 1)]);

    partial void OnNoticeFollowPointerChanged(bool value) => Apply(s => s.Notifications.FollowPointer = value);

    partial void OnNoticeLargeChanged(bool value) => Apply(s => s.Notifications.Large = value);

    partial void OnNoticeAccentEdgeChanged(bool value) => Apply(s => s.Notifications.AccentEdge = value);

    partial void OnNoticeSoundChanged(bool value) => Apply(s => s.Notifications.Sound = value);

    partial void OnNoticeQuietChanged(bool value) => Apply(s => s.Notifications.QuietInFullScreen = value);

    internal bool IsHidden(NoticeKind kind) => _shell.Settings.Notifications.Hidden.Contains(kind);

    internal void SetHidden(NoticeKind kind, bool hidden) => Apply(s =>
    {
        s.Notifications.Hidden.Remove(kind);
        if (hidden) s.Notifications.Hidden.Add(kind);
    });

    /// <summary>A card with the settings as they are now, also for a kind that is switched off.</summary>
    [RelayCommand]
    private static void TestNotice() => Views.Toast.ShowTest();

    private bool _marking;

    /// <summary>Rings the chosen swatch; a color matching none goes into the hex field.</summary>
    private void MarkAccent(string setting)
    {
        var key = setting.Length == 0 ? Accent.Presets[0].Hex : setting;
        var found = false;

        foreach (var choice in Accents)
        {
            choice.IsSelected = string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase);
            found |= choice.IsSelected;
        }

        _marking = true;
        if (found) CustomAccent = "";
        else if (!string.Equals(CustomAccent.Trim().TrimStart('#'), key.TrimStart('#'), StringComparison.OrdinalIgnoreCase))
            CustomAccent = key;
        _marking = false;
    }

    [ObservableProperty]
    public partial bool ShowOverrides { get; set; }

    [RelayCommand]
    private void ToggleOverrides() => ShowOverrides = !ShowOverrides;

    [RelayCommand]
    private void ShowIntro() => _shell.ShowIntro();

    [RelayCommand]
    private void OpenDiagnostics() => _shell.OpenDiagnostics();

    // ------------------------------------------------------------------ run as administrator

    public bool CanRunElevated => ElevatedStart.IsSupported;

    [ObservableProperty]
    public partial bool RunElevated { get; set; }

    [ObservableProperty]
    public partial string RunElevatedText { get; set; } = "";

    [ObservableProperty]
    public partial bool RunElevatedProblem { get; set; }

    private bool _switchingElevated;

    partial void OnRunElevatedChanged(bool value)
    {
        if (_loading || _switchingElevated) return;
        _ = SwitchElevatedAsync(value);
    }

    /// <summary>Creates or removes the task; the prompt for it comes from Windows.</summary>
    private async Task SwitchElevatedAsync(bool on)
    {
        _switchingElevated = true;
        try
        {
            var problem = on ? await ElevatedStart.EnableAsync() : await ElevatedStart.DisableAsync();

            if (problem is not null)
            {
                RunElevated = !on;
                RunElevatedText = problem;
                RunElevatedProblem = true;
                return;
            }

            Apply(s => s.RunElevated = on);
            RunElevatedProblem = false;
            RunElevatedText = DescribeElevated();

            if (on && !Elevation.IsElevated
                && await _shell.Api.ConfirmAsync("Restart as administrator",
                    "WinTo restarts with administrator rights now. Open tools close.", "Restart")
                && ElevatedStart.RestartElevated())
            {
                App.Quit();
            }
        }
        finally
        {
            _switchingElevated = false;
        }
    }

    /// <summary>
    /// The setting without its task (removed by a reinstall or by hand) would show "on" while every start is
    /// unelevated. schtasks is a process start, so off the UI thread.
    /// </summary>
    private async Task VerifyElevatedAsync()
    {
        if (await Task.Run(ElevatedStart.Exists)) return;

        _switchingElevated = true;
        RunElevated = false;
        _switchingElevated = false;

        Apply(s => s.RunElevated = false);
        RunElevatedText = DescribeElevated();
    }

    private string DescribeElevated() =>
        !ElevatedStart.IsSupported ? "Only for the installed WinTo under Program Files."
        : RunElevated && !Elevation.IsElevated ? "On from the next start."
        : !RunElevated && Elevation.IsElevated ? "Off from the next start."
        : "Asks once. Files can't be dragged in from Explorer while elevated.";

    /// <summary>Lives in the registry, not in the settings file: the installer writes the same entry.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial string? AutostartProblem { get; set; }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        AutostartProblem = Autostart.Set(value);
    }

    private void Apply(Action<AppSettings> change)
    {
        if (_loading) return;
        change(_shell.Settings);
        _shell.SaveSettings();
    }

    /// <summary>Opens a folder in Explorer. For the path list at the bottom of the page.</summary>
    [RelayCommand]
    private static void RevealDirectory(string path)
    {
        // Process.Start off the UI thread (antivirus delay on first launch).
        _ = Task.Run(() =>
        {
            try
            {
                AppPaths.EnsureDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // A folder that can't be opened is no reason to crash.
            }
        });
    }
}

/// <summary>One kind of card, switched on or off under Notifications.</summary>
public sealed partial class NoticeKindChoice : ObservableObject
{
    private readonly SettingsViewModel _owner;

    internal NoticeKindChoice(NoticeKind kind, string name, string example, SettingsViewModel owner)
    {
        Kind = kind;
        Name = name;
        Example = example;
        _owner = owner;
        _isOn = !owner.IsHidden(kind);
    }

    public NoticeKind Kind { get; }

    public string Name { get; }

    public string Example { get; }

    private bool _isOn;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value)) return;
            _owner.SetHidden(Kind, !value);
        }
    }
}

/// <summary>A swatch on the settings page.</summary>
/// <param name="Key">What goes into <see cref="AppSettings.Accent"/>.</param>
public sealed partial class AccentChoice(string key, string name, Color color) : ObservableObject
{
    public string Key { get; } = key;

    public string Name { get; } = name;

    public IBrush Brush { get; } = new SolidColorBrush(color).ToImmutable();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One row of the override list: how this module opens.</summary>
/// <remarks>
/// Three options instead of two checkboxes: <b>Default</b> follows the setting above, the other two
/// fix it for this module.
/// </remarks>
public sealed partial class ModuleOpenModeEntry : ObservableObject
{
    private readonly ShellViewModel _shell;

    public ModuleOpenModeEntry(ShellViewModel shell, string moduleId, string name)
    {
        _shell = shell;
        ModuleId = moduleId;
        Name = name;

        _mode = shell.Settings.OpenModePerModule.TryGetValue(moduleId, out var stored)
            ? stored
            : null;
    }

    public string ModuleId { get; }

    public string Name { get; }

    /// <summary>Radio buttons need their own group per row, or one click clears every other row.</summary>
    public string GroupName => "openMode:" + ModuleId;

    /// <summary><c>null</c> means the default applies.</summary>
    private ModuleOpenMode? _mode;

    public bool IsDefault
    {
        get => _mode is null;
        set { if (value) Set(null); }
    }

    public bool IsTab
    {
        get => _mode == ModuleOpenMode.Tab;
        set { if (value) Set(ModuleOpenMode.Tab); }
    }

    public bool IsWindow
    {
        get => _mode == ModuleOpenMode.Window;
        set { if (value) Set(ModuleOpenMode.Window); }
    }

    /// <summary>What follows from it, shown in gray after the row.</summary>
    public string EffectiveText => _mode is null
        ? $"follows the default ({(_shell.Settings.OpenMode == ModuleOpenMode.Window ? "window" : "tab")})"
        : "";

    private void Set(ModuleOpenMode? mode)
    {
        if (_mode == mode) return;
        _mode = mode;

        if (mode is null) _shell.Settings.OpenModePerModule.Remove(ModuleId);
        else _shell.Settings.OpenModePerModule[ModuleId] = mode.Value;

        _shell.SaveSettings();

        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsTab));
        OnPropertyChanged(nameof(IsWindow));
        OnPropertyChanged(nameof(EffectiveText));
    }

    /// <summary>Called when the default above changes.</summary>
    public void RefreshDefaultText() => OnPropertyChanged(nameof(EffectiveText));
}
