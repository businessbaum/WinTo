using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Host.ControlCenter;
using MultiTool.Host.Features;
using MultiTool.Host.Services;
using MultiTool.UI;

namespace MultiTool.Host.ViewModels;

/// <summary>The first-start tour: eight pages over the main window, each one something to do.</summary>
/// <remarks>
/// <para>
/// <b>Hands on, not a slide show (0.13.0).</b> Every page switches the real thing: the accent recolors the app
/// at once, the switches are the features' own, and "try it" boxes tick themselves off when the shortcut is
/// pressed (<see cref="Feature.Used"/>) or the control center opens (<see cref="ControlCenterHost.Opened"/>).
/// </para>
/// <para>
/// Disposed by <see cref="ShellViewModel"/> when it is closed, which ends those subscriptions.
/// </para>
/// </remarks>
public sealed partial class IntroViewModel : ObservableObject, IDisposable
{
    private static readonly string[] Titles =
        ["Welcome", "Look", "Clipboard", "Screenshots", "Shortcuts", "Control center", "Declutter", "Tools"];

    private static readonly string[] ShortcutIds =
        ["text-from-screen", "color-picker", "always-on-top", "mic-mute", "output-switch"];

    private static readonly string[] DeclutterIds =
        ["tweak-web-search", "tweak-copilot", "tweak-tips", "tweak-classic-menu", "tweak-extensions", "tweak-end-task"];

    private readonly ShellViewModel _shell;
    private readonly Action _clipboardUsed;
    private readonly Action _screenshotUsed;
    private readonly Action _controlCenterOpened;
    private bool _loading = true;

    public IntroViewModel(ShellViewModel shell)
    {
        _shell = shell;

        Steps = [.. Titles.Select((title, index) => new IntroStep(index, title) { IsActive = index == 0 })];

        var features = shell.Features.All;
        Clipboard = features.OfType<ClipboardHistoryFeature>().First();
        Screenshot = features.OfType<ScreenshotShortcutFeature>().First();
        Shortcuts = [.. ShortcutIds.Select(id => features.FirstOrDefault(f => f.Id == id)).OfType<Feature>()];
        Declutter = [.. DeclutterIds.Select(id => features.FirstOrDefault(f => f.Id == id)).OfType<Feature>()];

        Accents = [.. Accent.Presets.Select(p => new AccentChoice(p.Hex, p.Name, Color.Parse(p.Hex)))];
        MarkAccent(shell.Settings.Accent);

        StartWithWindows = Autostart.IsEnabled;

        // The most relevant tools, as on the dashboard.
        Tools = [.. shell.Catalog.Modules.OrderBy(m => m.Order).Take(6)
            .Select(m => new IntroTool(m.Id, m.Name, m.Summary, IconCache.Get(m.Icon)))];

        _clipboardUsed = () => Dispatcher.UIThread.Post(() => ClipboardTried = true);
        _screenshotUsed = () => Dispatcher.UIThread.Post(() => ScreenshotTried = true);
        _controlCenterOpened = () => Dispatcher.UIThread.Post(() => ControlCenterTried = true);
        Clipboard.Used += _clipboardUsed;
        Screenshot.Used += _screenshotUsed;
        ControlCenterHost.Opened += _controlCenterOpened;

        _loading = false;
    }

    public const int PageCount = 8;

    [ObservableProperty]
    public partial int Page { get; set; }

    public bool IsFirst => Page == 0;

    public bool IsLast => Page == PageCount - 1;

    public string NextText => Page switch
    {
        0 => "Start the tour",
        PageCount - 1 => "Get started",
        _ => "Next",
    };

    public string StepText => $"Step {Page + 1} of {PageCount} · {Titles[Page]}";

    /// <summary>The bar along the top: one segment per page, lit up to the current one.</summary>
    public IReadOnlyList<IntroStep> Steps { get; }

    partial void OnPageChanged(int value)
    {
        OnPropertyChanged(nameof(IsFirst));
        OnPropertyChanged(nameof(IsLast));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(StepText));
        foreach (var step in Steps)
        {
            step.IsActive = step.Index == value;
            step.IsDone = step.Index < value;
        }

        if (value == PageCount - 1) OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLast) Close();
        else Page++;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirst) Page--;
    }

    [RelayCommand]
    private void GoTo(IntroStep? step)
    {
        if (step is not null) Page = step.Index;
    }

    [RelayCommand]
    private void Close() => _shell.CloseIntro();

    // --------------------------------------------------------------- Look

    public IReadOnlyList<AccentChoice> Accents { get; }

    [RelayCommand]
    private void ChooseAccent(AccentChoice? choice)
    {
        if (choice is null) return;

        // The default violet is stored as empty, as on the settings page.
        var setting = choice.Key == Accent.Presets[0].Hex ? "" : choice.Key;
        _shell.Settings.Accent = setting;
        _shell.SaveSettings();
        Accent.Apply(setting);
        MarkAccent(setting);
    }

    private void MarkAccent(string setting)
    {
        var key = setting.Length == 0 ? Accent.Presets[0].Hex : setting;
        foreach (var choice in Accents) choice.IsSelected = string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial string? AutostartProblem { get; set; }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;

        AutostartProblem = Autostart.Set(value);
        if (AutostartProblem is not null) StartWithWindows = Autostart.IsEnabled;
    }

    // --------------------------------------------------------------- Features to try

    public ClipboardHistoryFeature Clipboard { get; }

    public ScreenshotShortcutFeature Screenshot { get; }

    /// <summary>The clipboard panel was opened during the tour.</summary>
    [ObservableProperty]
    public partial bool ClipboardTried { get; set; }

    [ObservableProperty]
    public partial bool ScreenshotTried { get; set; }

    [ObservableProperty]
    public partial bool ControlCenterTried { get; set; }

    public IReadOnlyList<Feature> Shortcuts { get; }

    public IReadOnlyList<Feature> Declutter { get; }

    [RelayCommand]
    private void OpenControlCenter() => ControlCenterHost.Show(_shell);

    // --------------------------------------------------------------- Tools and the end

    public IReadOnlyList<IntroTool> Tools { get; }

    [RelayCommand]
    private void OpenTool(IntroTool? tool)
    {
        if (tool is null) return;

        Close();
        _shell.OpenModule(tool.Id);
    }

    /// <summary>What the tour left switched on, for the last page.</summary>
    public string Summary
    {
        get
        {
            var on = _shell.Features.All.Count(f => f.IsEnabled);
            var tried = new[] { ClipboardTried, ScreenshotTried, ControlCenterTried }.Count(t => t);
            return tried == 0
                ? $"{on} features on. All of them are under Features."
                : $"{on} features on, {tried} of 3 tried. All of them are under Features.";
        }
    }

    public void Dispose()
    {
        Clipboard.Used -= _clipboardUsed;
        Screenshot.Used -= _screenshotUsed;
        ControlCenterHost.Opened -= _controlCenterOpened;
    }
}

/// <summary>One segment of the tour's progress bar.</summary>
public sealed partial class IntroStep(int index, string title) : ObservableObject
{
    public int Index { get; } = index;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }
}

/// <summary>A tool on the last page; a click opens it and ends the tour.</summary>
public sealed record IntroTool(string Id, string Name, string Summary, Geometry Icon);
