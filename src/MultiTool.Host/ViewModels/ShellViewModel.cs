using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Host.Features;
using MultiTool.Host.Services;
using MultiTool.Host.Views;
using MultiTool.UI;

namespace MultiTool.Host.ViewModels;

/// <summary>The shell: home page, tabs, module windows, settings.</summary>
/// <remarks>
/// The only place a module is opened, and so the only place module code enters the process. As
/// long as only <see cref="OpenModule"/> loads, "nothing is preloaded" can be checked in one method.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Dictionary<string, Presentation> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One <see cref="IModule.ActivityChanged"/> subscription per <b>loaded</b> module, not per open view.</summary>
    /// <remarks>
    /// Until 0.9.0 the subscription went with the view. A module still working on close stayed loaded,
    /// its tile spun forever and it was never released, because nobody noticed it finished.
    /// </remarks>
    private readonly Dictionary<string, EventHandler<ModuleActivity>> _activityHandlers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modules whose view is closed and that are released once they are idle.</summary>
    private readonly HashSet<string> _releaseWhenIdle = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ShellService _shellService;
    private readonly DispatcherTimer _noticeTimer;

    public ShellViewModel()
    {
        Settings = JsonStore.Load(AppPaths.SettingsFile, static () => new AppSettings());

        // System.Text.Json creates a new dictionary and drops the comparer; without this the overrides
        // would depend on the module id's casing.
        var perModule = new Dictionary<string, ModuleOpenMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, openMode) in Settings.OpenModePerModule) perModule[id] = openMode;
        Settings.OpenModePerModule = perModule;

        Catalog = ModuleCatalog.Discover();

        _shellService = new ShellService(this);
        Modules = new ModuleManager(_shellService, _shutdown.Token);

        ToolList = new ToolsViewModel(this);
        Features = new FeatureHost(this);
        Dashboard = new DashboardViewModel(this);

        Pages =
        [
            new NavItem("home", "Dashboard", App.SharedIcon("IconHome")),
            new NavItem("features", "Features", IconCache.Get(FeaturesIconPath)),
            new NavItem("tools", "Tools", App.SharedIcon("IconGrid")),
            new NavItem("settings", "Settings", App.SharedIcon("IconSettings")),
        ];

        Tabs.CollectionChanged += (_, _) => SyncOpenTabs();

        Tabs.Add(CreateHomeTab());
        SelectedTab = Tabs[0];

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeText = null;
        };

        // The request belongs to the calling thread; the constructor runs on the UI thread, which lives
        // as long as the app.
        StayAwake.Set(Settings.KeepAwake);
    }

    private ShellTab CreateHomeTab() => new()
    {
        Key = "home",
        Title = "Dashboard",
        Content = new DashboardView { DataContext = Dashboard },
        CanClose = false,
        CanPopOut = false,
        Icon = IconCache.Get(HomeIconPath),
    };

    /// <summary>The four fixed pages, the first tabs in the header. Their tabs never show in <see cref="OpenTabs"/>.</summary>
    public IReadOnlyList<NavItem> Pages { get; }

    /// <summary>Open tools (and diagnostics) as tabs after the pages.</summary>
    public ObservableCollection<ShellTab> OpenTabs { get; } = [];

    [ObservableProperty]
    public partial bool HasOpenTabs { get; set; }

    private bool IsPage(ShellTab tab) => Pages.Any(p => p.Key == tab.Key);

    private void SyncOpenTabs()
    {
        OpenTabs.Clear();
        foreach (var tab in Tabs)
            if (!IsPage(tab)) OpenTabs.Add(tab);

        HasOpenTabs = OpenTabs.Count > 0;
    }

    [RelayCommand]
    private void NavigateTo(NavItem page) => Navigate(page.Key);

    /// <summary>Shows a page by key: home, features, tools, settings.</summary>
    public void Navigate(string key)
    {
        switch (key)
        {
            case "home":
                if (Tabs.FirstOrDefault(t => t.Key == "home") is { } home) SelectedTab = home;
                else EnsureHomeTab();
                break;
            case "features":
                OpenSpecialTab("features", "Features", null, () => new FeaturesView { DataContext = new FeaturesViewModel(this) },
                    page: true, icon: IconCache.Get(FeaturesIconPath));
                break;
            case "tools":
                OpenSpecialTab("tools", "Tools", "IconGrid", () => new ToolsView { DataContext = ToolList }, page: true);
                break;
            case "settings":
                OpenSettings();
                break;
        }
    }

    private const string FeaturesIconPath =
        "M12 2.5c.4 2.9 1.2 4.7 2.4 5.9 1.2 1.2 3 2 5.9 2.4v1.2c-2.9.4-4.7 1.2-5.9 2.4-1.2 1.2-2 3-2.4 5.9h-1.2c-.4-2.9-1.2-4.7-2.4-5.9-1.2-1.2-3-2-5.9-2.4v-1.2c2.9-.4 4.7-1.2 5.9-2.4 1.2-1.2 2-3 2.4-5.9h1.2ZM19 15.5c.2 1.5.6 2.4 1.2 3s1.5 1 3 1.2v.6c-1.5.2-2.4.6-3 1.2s-1 1.5-1.2 3h-.6c-.2-1.5-.6-2.4-1.2-3s-1.5-1-3-1.2v-.6c1.5-.2 2.4-.6 3-1.2s1-1.5 1.2-3h.6Z";

    /// <summary>Rebuilds the home tab after the window came back from the tray.</summary>
    public void EnsureHomeTab()
    {
        ToolList.Resume();

        if (Tabs.Any(t => t.Key == "home")) return;

        Tabs.Insert(0, CreateHomeTab());
        SelectedTab = Tabs[0];
    }

    /// <summary>
    /// The main window closed but the app stays in the tray: release every module and drop all views.
    /// </summary>
    /// <remarks>
    /// The home view is rebuilt on the next open, so nothing of the old visual tree stays alive.
    /// </remarks>
    public void EnterBackground()
    {
        foreach (var moduleId in _open.Keys.ToList()) Close(moduleId, forceRelease: true);
        foreach (var session in Modules.Loaded.ToList()) ReleaseModule(session.Manifest.Id, force: true);

        foreach (var tab in Tabs.ToList())
            if (tab.ModuleId is null && tab.Content.DataContext is IDisposable disposable) disposable.Dispose();

        SelectedTab = null;
        Tabs.Clear();
        MainWindow = null;

        _noticeTimer.Stop();
        NoticeText = null;

        ToolList.Suspend();
        ToolList.SyncRuntimeState(Modules);
        UpdateActivityText();
        SaveSettings();
    }

    private const string HomeIconPath =
        "M11.3 3.3a1 1 0 0 1 1.4 0l8 8a1 1 0 0 1-1.4 1.4l-.3-.3V19a2 2 0 0 1-2 2h-3v-5h-2v5H7a2 2 0 0 1-2-2v-6.6l-.3.3a1 1 0 1 1-1.4-1.4l8-8Z";

    public AppSettings Settings { get; }

    /// <summary>What modules get as <see cref="IShell"/>; features use the same dialogs and pickers.</summary>
    internal IShell Api => _shellService;

    public ModuleCatalog Catalog { get; }

    public ModuleManager Modules { get; }

    public ToolsViewModel ToolList { get; }

    /// <summary>The start page. Refreshed whenever it comes into view.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>What WinTo does without a window. See <see cref="Feature"/>.</summary>
    public FeatureHost Features { get; }

    public ObservableCollection<ShellTab> Tabs { get; } = [];

    /// <summary>The main window. Set once it exists; <c>null</c> while the app is in the tray.</summary>
    public Window? MainWindow { get; set; }

    [ObservableProperty]
    public partial ShellTab? SelectedTab { get; set; }

    partial void OnSelectedTabChanged(ShellTab? oldValue, ShellTab? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;

        foreach (var page in Pages) page.IsSelected = newValue?.Key == page.Key;
        if (newValue?.Key == "home") _ = Dashboard.RefreshAsync();

        // A tab switch detaches the old view. Only the shell knows whether that means "hidden" or
        // "closed"; OnDetachedFromVisualTree looks the same for both.
        if (oldValue?.ModuleId is { } gone) SetViewVisible(gone, false);
        if (newValue?.ModuleId is { } shown) SetViewVisible(shown, true);
    }

    /// <summary>
    /// Tells a module whether its view is visible. Repeated values are swallowed so modules can rely
    /// on pairs.
    /// </summary>
    internal void SetViewVisible(string moduleId, bool visible)
    {
        if (!_open.TryGetValue(moduleId, out var presentation)) return;
        if (presentation.ViewVisible == visible) return;

        presentation.ViewVisible = visible;

        try
        {
            if (visible) presentation.Session.Module.ViewActivated(presentation.View);
            else presentation.Session.Module.ViewDeactivated(presentation.View);
        }
        catch (Exception ex)
        {
            _shellService.Notify(
                $"{presentation.Session.Manifest.Name}: {ex.Message}", StatusLevel.Warn);
        }
    }

    [RelayCommand]
    private void SelectTab(ShellTab tab) => SelectedTab = tab;

    /// <summary>Footer text: what is running anywhere.</summary>
    [ObservableProperty]
    public partial string? ActivityText { get; set; }

    /// <summary>A footer notice, separate from <see cref="ActivityText"/>.</summary>
    /// <remarks>
    /// Until 0.9.0 <see cref="IShell.Notify"/> wrote into <see cref="ActivityText"/>, where the next
    /// activity overwrote it or it stayed forever. Now it has its own place, a color by severity and
    /// disappears after ten seconds.
    /// </remarks>
    [ObservableProperty]
    public partial string? NoticeText { get; set; }

    [ObservableProperty]
    public partial StatusLevel NoticeLevel { get; set; }

    public bool NoticeIsWarn => NoticeLevel == StatusLevel.Warn;

    public bool NoticeIsBad => NoticeLevel == StatusLevel.Bad;

    public bool NoticeIsGood => NoticeLevel == StatusLevel.Good;

    /// <summary>The footer shows as soon as either has something to say.</summary>
    public bool HasFooter => ActivityText is { Length: > 0 } || NoticeText is { Length: > 0 };

    public bool HasActivity => ActivityText is { Length: > 0 };

    public bool HasNotice => NoticeText is { Length: > 0 };

    partial void OnActivityTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFooter));
        OnPropertyChanged(nameof(HasActivity));
    }

    partial void OnNoticeTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFooter));
        OnPropertyChanged(nameof(HasNotice));
    }

    partial void OnNoticeLevelChanged(StatusLevel value)
    {
        OnPropertyChanged(nameof(NoticeIsWarn));
        OnPropertyChanged(nameof(NoticeIsBad));
        OnPropertyChanged(nameof(NoticeIsGood));
    }

    /// <summary>The card at the top right; a click opens WinTo and the named module. UI thread only.</summary>
    public void ShowAnnouncement(Announcement announcement) => Toast.Show(announcement, () =>
    {
        App.ShowMainWindow();
        if (announcement.OpenModuleId is { } id)
            OpenModule(id, argument: announcement.OpenArgument);
    });

    /// <summary>Shows a footer notice. UI thread only.</summary>
    public void ShowNotice(string message, StatusLevel level = StatusLevel.Neutral)
    {
        // Without a visible window the footer reaches nobody.
        if (MainWindow is not { IsVisible: true }) Toast.Show(message, level);

        NoticeLevel = level;
        NoticeText = message;

        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer.Stop();
        NoticeText = null;
    }

    /// <summary>A second instance was started and handed over.</summary>
    /// <param name="moduleId">The module from <c>--open</c>, otherwise <c>null</c>.</param>
    public void ReceiveForwarded(string? moduleId, string? argument = null)
    {
        App.ShowMainWindow();
        if (moduleId is { Length: > 0 }) OpenModule(moduleId, argument: argument);
    }

    public bool IsElevated => Elevation.IsElevated;

    /// <summary>Whether the computer is kept awake. Shared by the header button and the settings page.</summary>
    public bool KeepAwake => Settings.KeepAwake;

    /// <summary>Toggles keep-awake. Applies immediately and is saved.</summary>
    [RelayCommand]
    public void ToggleKeepAwake() => SetKeepAwake(!Settings.KeepAwake);

    /// <summary>The one way this changes.</summary>
    /// <remarks>Header, settings page and control center all call here, so saving and notifying can't be forgotten.</remarks>
    public void SetKeepAwake(bool value)
    {
        Settings.KeepAwake = value;
        StayAwake.Set(value);
        SaveSettings();

        OnPropertyChanged(nameof(KeepAwake));
        KeepAwakeChanged?.Invoke(this, value);
    }

    /// <summary>So an open settings page follows.</summary>
    public event EventHandler<bool>? KeepAwakeChanged;

    // ================= Opening modules =================

    /// <summary>Opens a module. <paramref name="mode"/> overrides the setting once; the tile offers both.</summary>
    /// <param name="quiet">
    /// From a global shortcut: no window comes forward. A module that isn't open is loaded without a view,
    /// gets the argument, and is released once it reports idle (see <see cref="_closeWhenIdle"/>).
    /// </param>
    public void OpenModule(string moduleId, ModuleOpenMode? mode = null, string? argument = null, bool quiet = false)
    {
        var manifest = Catalog.Find(moduleId);
        if (manifest is null)
        {
            _shellService.Notify($"Unknown module: {moduleId}", StatusLevel.Bad);
            return;
        }

        if (quiet)
        {
            OpenQuietly(manifest, argument);
            return;
        }

        // A module needing rights this process lacks restarts WinTo elevated (see
        // ShellService.RunElevatedAsync).
        if (manifest.Elevation == ElevationRequirement.Required && !Elevation.IsElevated)
        {
            _ = _shellService.RunElevatedAsync(manifest.Id);
            return;
        }

        // Already open: bring it forward instead of opening twice.
        if (_open.TryGetValue(moduleId, out var existing))
        {
            Reveal(existing);
            HandOver(existing.Session, argument);
            return;
        }

        // A missing mandatory tool is asked about (and fetched) before the module DLL is touched.
        // The check itself is a few File.Exists; only a real gap becomes a dialog.
        if (ToolGate.IsMissingAnything(manifest))
        {
            _ = OpenAfterGateAsync(manifest, mode, argument);
            return;
        }

        Load(manifest, mode, argument);
    }

    /// <summary>Modules loaded without a view by <see cref="OpenQuietly"/>; released when they go idle.</summary>
    private readonly HashSet<string> _closeWhenIdle = new(StringComparer.OrdinalIgnoreCase);

    private void OpenQuietly(ModuleManifest manifest, string? argument)
    {
        // Already loaded, with or without a view: just hand over.
        if (Modules.Find(manifest.Id) is { } loaded)
        {
            HandOver(loaded, argument);
            return;
        }

        // Missing tools need a dialog, and that needs the normal way.
        if (ToolGate.IsMissingAnything(manifest) || manifest.Elevation == ElevationRequirement.Required)
        {
            OpenModule(manifest.Id, argument: argument);
            return;
        }

        ModuleSession session;
        try
        {
            session = Modules.Load(manifest);
        }
        catch (ModuleLoadException ex)
        {
            ShowNotice($"{manifest.Name}: {ex.Message}", StatusLevel.Bad);
            return;
        }

        Subscribe(session);
        _closeWhenIdle.Add(manifest.Id);
        ToolList.SyncRuntimeState(Modules);
        HandOver(session, argument);
    }

    /// <summary>Asks about missing tools, then opens, or doesn't.</summary>
    private async Task OpenAfterGateAsync(ModuleManifest manifest, ModuleOpenMode? mode, string? argument)
    {
        if (!await ToolGate.EnsureAsync(manifest, _shellService.OwnerWindow)) return;

        // The user may have clicked the tile again between dialog and open.
        if (_open.TryGetValue(manifest.Id, out var opened))
        {
            Reveal(opened);
            HandOver(opened.Session, argument);
            return;
        }

        Load(manifest, mode, argument);
    }

    /// <summary>Passes an argument to the module. See <see cref="IModule.OpenWith"/>.</summary>
    private void HandOver(ModuleSession session, string? argument)
    {
        if (argument is null) return;

        try
        {
            session.Module.OpenWith(argument);
        }
        catch (Exception ex)
        {
            ShowNotice($"{session.Manifest.Name}: {ex.Message}", StatusLevel.Warn);
        }
    }

    private void Load(ModuleManifest manifest, ModuleOpenMode? mode, string? argument)
    {
        var moduleId = manifest.Id;

        ModuleSession session;
        try
        {
            session = Modules.Load(manifest);
        }
        catch (ModuleLoadException ex)
        {
            _ = _shellService.AlertAsync($"{manifest.Name} could not be opened", ex.Message);
            return;
        }

        // A reopened module that was still working must no longer be released when it finishes.
        _releaseWhenIdle.Remove(moduleId);
        _closeWhenIdle.Remove(moduleId);
        Subscribe(session);

        Control view;
        try
        {
            view = session.Module.CreateView();
        }
        catch (Exception ex)
        {
            ReleaseModule(manifest.Id, force: true);
            _ = _shellService.AlertAsync($"{manifest.Name} could not be shown", ex.Message);
            return;
        }

        session.View = view;

        var presentation = new Presentation(session, view);
        _open[moduleId] = presentation;

        var resolved = mode ?? ResolveOpenMode(moduleId);
        if (resolved == ModuleOpenMode.Window)
        {
            ShowAsWindow(presentation);
        }
        else
        {
            // Opened from the tray while in the background: a tab needs the main window.
            App.ShowMainWindow();
            ShowAsTab(presentation);
        }

        ToolList.SyncRuntimeState(Modules);
        HandOver(session, argument);
    }

    /// <summary>Subscribes the shell to a loaded module's activity.</summary>
    private void Subscribe(ModuleSession session)
    {
        var moduleId = session.Manifest.Id;
        if (_activityHandlers.ContainsKey(moduleId)) return;

        EventHandler<ModuleActivity> handler = (_, activity) =>
            Dispatcher.UIThread.Post(() => OnActivityChanged(moduleId, activity));

        session.Module.ActivityChanged += handler;
        _activityHandlers[moduleId] = handler;
    }

    /// <summary>Releases a module and drops the subscription. The only way, so no subscription outlives its module.</summary>
    private bool ReleaseModule(string moduleId, bool force = false)
    {
        var session = Modules.Find(moduleId);
        if (session is null) return true;

        // Unsubscribe first: Dispose often reports a final "idle", which must not reach a callback
        // that wants to release again.
        if (_activityHandlers.Remove(moduleId, out var handler))
            session.Module.ActivityChanged -= handler;

        if (Modules.Release(moduleId, force))
        {
            _releaseWhenIdle.Remove(moduleId);
            return true;
        }

        // Not released because it is working: subscribe again so its end arrives.
        Subscribe(session);
        return false;
    }

    public ModuleOpenMode ResolveOpenMode(string moduleId) =>
        Settings.OpenModePerModule.TryGetValue(moduleId, out var perModule) ? perModule : Settings.OpenMode;

    private void ShowAsTab(Presentation presentation)
    {
        var manifest = presentation.Session.Manifest;

        var tab = new ShellTab
        {
            Key = "module:" + manifest.Id,
            ModuleId = manifest.Id,
            Title = manifest.Name,
            Icon = IconCache.Get(manifest.Icon),
            Content = presentation.View,
        };

        presentation.Tab = tab;
        Tabs.Add(tab);
        SelectedTab = tab;              // raises SetViewVisible(..., true)
    }

    private void ShowAsWindow(Presentation presentation)
    {
        var window = new ModuleWindow(this, presentation.Session.Manifest, presentation.View);
        presentation.Window = window;

        var moduleId = presentation.Session.Manifest.Id;

        // For a window the question must come BEFORE closing, so cancel, ask, and finish from the
        // shell. No second pass: Close() clears the mapping before calling window.Close().
        window.Closing += (_, e) =>
        {
            if (presentation.Window != window) return;

            e.Cancel = true;
            _ = CloseWithPromptAsync(moduleId);
        };

        window.Closed += (_, _) =>
        {
            // In case the window disappears some other way.
            if (presentation.Window == window) Close(moduleId);
        };

        window.Show();
        SetViewVisible(presentation.Session.Manifest.Id, true);
    }

    private void Reveal(Presentation presentation)
    {
        if (presentation.Window is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        else if (presentation.Tab is { } tab)
        {
            SelectedTab = tab;
            MainWindow?.Activate();
        }
    }

    // ================= Switching between tab and window =================

    /// <summary>
    /// Pops a tab out into its own window with the <b>same</b> view; a running download keeps going.
    /// </summary>
    [RelayCommand]
    public async Task PopOutAsync(ShellTab tab)
    {
        if (tab.ModuleId is null || !_open.TryGetValue(tab.ModuleId, out var presentation)) return;

        // Order matters: deselect first so the main window releases the view, remove the tab, then
        // wait one dispatcher turn (see YieldTurnAsync).
        var index = Tabs.IndexOf(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count > 1 ? Tabs[index == 0 ? 1 : index - 1] : null;

        Tabs.Remove(tab);
        presentation.Tab = null;

        await YieldTurnAsync();

        ShowAsWindow(presentation);
        SelectedTab ??= Tabs.FirstOrDefault();
    }

    /// <summary>Docks a window back as a tab.</summary>
    public async Task DockWindowAsync(string moduleId)
    {
        if (!_open.TryGetValue(moduleId, out var presentation) || presentation.Window is not { } window) return;

        // Clear the mapping first so the Closed handler doesn't read this as closing.
        presentation.Window = null;
        window.DetachContent();
        window.Close();

        await YieldTurnAsync();

        ShowAsTab(presentation);
        MainWindow?.Activate();
    }

    /// <summary>Waits one dispatcher turn.</summary>
    /// <remarks>
    /// Moving a control from an open window into another window's tree in the <b>same</b> turn kills the
    /// process on the next render (<c>Attempt to call InvalidateArrange on wrong LayoutManager</c>),
    /// uncatchable. One layout pass in between clears the old LayoutManager.
    /// </remarks>
    private static async Task YieldTurnAsync() =>
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    // ================= Closing =================

    [RelayCommand]
    public async Task CloseTabAsync(ShellTab tab)
    {
        if (!tab.CanClose) return;

        if (tab.ModuleId is null)
        {
            Tabs.Remove(tab);
            if (SelectedTab == tab) SelectedTab = Tabs.FirstOrDefault();

            // Special pages have no module lifecycle; unsubscribe whatever their constructor subscribed.
            if (tab.Content.DataContext is IDisposable disposable) disposable.Dispose();
            return;
        }

        await CloseWithPromptAsync(tab.ModuleId);
    }

    /// <summary>Asks before closing, and keeps the tab if declined.</summary>
    /// <remarks>
    /// <para>
    /// <b>There is no "closed but still running".</b> Keep open keeps the tab or window; close releases
    /// the module. The old third state (tab gone, module loaded, hotkeys live) was invisible.
    /// </para>
    /// <para>
    /// Asked only when closing would lose something: running work (<see cref="ModuleActivity.IsBusy"/>)
    /// or results that live only in memory (<see cref="IModule.HasUnsavedWork"/>). Most actions are saved
    /// the moment they happen, and a question that is almost always "yes" trains people to click it away.
    /// </para>
    /// <para>Lives in the shell because only the shell knows what closing means here.</para>
    /// </remarks>
    public async Task CloseWithPromptAsync(string moduleId)
    {
        if (!_open.TryGetValue(moduleId, out var presentation)) return;

        var manifest = presentation.Session.Manifest;

        var module = presentation.Session.Module;
        var activity = module.Activity;
        var unsaved = HasUnsavedWork(module);

        if (activity.IsBusy || unsaved)
        {
            var prompt = manifest.ClosePrompt;

            var what = activity.IsBusy
                ? $"{manifest.Name} is working{(activity.Text is { Length: > 0 } text ? ": " + text.TrimEnd('…', '.') : "")}."
                : $"{manifest.Name} has results that aren't saved.";

            var hint = prompt?.Hint
                ?? "Keep open keeps the module and its state. "
                   + "Close returns devices, threads and memory.";

            var keepOpen = await MessageDialog.ChooseAsync(
                _shellService.OwnerWindow,
                "Close " + manifest.Name,
                $"{what}{Environment.NewLine}{Environment.NewLine}{hint}",
                primaryText: prompt?.Keep ?? "Keep open",
                secondaryText: prompt?.Close ?? "Close");

            // Keep open means doing nothing; for a window the e.Cancel in the Closing handler applies.
            if (keepOpen)
            {
                Reveal(presentation);
                return;
            }

            // Closing here always releases too, or the forbidden state would remain.
            Close(moduleId, forceRelease: true);
            return;
        }

        Close(moduleId, forceRelease: manifest.KeepsRunning);
    }

    /// <summary>A module's own answer; an exception from module code counts as "nothing to lose".</summary>
    private static bool HasUnsavedWork(IModule module)
    {
        try
        {
            return module.HasUnsavedWork;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>What closing everything would lose, one line per module. Empty means close without asking.</summary>
    public IReadOnlyList<string> PendingLosses() =>
        Modules.Loaded
            .Select(s => s.Module.Activity.IsBusy
                ? $"{s.Manifest.Name}: {(s.Module.Activity.Text is { Length: > 0 } text ? text.TrimEnd('…', '.') : "working")}"
                : HasUnsavedWork(s.Module) ? $"{s.Manifest.Name}: results not saved" : null)
            .OfType<string>()
            .OrderBy(line => line, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Closes a module's view and releases it if it is done.</summary>
    public void Close(string moduleId, bool forceRelease = false)
    {
        if (!_open.Remove(moduleId, out var presentation)) return;

        var session = presentation.Session;

        if (presentation.Tab is { } tab)
        {
            var index = Tabs.IndexOf(tab);
            Tabs.Remove(tab);
            if (SelectedTab == tab)
                SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        if (presentation.Window is { } window)
        {
            presentation.Window = null;
            window.DetachContent();
            window.Close();
        }

        // Hidden first, then closed; a module never sees the order reversed.
        presentation.ViewVisible = false;

        try
        {
            session.Module.ViewDeactivated(presentation.View);
        }
        catch (Exception)
        {
            // Keep closing; an angry module stops nothing.
        }

        try
        {
            session.Module.ViewClosed(presentation.View);
        }
        catch (Exception)
        {
            // A module throwing on close is released anyway.
        }

        session.View = null;

        // A working module keeps its instance (the download continues, the tile shows it); otherwise
        // it is released with its devices, threads and buffers. forceRelease comes from the prompt:
        // whoever chose close means it even while the module works.
        if (forceRelease) ReleaseModule(moduleId, force: true);
        else if (Settings.ReleaseOnClose && !ReleaseModule(moduleId))
        {
            // Still working; released once done (see OnActivityChanged).
            _releaseWhenIdle.Add(moduleId);
        }

        ToolList.SyncRuntimeState(Modules);
        UpdateActivityText();
    }

    // ================= Special pages =================

    [RelayCommand]
    public void OpenSettings() => OpenSpecialTab("settings", "Settings", "IconSettings",
        () => new SettingsView { DataContext = new SettingsViewModel(this) }, page: true);

    [RelayCommand]
    public void OpenDiagnostics() => OpenSpecialTab("diagnostics", "Diagnostics", "IconPackage",
        () => new DiagnosticsView { DataContext = new DiagnosticsViewModel(this) });

    [RelayCommand]
    public void GoHome() => SelectedTab = Tabs.FirstOrDefault(t => t.Key == "home");

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab: the next or previous tab, pages first, then the open tools.</summary>
    public void CycleTab(int step)
    {
        var order = Pages.Select(p => (Page: (NavItem?)p, Tab: (ShellTab?)null))
            .Concat(OpenTabs.Select(t => (Page: (NavItem?)null, Tab: (ShellTab?)t)))
            .ToList();

        var current = order.FindIndex(e => e.Page is { IsSelected: true } || (e.Tab is not null && e.Tab == SelectedTab));
        var next = order[((current < 0 ? 0 : current) + step + order.Count) % order.Count];

        if (next.Page is { } page) Navigate(page.Key);
        else if (next.Tab is { } tab) SelectedTab = tab;
    }

    /// <summary>Ctrl+W and middle-click: closes a tool's tab. Pages stay.</summary>
    public void CloseTool(ShellTab? tab)
    {
        if (tab is null || IsPage(tab) || !tab.CanClose) return;
        _ = CloseTabAsync(tab);
    }

    /// <param name="page">A fixed page tab: not closable, not listed among the open tools.</param>
    private void OpenSpecialTab(string key, string title, string? iconKey, Func<Control> build, bool page = false,
        Geometry? icon = null)
    {
        if (Tabs.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            SelectedTab = existing;
            return;
        }

        var tab = new ShellTab
        {
            Key = key,
            Title = title,
            Content = build(),
            CanPopOut = false,
            CanClose = !page,
            Icon = icon ?? (iconKey is null ? null : App.SharedIcon(iconKey)),
        };

        Tabs.Add(tab);
        SelectedTab = tab;
    }

    // ================= State =================

    private void OnActivityChanged(string moduleId, ModuleActivity activity)
    {
        // A report arriving after release belongs to nothing.
        if (Modules.Find(moduleId) is null) activity = ModuleActivity.Idle;

        if (_open.TryGetValue(moduleId, out var presentation) && presentation.Tab is { } tab)
            tab.IsBusy = activity.IsBusy;

        foreach (var tile in ToolList.AllTiles)
            if (string.Equals(tile.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                tile.Activity = activity;

        // Loaded quietly for a shortcut and done: nothing to show, so nothing stays loaded.
        if (!activity.IsBusy && !_open.ContainsKey(moduleId) && _closeWhenIdle.Remove(moduleId))
        {
            ReleaseModule(moduleId, force: true);
            ToolList.SyncRuntimeState(Modules);
        }

        // View closed, work done: catch up on the release that couldn't happen on close.
        if (!activity.IsBusy && _releaseWhenIdle.Contains(moduleId) && !_open.ContainsKey(moduleId))
        {
            ReleaseModule(moduleId);
            ToolList.SyncRuntimeState(Modules);
        }

        UpdateActivityText();
    }

    private void UpdateActivityText()
    {
        var busy = Modules.Loaded
            .Where(s => s.Module.Activity.IsBusy)
            .Select(s => s.Module.Activity.Text ?? s.Manifest.Name)
            .ToList();

        ActivityText = busy.Count switch
        {
            0 => null,
            1 => busy[0],
            _ => $"{busy[0]}  ·  {busy.Count - 1} more",
        };
    }

    /// <summary>The first-start tour, filling the main window below the header; <c>null</c> when not shown.</summary>
    [ObservableProperty]
    public partial IntroViewModel? Intro { get; set; }

    public void ShowIntro()
    {
        if (MainWindow is null) App.ShowMainWindow();
        Intro ??= new IntroViewModel(this);
    }

    // The tour listens to features and the control center; a closed one lets go.
    partial void OnIntroChanged(IntroViewModel? oldValue, IntroViewModel? newValue) => oldValue?.Dispose();

    public void CloseIntro()
    {
        Intro = null;
        if (Settings.IntroShown) return;

        Settings.IntroShown = true;
        SaveSettings();
    }


    /// <summary>Open modules, working or not. The elevated restart names them: all of them are closed.</summary>
    public IReadOnlyList<string> OpenModules() =>
        Modules.Loaded.Select(s => s.Manifest.Name).OrderBy(n => n, StringComparer.CurrentCulture).ToList();

    /// <summary>Queues the settings for a deferred background write (see <see cref="DeferredJson"/>).</summary>
    public void SaveSettings()
    {
        try
        {
            DeferredJson.Write(AppPaths.SettingsFile, JsonStore.Serialize(Settings),
                ex => new ModuleLog("shell").Error("Settings could not be written.", ex));
        }
        catch (Exception ex)
        {
            new ModuleLog("shell").Error("Settings could not be serialized.", ex);
        }
    }

    private bool _shutDown;

    public void Shutdown()
    {
        // Can run twice: on window close and via the runtime's ShutdownRequested.
        if (_shutDown) return;
        _shutDown = true;

        _shutdown.Cancel();

        // Give back what was requested.
        StayAwake.Set(false);

        foreach (var moduleId in _open.Keys.ToList()) Close(moduleId);
        foreach (var session in Modules.Loaded.ToList()) ReleaseModule(session.Manifest.Id, force: true);

        SaveSettings();

        // Flush everything deferred now; a background timer would not get another turn.
        DeferredJson.FlushAll();
    }

    /// <summary>An open module and where it currently lives.</summary>
    private sealed class Presentation(ModuleSession session, Control view)
    {
        public ModuleSession Session { get; } = session;

        public Control View { get; } = view;

        /// <summary>Exactly one of both is set.</summary>
        public ShellTab? Tab { get; set; }

        public ModuleWindow? Window { get; set; }

        /// <summary>Whether the module was last told "visible".</summary>
        public bool ViewVisible { get; set; }
    }
}
