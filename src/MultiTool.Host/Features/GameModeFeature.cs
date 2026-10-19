using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>Switches to a faster power plan while a listed game runs, and back afterwards.</summary>
/// <remarks>
/// <para>
/// From the former game mode module, which only switched while its tab was open. Plans themselves are in the
/// control center.
/// </para>
/// <para>
/// <b>Polling, on purpose and only when needed:</b> process start events need WMI or elevation; one
/// enumeration costs about 8 ms, every four seconds, and only while games are listed. No card when switching:
/// whoever starts a game doesn't want anything on top of it.
/// </para>
/// <para>
/// <b>Back only if still on the gaming plan.</b> A plan chosen by hand while playing is a decision and stays.
/// The plan to return to is stored, so a crash doesn't leave the computer on high performance for good.
/// </para>
/// </remarks>
public sealed class GameModeFeature : Feature
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    private Timer? _timer;

    /// <summary>For the pool thread; replaced, never modified.</summary>
    private volatile string[] _names = [];

    private string? _appliedFor;

    public GameModeFeature(ShellViewModel shell) : base(shell)
    {
        GameOptions = new GameModeOptions(this);
        Describe();
    }

    public override string Id => "game-mode";

    public override string Name => "Auto game mode";

    public override string Description => "A faster power plan while a listed game runs, the previous one afterwards.";

    public override string Group => "Power";

    protected override string IconPath =>
        "M7 6h10a5 5 0 0 1 4.9 6l-.9 4.4a2.7 2.7 0 0 1-4.6 1.3L14.2 16H9.8l-2.2 1.7A2.7 2.7 0 0 1 3 16.4L2.1 12A5 5 0 0 1 7 6Zm0 3v1.5H5.5v2H7V14h2v-1.5h1.5v-2H9V9H7Zm9 1a1 1 0 1 0 0 2 1 1 0 0 0 0-2Zm2.5-1.5a1 1 0 1 0 0 2 1 1 0 0 0 0-2Z";

    public GameModeOptions GameOptions { get; }

    public override object? Options => GameOptions;

    protected override void OptionsOpened() => _ = GameOptions.RefreshAsync();

    internal GameModeSettings Stored => Settings.GameMode;

    /// <summary>Dialogs for the options, e.g. picking a game's EXE.</summary>
    internal IShell Api => Shell.Api;

    protected override bool ReadEnabled() => Stored.Enabled;

    protected override void StoreEnabled(bool value) => Stored.Enabled = value;

    protected override void Start()
    {
        RecoverFromCrash();
        _names = [.. Stored.Games];
        _timer = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(2), Interval);
        Describe();
    }

    protected override void Stop()
    {
        _timer?.Dispose();
        _timer = null;

        // Switched off while it had switched on: don't leave the plan with nobody to undo it.
        if (_appliedFor is not null) Restore();
        Describe();
    }

    /// <summary>The game list or the plan changed on the Features page.</summary>
    internal void OnGamesChanged()
    {
        _names = [.. Stored.Games];
        Shell.SaveSettings();
        Describe();
    }

    /// <summary>A switch that wasn't undone last time, e.g. after a crash.</summary>
    private void RecoverFromCrash()
    {
        if (!Guid.TryParse(Stored.RestorePlan, out var previous)) return;

        Stored.RestorePlan = "";
        Shell.SaveSettings();

        if (GamePlanId() is { } game && PowerPlans.Active() == game) PowerPlans.Activate(previous);
    }

    /// <summary>On a pool thread.</summary>
    private void Watch()
    {
        var names = _names;
        string? running = null;

        if (names.Length > 0)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (running is null && wanted.Contains(process.ProcessName)) running = process.ProcessName;
                    }
                    catch (Exception)
                    {
                        // Exited in the meantime.
                    }
                }
            }
        }

        Dispatcher.UIThread.Post(() => OnWatched(running));
    }

    private void OnWatched(string? running)
    {
        // A tick still in flight after switching off.
        if (_timer is null) return;

        if (running is null)
        {
            if (_appliedFor is not null) Restore();
            return;
        }

        if (_appliedFor is null) Apply(running);
    }

    private void Apply(string game)
    {
        if (GamePlanId() is not { } target) return;

        var current = PowerPlans.Active();
        if (current == target)
        {
            // Already on it: nothing to return to, but remember the game so the end is noticed.
            _appliedFor = game;
            Describe();
            return;
        }

        if (PowerPlans.Activate(target) is { } problem)
        {
            new ModuleLog("features").Warn($"Game mode for {game}: {problem}");
            return;
        }

        _appliedFor = game;
        Stored.RestorePlan = current?.ToString() ?? "";
        Shell.SaveSettings();
        Describe();
    }

    private void Restore()
    {
        if (Guid.TryParse(Stored.RestorePlan, out var previous)
            && GamePlanId() is { } game && PowerPlans.Active() == game)
        {
            PowerPlans.Activate(previous);
        }

        _appliedFor = null;
        Stored.RestorePlan = "";
        Shell.SaveSettings();
        Describe();
    }

    /// <summary>The chosen plan, or the best one for games if none was chosen or it no longer exists.</summary>
    internal Guid? GamePlanId()
    {
        var plans = PowerPlans.All();

        return Guid.TryParse(Stored.GamePlan, out var chosen) && plans.Any(p => p.Id == chosen)
            ? chosen
            : PowerPlans.BestForGames(plans)?.Id;
    }

    internal void Describe()
    {
        var games = Stored.Games.Count;

        Detail = !IsEnabled ? "Off"
            : _appliedFor is { } game ? $"On while {game} runs"
            : games == 0 ? "No games listed"
            : games == 1 ? $"Waits for {Stored.Games[0]}"
            : $"Waits for {games} games";
    }
}

