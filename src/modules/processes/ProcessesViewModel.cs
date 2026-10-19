using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Processes;

/// <summary>A row of the process list.</summary>
/// <remarks>
/// <b>Refreshed, not replaced.</b> The list reloads every two seconds; rebuilding rows would lose the
/// selection and scroll position on every tick.
/// </remarks>
public sealed partial class ProcessRow : ObservableObject
{
    public ProcessRow(ProcessInfo info) => Info = info;

    /// <summary>The state. Set on each tick; everything else derives from it.</summary>
    /// <remarks><c>= null!</c> because a partial property gets its storage from the generator; it is always set in the constructor.</remarks>
    [ObservableProperty] public partial ProcessInfo Info { get; set; } = null!;

    public int Id => Info.Id;

    public string Name => Info.Name;

    public string DisplayName => Info.DisplayName;

    public string Detail => Info.Detail;

    public string MemoryText => Info.MemoryText;

    public string CpuText => Info.CpuText;

    public bool IsProtected => Info.IsProtected;

    public bool IsCritical => Info.IsCritical;

    public string ProtectionText => Info.ProtectionText;

    public bool HasWindow => Info.HasWindow;

    /// <summary>Can't be ended: protected or critical.</summary>
    public bool IsUntouchable => Info.IsProtected || Info.IsCritical;

    /// <summary>Whether the row shows a note at all.</summary>
    /// <remarks>
    /// Also for "not inspectable": whether the process is protected is <i>unknown</i> there, and an empty
    /// row would read as "normal, will work", the wrong expectation for a SYSTEM service.
    /// </remarks>
    public bool HasNote => IsUntouchable || !Info.Inspectable;

    public string BadgeText => Info.IsCritical ? "critical" : Info.ProtectionText;

    partial void OnInfoChanged(ProcessInfo value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(IsCritical));
        OnPropertyChanged(nameof(IsUntouchable));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(ProtectionText));
        OnPropertyChanged(nameof(HasWindow));
    }
}

