using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.GameMode;

/// <summary>What goes into the module's <c>settings.json</c>.</summary>
public sealed class GameModeSettings
{
    /// <summary>The gaming plan's id. Empty means the best available one.</summary>
    public string GamePlan { get; set; } = "";

    /// <summary>Process names without <c>.exe</c>.</summary>
    public List<string> Games { get; set; } = [];

    public bool Automatic { get; set; } = true;

    /// <summary>The previous plan while game mode applies.</summary>
    /// <remarks>
    /// On disk, not only in memory: after an unexpected exit the computer would otherwise stay on high
    /// performance for good. The next open restores it.
    /// </remarks>
    public string RestorePlan { get; set; } = "";
}

/// <summary>A plan in the list.</summary>
public sealed record PlanRow(PowerPlan Plan, bool IsActive)
{
    public string Name => Plan.Name;
}

/// <summary>A listed game.</summary>
public sealed record GameRow(string Name, bool IsRunning);

public sealed partial class GameModeViewModel : ObservableObject, IDisposable
{
    /// <summary>How often to check whether a game runs.</summary>
    /// <remarks>Three seconds is quick enough for a game start; one process enumeration costs about 8 ms.</remarks>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(3);

    private readonly IModuleContext _context;
    private readonly GameModeSettings _settings;
    private readonly bool _loading;
    private Timer? _watch;

    /// <summary>Game names for the background thread; replaced, never modified.</summary>
    private volatile string[] _gameNames = [];

    /// <summary>The plan to return to. <c>null</c> if nothing was switched.</summary>
    private Guid? _restore;

    /// <summary>What turned game mode on: a game name, or <c>null</c> for manual.</summary>
    private string? _appliedFor;

    /// <summary>
    /// Turned off manually while a game ran. Automatic switching waits until no game runs, otherwise
    /// the button would be undone three seconds later.
    /// </summary>
    private bool _suppressed;

    public GameModeViewModel(IModuleContext context)
    {
        _loading = true;
        _context = context;
        _settings = context.Settings.Load<GameModeSettings>();

        foreach (var game in _settings.Games.Distinct(StringComparer.OrdinalIgnoreCase))
            Games.Add(new GameRow(game, false));

        _gameNames = [.. _settings.Games];
        Automatic = _settings.Automatic;
        WindowsGameMode = PowerPlans.WindowsGameMode;

        Games.CollectionChanged += (_, _) => HasGames = Games.Count > 0;
        HasGames = Games.Count > 0;
        _loading = false;
    }

    /// <summary>Tells the shell what is running. Wired by the module.</summary>
    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<PlanRow> Plans { get; } = [];

    public ObservableCollection<PowerPlan> PlanChoices { get; } = [];

    public ObservableCollection<GameRow> Games { get; } = [];

    public ObservableCollection<string> RunningApps { get; } = [];