/// <summary>The game list and plan choice on the Features page.</summary>
public sealed partial class GameModeOptions : ObservableObject
{
    private readonly GameModeFeature _feature;

    internal GameModeOptions(GameModeFeature feature)
    {
        _feature = feature;
        foreach (var game in feature.Stored.Games) Games.Add(game);

        // An explicit bool: a binding on Games.Count would silently do nothing.
        Games.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGames));
    }

    public ObservableCollection<string> Games { get; } = [];

    public bool HasGames => Games.Count > 0;

    public ObservableCollection<PowerPlan> Plans { get; } = [];

    public ObservableCollection<string> RunningApps { get; } = [];

    [ObservableProperty]
    public partial PowerPlan? SelectedPlan { get; set; }

    [ObservableProperty]
    public partial string? SelectedApp { get; set; }

    private bool _loading;

    partial void OnSelectedPlanChanged(PowerPlan? value)
    {
        if (_loading || value is null) return;

        _feature.Stored.GamePlan = value.IdText;
        _feature.OnGamesChanged();
    }

    /// <summary>When the options open: plans and running apps, both off the UI thread.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var (plans, apps) = await Task.Run(() => (PowerPlans.All(), RunningWindowApps()));

        _loading = true;
        Plans.Clear();
        foreach (var plan in plans) Plans.Add(plan);
        SelectedPlan = _feature.GamePlanId() is { } id ? Plans.FirstOrDefault(p => p.Id == id) : null;
        _loading = false;

        RunningApps.Clear();
        foreach (var app in apps) RunningApps.Add(app);
        SelectedApp ??= RunningApps.FirstOrDefault();
    }

    [RelayCommand]
    private void AddRunning()
    {
        if (SelectedApp is { Length: > 0 } name) Add(name);
    }

    [RelayCommand]
    private async Task AddFromFileAsync()
    {
        var picked = await _feature.Api.PickFilesAsync("Choose game", multiple: true, new FilePattern("Program", "exe"));
        foreach (var path in picked) Add(Path.GetFileNameWithoutExtension(path));
    }

    [RelayCommand]
    private void Remove(string? game)
    {
        if (game is null) return;

        Games.Remove(game);
        Persist();
    }

    private void Add(string name)
    {
        var clean = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (clean.Length == 0 || Games.Contains(clean, StringComparer.OrdinalIgnoreCase)) return;

        Games.Add(clean);
        Persist();
    }

    private void Persist()
    {
        _feature.Stored.Games = [.. Games];
        _feature.OnGamesChanged();
    }

    /// <summary>Programs with a window, as candidates for the list.</summary>
    private static List<string> RunningWindowApps()
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != 0)
                        result.Add(process.ProcessName);
                }
                catch (Exception)
                {
                    // Exited or not accessible.
                }
            }
        }

        return [.. result];
    }
}