public sealed partial class ProcessesViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private readonly DispatcherTimer _timer;

    /// <summary>Everything running, unfiltered. <see cref="Rows"/> is the filtered part.</summary>
    private List<ProcessInfo> _all = [];

    private readonly Dictionary<int, ProcessRow> _rowsById = [];

    /// <summary>CPU time per process at the last tick, for the load.</summary>
    private Dictionary<int, TimeSpan> _lastProcessor = [];
    private DateTimeOffset _lastSample;

    private bool _reading;

    public ProcessesViewModel(IModuleContext context)
    {
        _context = context;

        // Nothing expensive in the constructor: it runs inside the user's click, and enumerating 350
        // processes with an OpenProcess each belongs on a background thread.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<ProcessRow> Rows { get; } = [];

    /// <summary>The rungs of the last kill attempt, top to bottom.</summary>
    public ObservableCollection<KillStep> Steps { get; } = [];

    // --------------------------------------------------------------- Display

    [ObservableProperty] public partial string Search { get; set; } = "";

    /// <summary>Only programs with a window, what Task Manager calls "Apps".</summary>
    [ObservableProperty] public partial bool OnlyApplications { get; set; } = true;

    /// <summary>0 name · 1 CPU · 2 memory.</summary>
    [ObservableProperty] public partial int SortIndex { get; set; }

    [ObservableProperty] public partial ProcessRow? Selected { get; set; }

    /// <summary>Whether forcing includes child processes.</summary>
    [ObservableProperty] public partial bool WithTree { get; set; } = true;

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string? ResultText { get; set; }
    [ObservableProperty] public partial string? AdviceText { get; set; }
    [ObservableProperty] public partial bool ResultIsGood { get; set; }

    [ObservableProperty] public partial string CountText { get; set; } = "";

    /// <summary>How many processes couldn't even be opened.</summary>
    [ObservableProperty] public partial int HiddenCount { get; set; }

    /// <summary>The rights notice, visible rather than hidden in a tooltip.</summary>
    /// <remarks>
    /// Unelevated, 146 of 389 processes here couldn't be opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>
    /// (error 5 for <c>csrss</c>, <c>services</c>, <c>lsass</c>, <c>MsMpEng</c>): no path, no protection
    /// status, none can be ended. Over a third of the list.
    /// </remarks>
    public string HiddenHint =>
        $"{HiddenCount} of {_all.Count} processes not inspectable without administrator rights. "
        + "Protection status unknown, can't be ended.";

    public bool ShowHiddenHint => CanElevate && HiddenCount > 0;

    partial void OnHiddenCountChanged(int value)
    {
        OnPropertyChanged(nameof(HiddenHint));
        OnPropertyChanged(nameof(ShowHiddenHint));
    }

    public bool HasResult => ResultText is { Length: > 0 };

    public bool HasAdvice => AdviceText is { Length: > 0 };

    public bool HasSteps => Steps.Count > 0;

    public bool CanElevate => !_context.Shell.IsElevated;

    public bool HasSelection => Selected is not null;

    /// <summary>The selected process is this app itself.</summary>
    public bool SelectedIsSelf => Selected?.Id == Environment.ProcessId;

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnOnlyApplicationsChanged(bool value) => ApplyFilter();

    /// <remarks>
    /// The three <c>SortBy…</c> are computed; assigning <see cref="SortIndex"/> doesn't raise them. Without
    /// these lines the list re-sorted but the highlight stayed on the old button.
    /// </remarks>
    partial void OnSortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SortByName));
        OnPropertyChanged(nameof(SortByCpu));
        OnPropertyChanged(nameof(SortByMemory));

        ApplyFilter();
    }

    partial void OnSelectedChanged(ProcessRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedIsSelf));
    }

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    partial void OnAdviceTextChanged(string? value) => OnPropertyChanged(nameof(HasAdvice));

    [RelayCommand]
    private void SelectSort(string index)
    {
        if (int.TryParse(index, out var value)) SortIndex = value;
    }

    public bool SortByName => SortIndex == 0;
    public bool SortByCpu => SortIndex == 1;
    public bool SortByMemory => SortIndex == 2;

    // --------------------------------------------------------------- Reading

    private bool _rightsAsked;

    /// <summary>Asks for administrator rights right when opening.</summary>
    /// <remarks>
    /// <para>
    /// Without them this module is half blind (148 of 391 processes here couldn't even be opened).
    /// Asking only afterwards means letting the user fail first.
    /// </para>
    /// <para>Once per module instance, not per tab switch: <c>ViewActivated</c> fires every time.</para>
    /// </remarks>
    public async Task EnsureRightsAsync()
    {
        if (_rightsAsked || _context.Shell.IsElevated) return;
        _rightsAsked = true;

        // On success WinTo restarts elevated and this process is closing; a message here would
        // reach nobody.
        if (await _context.Shell.RunElevatedAsync(_context.Manifest.Id)) return;

        ResultText = "Without administrator rights.";
        AdviceText = "SYSTEM services and other users' processes stay invisible and can't be ended. "
                   + "\"As administrator\" asks again.";
        ResultIsGood = false;
    }

    /// <summary>
    /// Whether the view currently wants the timer. The <c>finally</c> in <see cref="RunAsync"/> follows it,
    /// so a tab switch during the ladder isn't undone when it finishes.
    /// </summary>
    private bool _ticking;

    public void StartTicking()
    {
        _ticking = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void StopTicking()
    {
        _ticking = false;
        _timer.Stop();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_reading) return;
        _reading = true;

        try
        {
            var list = await Task.Run(ProcessList.Read, _context.Shutdown);

            var now = DateTimeOffset.UtcNow;
            var seconds = _lastSample == default ? 0 : (now - _lastSample).TotalSeconds;
            var next = new Dictionary<int, TimeSpan>(list.Count);

            foreach (var info in list)
            {
                if (info.Processor is not { } processor) continue;
                next[info.Id] = processor;

                // Share of the total CPU, not of one core, the same basis as Task Manager; otherwise a
                // process with eight busy threads would show 800 %.
                if (seconds > 0.2 && _lastProcessor.TryGetValue(info.Id, out var before))
                {
                    var delta = (processor - before).TotalSeconds;
                    info.Cpu = Math.Clamp(delta / (seconds * Environment.ProcessorCount), 0, 1);
                }
            }

            _lastProcessor = next;
            _lastSample = now;
            _all = list;

            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _context.Log.Error("The process list could not be read.", ex);
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>Updates <see cref="Rows"/> with the filtered part.</summary>
    /// <remarks>
    /// <b>Reconcile instead of refill.</b> Clear and refill would jump every two seconds: selection gone,
    /// scrolled to top, and a click might hit the row below. Existing rows get the new state, vanished ones
    /// go, new ones come in. Only sorting by CPU moves rows, which is the point there.
    /// </remarks>
    private void ApplyFilter()
    {
        var needle = Search.Trim();

        IEnumerable<ProcessInfo> query = _all;

        if (OnlyApplications) query = query.Where(p => p.IsApplication);

        if (needle.Length > 0)
        {
            query = query.Where(p =>
                p.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || p.DisplayName.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || p.Id.ToString().Contains(needle, StringComparison.Ordinal));
        }

        query = SortIndex switch
        {
            1 => query.OrderByDescending(p => p.Cpu ?? 0).ThenBy(p => p.DisplayName, StringComparer.CurrentCulture),
            2 => query.OrderByDescending(p => p.WorkingSet),
            _ => query.OrderBy(p => p.DisplayName, StringComparer.CurrentCulture),
        };

        var wanted = query.ToList();

        CountText = OnlyApplications
            ? $"{wanted.Count} of {_all.Count} processes with a window"
            : $"{wanted.Count} of {_all.Count} processes";

        HiddenCount = _all.Count(p => !p.Inspectable);

        var keep = wanted.Select(p => p.Id).ToHashSet();

        foreach (var stale in _rowsById.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            if (_rowsById.Remove(stale, out var row)) Rows.Remove(row);
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            var info = wanted[index];

            if (_rowsById.TryGetValue(info.Id, out var row))
            {
                row.Info = info;

                var at = Rows.IndexOf(row);
                if (at != index && at >= 0 && index < Rows.Count) Rows.Move(at, index);
            }
            else
            {
                row = new ProcessRow(info);
                _rowsById[info.Id] = row;
                Rows.Insert(Math.Min(index, Rows.Count), row);
            }
        }

        // The selection may have dropped out of the list (filtered or ended); otherwise the pane would
        // keep showing a process that no longer exists.
        if (Selected is not null && !_rowsById.ContainsKey(Selected.Id)) Selected = null;
    }

    // --------------------------------------------------------------- Ending

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (Selected is not { } row) return;

        await RunAsync(row, () => Terminator.CloseAsync(row.Info, _context.Shutdown),
            $"Asking \"{row.DisplayName}\" to close…");
    }

    [RelayCommand]
    private async Task ForceAsync()
    {
        if (Selected is not { } row) return;

        if (row.IsCritical)
        {
            Report(new KillResult(false, [],
                $"\"{row.Name}\" is marked critical.",
                "Windows crashes when such a process is ended. "
                + "The attempt is therefore not made."));
            return;
        }

        var warning = row.Id == Environment.ProcessId
            ? "\n\nThis is WinTo itself. All running work stops."
            : row.IsProtected
                ? $"\n\n{row.ProtectionText}. Protected processes can't be ended from user mode."
                : "";

        var confirmed = await _context.Shell.ConfirmAsync(
            "Force process",
            $"\"{row.DisplayName}\" (PID {row.Id}) is ended immediately. "
            + "Unsaved work is lost."
            + (WithTree ? " Child processes are ended too." : "")
            + warning,
            "Force", danger: true);

        if (!confirmed) return;

        await RunAsync(row,
            () => Terminator.ForceAsync(row.Info, _all, WithTree, _context.Shell.IsElevated, _context.Shutdown),
            $"Ending \"{row.DisplayName}\"…");
    }

    /// <summary>Runs a kill attempt and tells the shell something is running.</summary>
    /// <remarks>
    /// The timer pauses meanwhile; otherwise a refresh mid-ladder would set <see cref="Selected"/> to
    /// <c>null</c> as the process vanishes, and the report would belong to nobody.
    /// </remarks>
    private async Task RunAsync(ProcessRow row, Func<Task<KillResult>> work, string activity)
    {
        IsBusy = true;
        _timer.Stop();
        ActivityChanged?.Invoke(this, new ModuleActivity(true, activity));

        try
        {
            Report(await work());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _context.Log.Error($"Ending {row.Name} failed.", ex);
            Report(new KillResult(false, [], "The attempt aborted.", ex.Message));
        }
        finally
        {
            IsBusy = false;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);

            // Only if the view is still visible: ViewDeactivated, ViewClosed or Dispose may have come
            // during the ladder, and their stop applies.
            if (_ticking)
            {
                await RefreshAsync();
                _timer.Start();
            }
        }
    }

    private void Report(KillResult result)
    {
        Steps.Clear();
        foreach (var step in result.Steps) Steps.Add(step);

        OnPropertyChanged(nameof(HasSteps));

        ResultText = result.Summary;
        AdviceText = result.Advice;
        ResultIsGood = result.Killed;
    }

    // --------------------------------------------------------------- Helpers

    [RelayCommand]
    private void Reveal()
    {
        if (Selected?.Info.Path is { Length: > 0 } path) _context.Shell.RevealInExplorer(path);
    }

    [RelayCommand]
    private async Task ElevateAsync()
    {
        if (!await _context.Shell.RunElevatedAsync(_context.Manifest.Id))
        {
            ResultText = "Restart cancelled.";
            AdviceText = "";
            ResultIsGood = false;
        }
    }

    [RelayCommand]
    private void ClearResult()
    {
        Steps.Clear();
        OnPropertyChanged(nameof(HasSteps));
        ResultText = null;
        AdviceText = null;
    }

    public void Dispose()
    {
        _ticking = false;
        _timer.Stop();
        _rowsById.Clear();
        _all = [];
        _lastProcessor.Clear();
    }
}