    [ObservableProperty] public partial PowerPlan? GamePlan { get; set; }
    [ObservableProperty] public partial string ActivePlanName { get; set; } = "";
    [ObservableProperty] public partial bool IsApplied { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool Automatic { get; set; }
    [ObservableProperty] public partial bool HasGames { get; set; }
    [ObservableProperty] public partial bool CanAddUltimate { get; set; }
    [ObservableProperty] public partial bool OnlyOnePlan { get; set; }
    [ObservableProperty] public partial bool OnBattery { get; set; }
    [ObservableProperty] public partial bool WindowsGameMode { get; set; }
    [ObservableProperty] public partial string? SelectedRunningApp { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    public string ToggleText => IsApplied ? "Turn off game mode" : "Turn on game mode";

    partial void OnIsAppliedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleText));
        UpdateStatus();
    }

    partial void OnAutomaticChanged(bool value)
    {
        if (_loading) return;

        _settings.Automatic = value;
        Save();
        UpdateStatus();

        // Automatic off while it had switched on: don't leave game mode applied with nobody to undo it.
        if (!value && IsApplied && _appliedFor is not null) Restore();
    }

    partial void OnGamePlanChanged(PowerPlan? value)
    {
        if (_loading || value is null) return;

        _settings.GamePlan = value.IdText;
        Save();
    }

    partial void OnWindowsGameModeChanged(bool value)
    {
        if (_loading || value == PowerPlans.WindowsGameMode) return;

        if (PowerPlans.SetWindowsGameMode(value) is { } problem) Show(problem, error: true);
    }

    // --------------------------------------------------------------- Lifecycle

    /// <summary>On load: read plans, undo a leftover game mode, start watching.</summary>
    public void Start()
    {
        RefreshPlans();
        RecoverFromCrash();

        _watch = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(1), WatchInterval);
    }

    /// <summary>A game mode that wasn't undone last time.</summary>
    /// <remarks>Only restored if the gaming plan still applies; a plan chosen since then is a decision and stays.</remarks>
    private void RecoverFromCrash()
    {
        if (!Guid.TryParse(_settings.RestorePlan, out var previous)) return;

        _settings.RestorePlan = "";
        Save();

        if (GamePlan is not { } plan || PowerPlans.Active() != plan.Id) return;
        if (PlanChoices.All(p => p.Id != previous)) return;

        if (PowerPlans.Activate(previous) is null)
        {
            Show("Previous power plan restored.");
            RefreshPlans();
        }
    }

    [RelayCommand]
    public void RefreshPlans()
    {
        var plans = PowerPlans.All();
        var active = PowerPlans.Active();

        Plans.Clear();
        foreach (var plan in plans) Plans.Add(new PlanRow(plan, plan.Id == active));

        var chosen = GamePlan?.Id
            ?? (Guid.TryParse(_settings.GamePlan, out var stored) ? stored : (Guid?)null);

        PlanChoices.Clear();
        foreach (var plan in plans) PlanChoices.Add(plan);

        GamePlan = PlanChoices.FirstOrDefault(p => p.Id == chosen) ?? PowerPlans.BestForGames(plans);

        ActivePlanName = plans.FirstOrDefault(p => p.Id == active)?.Name ?? "unknown";
        CanAddUltimate = !PowerPlans.HasUltimate(plans);
        OnlyOnePlan = plans.Count <= 1;
        OnBattery = PowerPlans.OnBattery();

        UpdateStatus();
    }

    // --------------------------------------------------------------- Watching

    /// <summary>Runs on a pool thread.</summary>
    private void Watch()
    {
        var names = _gameNames;
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
        // A tick still in flight when the module was released must not turn game mode back on.
        if (_disposed) return;

        // The list shows which game is running.
        for (var i = 0; i < Games.Count; i++)
        {
            var isRunning = running is not null && string.Equals(Games[i].Name, running, StringComparison.OrdinalIgnoreCase);
            if (Games[i].IsRunning != isRunning) Games[i] = Games[i] with { IsRunning = isRunning };
        }

        if (running is null)
        {
            _suppressed = false;
            if (IsApplied && _appliedFor is not null) Restore();
            return;
        }

        if (Automatic && !IsApplied && !_suppressed) Apply(running);
    }

    // --------------------------------------------------------------- Switching

    [RelayCommand]
    private void Toggle()
    {
        if (!IsApplied)
        {
            Apply(null);
            return;
        }

        // Manually off while automatic had switched on: don't switch right back on.
        if (_appliedFor is not null) _suppressed = true;
        Restore();
    }

    private void Apply(string? game)
    {
        if (GamePlan is not { } target)
        {
            Show("No plan for games selected.", error: true);
            return;
        }

        var current = PowerPlans.Active();

        if (current != target.Id)
        {
            if (PowerPlans.Activate(target.Id) is { } problem)
            {
                Show(problem, error: true);
                return;
            }

            _restore = current;
        }
        else
        {
            // Already active: nothing to return to.
            _restore = null;
        }

        _appliedFor = game;
        IsApplied = true;

        _settings.RestorePlan = _restore?.ToString() ?? "";
        Save();

        ActivityChanged?.Invoke(this, new ModuleActivity(true,
            game is null ? $"Game mode · {target.Name}" : $"Game mode · {game}"));

        Show(game is null ? $"{target.Name} active." : $"{game} running. {target.Name} active.");
        RefreshPlans();
    }

    /// <summary>Back to the previous plan, unless one was chosen manually since then.</summary>
    private void Restore()
    {
        if (_restore is { } previous && GamePlan is { } plan && PowerPlans.Active() == plan.Id)
        {
            if (PowerPlans.Activate(previous) is { } problem) Show(problem, error: true);
        }

        _restore = null;
        _appliedFor = null;
        IsApplied = false;

        _settings.RestorePlan = "";
        Save();

        ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        RefreshPlans();
        Show($"Back to {ActivePlanName}.");
    }

    [RelayCommand]
    private void ActivatePlan(PlanRow? row)
    {
        if (row is null) return;

        if (PowerPlans.Activate(row.Plan.Id) is { } problem)
        {
            Show(problem, error: true);
            return;
        }

        RefreshPlans();
        Show($"{row.Name} active.");
    }

    [RelayCommand]
    private async Task AddUltimateAsync()
    {
        var (id, problem) = PowerPlans.AddUltimate();

        if (id is { } created)
        {
            RefreshPlans();
            GamePlan = PlanChoices.FirstOrDefault(p => p.Id == created) ?? GamePlan;
            Show("\"Ultimate Performance\" created and selected for games.");
            return;
        }

        if (!_context.Shell.IsElevated && problem is { } text && text.Contains("administrator", StringComparison.Ordinal))
        {
            await _context.Shell.RunElevatedAsync(_context.Manifest.Id);
            return;
        }

        Show(problem ?? "Creating failed.", error: true);
    }

    // --------------------------------------------------------------- Games

    [RelayCommand]
    private async Task RefreshAppsAsync()
    {
        var names = await Task.Run(() =>
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
                    }
                }
            }

            return result.ToList();
        });

        RunningApps.Clear();
        foreach (var name in names) RunningApps.Add(name);
        SelectedRunningApp ??= RunningApps.FirstOrDefault();
    }

    [RelayCommand]
    private void AddRunning()
    {
        if (SelectedRunningApp is { Length: > 0 } name) AddGame(name);
    }

    [RelayCommand]
    private async Task AddFromFileAsync()
    {
        var picked = await _context.Shell.PickFilesAsync("Choose game", multiple: true,
            new FilePattern("Program", "exe"));

        foreach (var path in picked) AddGame(Path.GetFileNameWithoutExtension(path));
    }

    private void AddGame(string name)
    {
        var clean = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (clean.Length == 0 || Games.Any(g => string.Equals(g.Name, clean, StringComparison.OrdinalIgnoreCase))) return;

        Games.Add(new GameRow(clean, false));
        PersistGames();
        Show($"{clean} added.");
    }

    [RelayCommand]
    private void RemoveGame(GameRow? row)
    {
        if (row is null) return;

        Games.Remove(row);
        PersistGames();
    }

    private void PersistGames()
    {
        _settings.Games = [.. Games.Select(g => g.Name)];
        _gameNames = [.. _settings.Games];
        Save();
        UpdateStatus();
    }

    [RelayCommand]
    private void OpenPowerSettings() => _context.Shell.OpenUrl("ms-settings:powersleep");

    // --------------------------------------------------------------- Helpers

    private void UpdateStatus()
    {
        StatusText = IsApplied
            ? _appliedFor is { } game ? $"On while {game} runs." : "On, turned on manually."
            : !Automatic ? "Off."
            : !HasGames ? "Off. Add a game for automatic switching."
            : "Off. Turns on as soon as a listed game starts.";
    }

    private void Save()
    {
        if (_loading) return;
        _context.Settings.Save(_settings);
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _watch?.Dispose();
        _watch = null;

        // Closing means back, as the prompt says; staying on high performance after closing would be
        // the "closed but still running" state that must not exist.
        if (IsApplied) Restore();
    }
}
