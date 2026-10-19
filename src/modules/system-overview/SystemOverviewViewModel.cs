using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.SystemOverview;

/// <summary>A row of the autostart list. The switch writes immediately.</summary>
public sealed partial class AutostartRow : ObservableObject
{
    private readonly SystemOverviewViewModel _owner;
    private bool _suspend;

    public AutostartRow(AutostartEntry entry, SystemOverviewViewModel owner)
    {
        _owner = owner;
        Entry = entry;
        _suspend = true;
        Enabled = entry.Enabled;
        _suspend = false;
    }

    public AutostartEntry Entry { get; }

    public string Name => Entry.Name;

    public string LocationText => Entry.LocationText;

    public string Detail => Entry.Publisher is { } publisher
        ? $"{publisher}  ·  {Entry.TargetPath ?? Entry.Command}"
        : Entry.TargetPath ?? Entry.Command;

    public bool CanToggle => Entry.CanToggle;

    public bool NeedsElevation => Entry.NeedsElevation;

    [ObservableProperty] public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (_suspend) return;
        _owner.ApplyToggle(this, value);
    }

    /// <summary>Resets the switch without triggering the write again.</summary>
    /// <remarks>Otherwise a denied write would loop: reset triggers write, write fails, reset again.</remarks>
    public void ResetTo(bool value)
    {
        _suspend = true;
        Enabled = value;
        _suspend = false;
    }
}

/// <summary>A network adapter with a switch.</summary>
public sealed partial class AdapterRow : ObservableObject
{
    private readonly SystemOverviewViewModel _owner;
    private bool _suspend;

    public AdapterRow(NetworkAdapter adapter, SystemOverviewViewModel owner)
    {
        _owner = owner;
        Adapter = adapter;
        _suspend = true;
        Enabled = adapter.IsEnabled;
        _suspend = false;
    }

    public NetworkAdapter Adapter { get; }

    public string Name => Adapter.Name;
    public string Description => Adapter.Description;
    public string Kind => Adapter.Kind;
    public string Addresses => Adapter.Addresses;
    public string Gateway => Adapter.Gateway;
    public string Dns => Adapter.Dns;
    public string Speed => Adapter.Speed;
    public string MacAddress => Adapter.MacAddress;
    public string StateText => Adapter.StateText;
    public bool IsUp => Adapter.IsUp;
    public bool IsDefaultRoute => Adapter.IsDefaultRoute;

    [ObservableProperty] public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (_suspend) return;
        _ = _owner.ApplyAdapterAsync(this, value);
    }

    /// <summary>Resets the switch without triggering the change again.</summary>
    public void ResetTo(bool value)
    {
        _suspend = true;
        Enabled = value;
        _suspend = false;
    }
}

/// <summary>An environment variable as an editable row.</summary>
/// <remarks>
/// <b>Only <see cref="Value"/> changes, written on button press.</b> Autostart and network write
/// immediately (one bit, visible at a glance). A <c>Path</c> is long and invalid mid-typing; an
/// intermediate state written immediately would be a broken Path.
/// </remarks>
public sealed partial class VariableRow : ObservableObject
{
    public VariableRow(EnvVariable variable)
    {
        // Order: Variable first. The Value callback fires during the constructor, and IsDirty reads Variable.
        Variable = variable;
        Entries = [.. variable.IsList ? EnvironmentVariables.Split(variable.RawValue) : []];
        Value = variable.RawValue;
    }

    public EnvVariable Variable { get; }

    public string Name => Variable.Name;

    public string ScopeText => Variable.ScopeText;

    public bool IsMachine => Variable.Machine;

    public bool IsList => Variable.IsList;

    /// <summary>The split entries, filled only for list-like variables.</summary>
    public ObservableCollection<ListEntry> Entries { get; }

    [ObservableProperty] public partial string Value { get; set; } = "";

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool IsDirty => !string.Equals(Value, Variable.RawValue, StringComparison.Ordinal);

    public int ProblemCount => Entries.Count(e => e.IsProblem);

    public bool HasProblems => ProblemCount > 0;

    /// <summary>The suffix after the name: "34 entries · 3 missing · 1 duplicate".</summary>
    public string SummaryText
    {
        get
        {
            if (!IsList) return "";

            var dead = Entries.Count(e => e.LooksLikePath && !e.Exists);
            var twice = Entries.Count(e => e.IsDuplicate);

            var text = $"{Entries.Count} entries";
            if (dead > 0) text += $"  ·  {dead} missing";
            if (twice > 0) text += $"  ·  {twice} duplicate";
            return text;
        }
    }

    /// <summary>The value without dead and duplicate entries.</summary>
    public string WithoutProblems =>
        string.Join(";", Entries.Where(e => !e.IsProblem).Select(e => e.Text));

    /// <summary>Removes an entry, pending; written only on button press.</summary>
    [RelayCommand]
    private void DropEntry(ListEntry? entry)
    {
        if (entry is null) return;

        Set(string.Join(";", Entries.Where(e => !ReferenceEquals(e, entry)).Select(e => e.Text)));
    }

    public void Append(string folder)
    {
        var text = Value.TrimEnd(';');
        Set(text.Length == 0 ? folder : $"{text};{folder}");
    }

    public void Clean() => Set(WithoutProblems);

    public void Revert() => Set(Variable.RawValue);

    /// <summary>Sets the value and splits it again.</summary>
    /// <remarks>
    /// Splitting belongs here, not in the <see cref="Value"/> callback: that fires on every key press in the
    /// raw field, and splitting checks the disk for each entry (with a sleeping network drive that would
    /// be noticeable while typing). Saving rereads everything anyway.
    /// </remarks>
    private void Set(string value)
    {
        Value = value;

        Entries.Clear();
        if (!IsList) return;

        foreach (var entry in EnvironmentVariables.Split(value)) Entries.Add(entry);

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(HasProblems));
    }

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(IsDirty));
}

/// <summary>A sound device with volume and mute.</summary>
public sealed partial class AudioRow : ObservableObject
{
    private readonly SystemOverviewViewModel _owner;
    private bool _suspend;

    public AudioRow(AudioEndpoint endpoint, SystemOverviewViewModel owner)
    {
        _owner = owner;
        Endpoint = endpoint;
        _suspend = true;
        Volume = (endpoint.Volume ?? 0) * 100;
        Muted = endpoint.Muted;
        _suspend = false;
    }

    public AudioEndpoint Endpoint { get; }

    public string Name => Endpoint.Name;
    public bool IsDefault => Endpoint.IsDefault;

    /// <summary>Devices without a control (VB-CABLE, for example) get none.</summary>
    public bool HasVolume => Endpoint.Volume is not null;

    [ObservableProperty] public partial double Volume { get; set; }
    [ObservableProperty] public partial bool Muted { get; set; }

    public string VolumeText => $"{Volume:0}%";

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (_suspend) return;
        _owner.ApplyAudio(this, volume: value / 100, mute: null);
    }

    partial void OnMutedChanged(bool value)
    {
        if (_suspend) return;
        _owner.ApplyAudio(this, volume: null, mute: value);
    }
}

public sealed partial class SystemOverviewViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private readonly DispatcherTimer _timer;

    private long _lastSent, _lastReceived;
    private DateTimeOffset _lastSample;
    private bool _sampling;

    /// <summary>What netsh knew at the last read. The per-second tick filters the same adapters without starting netsh again.</summary>
    private IReadOnlyDictionary<string, bool>? _connections;

    public SystemOverviewViewModel(IModuleContext context)
    {
        _context = context;

        // Nothing expensive in the constructor: it runs inside the user's click, and registry plus COM
        // would be hundreds of milliseconds of a tile looking pressed.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public ObservableCollection<AutostartRow> AutostartEntries { get; } = [];

    public ObservableCollection<DiskVolume> Disks { get; } = [];

    public ObservableCollection<AdapterRow> Adapters { get; } = [];

    public ObservableCollection<AudioRow> Playback { get; } = [];

    public ObservableCollection<AudioRow> Recording { get; } = [];

    /// <summary>What is shown, filtered. <see cref="_allVariables"/> is the full list.</summary>
    public ObservableCollection<VariableRow> Variables { get; } = [];

    // --------------------------------------------------------------- Sections

    [ObservableProperty] public partial int Section { get; set; }

    public bool ShowOverview => Section == 0;
    public bool ShowAutostart => Section == 1;
    public bool ShowNetwork => Section == 2;
    public bool ShowVariables => Section == 3;
    public bool ShowAudio => Section == 4;

    partial void OnSectionChanged(int value)
    {
        OnPropertyChanged(nameof(ShowOverview));
        OnPropertyChanged(nameof(ShowAutostart));
        OnPropertyChanged(nameof(ShowNetwork));
        OnPropertyChanged(nameof(ShowVariables));
        OnPropertyChanged(nameof(ShowAudio));
    }

    [RelayCommand]
    private void SelectSection(string index)
    {
        if (int.TryParse(index, out var value)) Section = value;
    }

    // --------------------------------------------------------------- Display

    [ObservableProperty] public partial string ComputerName { get; set; } = "";
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Windows { get; set; } = "";
    [ObservableProperty] public partial string Processor { get; set; } = "";
    [ObservableProperty] public partial string Graphics { get; set; } = "";
    [ObservableProperty] public partial string MemoryTotal { get; set; } = "";

    [ObservableProperty] public partial double CpuLoad { get; set; }
    [ObservableProperty] public partial string CpuText { get; set; } = "–";
    [ObservableProperty] public partial double MemoryLoad { get; set; }
    [ObservableProperty] public partial string MemoryText { get; set; } = "–";
    [ObservableProperty] public partial string UptimeText { get; set; } = "–";
    [ObservableProperty] public partial string ThroughputText { get; set; } = "–";

    [ObservableProperty] public partial string DefaultPlayback { get; set; } = "–";
    [ObservableProperty] public partial string DefaultRecording { get; set; } = "–";

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    /// <summary>Whether "open as administrator" would change anything.</summary>
    public bool CanElevate => !_context.Shell.IsElevated;

    // --------------------------------------------------------------- Loading

    /// <summary>Reads everything. Queries run in the background, display afterwards.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            // One Task.Run for all four sources instead of four parallel ones: they hit COM and the
            // registry one after another anyway, and results arriving in four batches make the page twitch.
            // netsh first: the adapter list needs its answer to tell "disabled" from "unplugged".
            _connections = await SystemControl.ReadConnectionsAsync(_context.Shutdown);

            var (machine, entries, disks, adapters, audio, variables) = await Task.Run(() => (
                Machine.Read(),
                Autostart.Read(),
                Devices.ReadDisks(),
                Devices.ReadNetwork(_connections),
                Devices.ReadAudio(),
                EnvironmentVariables.Read()));

            ComputerName = machine.ComputerName;
            UserName = machine.UserName;
            Windows = machine.Windows;
            Processor = machine.Processor;
            Graphics = machine.Graphics ?? "unknown";
            MemoryTotal = Humanize.Bytes(machine.TotalMemoryBytes);

            Replace(AutostartEntries, entries.Select(e => new AutostartRow(e, this)));
            Replace(Disks, disks);
            Replace(Adapters, adapters.Select(a => new AdapterRow(a, this)));
            Replace(Playback, audio.Playback.Select(e => new AudioRow(e, this)));
            Replace(Recording, audio.Recording.Select(e => new AudioRow(e, this)));

            _allVariables = [.. variables.Select(v => new VariableRow(v))];
            ApplyVariableFilter();

            DefaultPlayback = audio.DefaultPlaybackName;
            DefaultRecording = audio.DefaultRecordingName;

            _lastSent = adapters.Sum(a => a.BytesSent);
            _lastReceived = adapters.Sum(a => a.BytesReceived);
            _lastSample = DateTimeOffset.UtcNow;

            Tick();
            PublishStatus();
        }
        catch (Exception ex)
        {
            _context.Log.Error("The system query failed.", ex);
            Show($"System query incomplete: {ex.Message}", error: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>What the tile shows.</summary>
    /// <remarks>
    /// All of it is a <b>state</b>, not a report on a run: autostart entry count, how many are off, and
    /// whether Path contains folders that don't exist. Still true in a week.
    /// </remarks>
    private void PublishStatus()
    {
        var disabled = AutostartEntries.Count(r => !r.Enabled);
        var text = disabled == 0
            ? $"{AutostartEntries.Count} autostart entries"
            : $"{AutostartEntries.Count} autostart · {disabled} off";

        var dead = _allVariables
            .Where(v => v.IsList)
            .Sum(v => v.Entries.Count(e => e.LooksLikePath && !e.Exists));

        var detail = dead > 0
            ? $"{Windows} · {dead} missing path entries"
            : $"{Windows} · {MemoryTotal} RAM";

        _context.PublishStatus(new ModuleStatus(
            dead > 0 ? StatusLevel.Warn : StatusLevel.Neutral,
            text, DateTimeOffset.Now, detail));
    }

    // --------------------------------------------------------------- Tick

    public void StartTicking()
    {
        _timer.Start();
        Tick();
    }

    public void StopTicking() => _timer.Stop();

    /// <summary>A snapshot of the live values.</summary>
    /// <remarks>
    /// CPU and memory are two system calls and fine on the UI thread. Network throughput isn't: all
    /// adapters are enumerated, tens of milliseconds with many virtual adapters, so it runs aside and
    /// reports back; <see cref="_sampling"/> keeps queries from piling up.
    /// </remarks>
    private void Tick()
    {
        var load = Machine.ReadLoad();

        CpuLoad = load.CpuLoad;
        CpuText = Humanize.Percent(load.CpuLoad);
        MemoryLoad = load.MemoryLoad;
        MemoryText = $"{Humanize.Bytes(load.UsedMemoryBytes)} of {Humanize.Bytes(load.TotalMemoryBytes)}";
        UptimeText = Humanize.Duration(load.Uptime);

        if (_sampling) return;
        _sampling = true;

        _ = SampleThroughputAsync();
    }

    private async Task SampleThroughputAsync()
    {
        try
        {
            // The list from the last read instead of a new netsh query (a process start per second would
            // be excessive). It must be passed: otherwise throughput counts filter instances, which mirror
            // their adapter's counters (six times the same Ethernet counter here).
            var adapters = await Task.Run(() => Devices.ReadNetwork(_connections));

            var sent = adapters.Sum(a => a.BytesSent);
            var received = adapters.Sum(a => a.BytesReceived);
            var now = DateTimeOffset.UtcNow;
            var seconds = (now - _lastSample).TotalSeconds;

            if (_lastSample != default && seconds > 0.2)
            {
                // Counters reset when adapters change; negative values are a new starting point, not negative throughput.
                var up = Math.Max(0, sent - _lastSent) / seconds;
                var down = Math.Max(0, received - _lastReceived) / seconds;

                ThroughputText = $"↓ {Humanize.Bytes(down)}/s      ↑ {Humanize.Bytes(up)}/s";
            }

            _lastSent = sent;
            _lastReceived = received;
            _lastSample = now;
        }
        catch (Exception)
        {
            ThroughputText = "–";
        }
        finally
        {
            _sampling = false;
        }
    }

    // --------------------------------------------------------------- Autostart

    internal void ApplyToggle(AutostartRow row, bool enabled)
    {
        var problem = Autostart.SetEnabled(row.Entry, enabled);

        if (problem is null)
        {
            Show(enabled
                ? $"Autostart for \"{row.Name}\" enabled."
                : $"Autostart for \"{row.Name}\" disabled.");
            PublishStatus();
            return;
        }

        row.ResetTo(!enabled);
        Show(problem, error: true);
    }

    [RelayCommand]
    private async Task RemoveAsync(AutostartRow? row)
    {
        if (row is null) return;

        var confirmed = await _context.Shell.ConfirmAsync(
            "Remove entry",
            $"\"{row.Name}\" is permanently deleted from autostart. "
            + "To disable it, use the switch next to it.",
            "Remove", danger: true);

        if (!confirmed) return;

        var problem = Autostart.Remove(row.Entry);

        if (problem is not null)
        {
            Show(problem, error: true);
            return;
        }

        AutostartEntries.Remove(row);
        Show($"\"{row.Name}\" removed.");
        PublishStatus();
    }

    [RelayCommand]
    private async Task ElevateAsync()
    {
        // Restarts the whole app elevated with this module as an open tab; the shell asks first.
        if (!await _context.Shell.RunElevatedAsync(_context.Manifest.Id))
            Show("Elevated start cancelled.", error: true);
    }

    // --------------------------------------------------------------- Variables

    private List<VariableRow> _allVariables = [];

    /// <summary>Read on first access; the constructor touches nothing.</summary>
    private VariableHistory? _history;

    private VariableHistory History => _history ??= new VariableHistory(_context.DataDirectory);

    public bool CanUndoVariable => History.Last is not null;

    public string UndoVariableTip => History.Last is { } last
        ? $"\"{last.Name}\" ({(last.Machine ? "System" : "User")}) {last.What} on {last.When.LocalDateTime:g}. Undo restores the value before."
        : "";

    private void RefreshUndo()
    {
        OnPropertyChanged(nameof(CanUndoVariable));
        OnPropertyChanged(nameof(UndoVariableTip));
    }

    private void Remember(VariableChange change)
    {
        History.Record(change);
        RefreshUndo();
    }

    [RelayCommand]
    private async Task UndoVariableAsync()
    {
        if (History.Last is not { } last) return;

        var scope = last.Machine ? "System" : "User";
        var what = last.OldValue is null
            ? $"\"{last.Name}\" ({scope}) will be deleted again."
            : $"\"{last.Name}\" ({scope}) gets its value from before {last.When.LocalDateTime:g} back.";

        if (!await _context.Shell.ConfirmAsync("Undo change", what, "Undo")) return;

        var target = new EnvVariable(last.Name, "", last.Machine, last.OldKind);
        var problem = last.OldValue is null
            ? EnvironmentVariables.Remove(target)
            : EnvironmentVariables.Write(target, last.OldValue);

        if (problem is not null)
        {
            Show(problem, error: true);
            return;
        }

        History.DropLast();
        RefreshUndo();

        Show($"\"{last.Name}\" restored.");
        ReloadVariables();
        PublishStatus();
    }

    /// <summary>Search field above the variables section.</summary>
    [ObservableProperty] public partial string VariableFilter { get; set; } = "";

    [ObservableProperty] public partial string NewVariableName { get; set; } = "";
    [ObservableProperty] public partial string NewVariableValue { get; set; } = "";

    /// <summary>Whether a new variable applies to all users.</summary>
    [ObservableProperty] public partial bool NewVariableForMachine { get; set; }

    partial void OnVariableFilterChanged(string value) => ApplyVariableFilter();

    private void ApplyVariableFilter()
    {
        var needle = VariableFilter.Trim();

        // Typed explicitly, not var: a collection expression in a conditional needs a target type.
        List<VariableRow> shown = needle.Length == 0
            ? _allVariables
            : [.. _allVariables.Where(v =>
                v.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || v.Value.Contains(needle, StringComparison.CurrentCultureIgnoreCase))];

        Replace(Variables, shown);
    }

    /// <summary>Reloads the variables.</summary>
    /// <remarks>
    /// After every write: a row keeps its original value to detect "changed"; without rereading, a just
    /// saved row would stay marked changed forever.
    /// </remarks>
    private void ReloadVariables()
    {
        var expanded = _allVariables.Where(v => v.IsExpanded).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);

        _allVariables = [.. EnvironmentVariables.Read().Select(v => new VariableRow(v))];

        foreach (var row in _allVariables)
        {
            if (expanded.Contains(row.Name)) row.IsExpanded = true;
        }

        ApplyVariableFilter();
    }

    [RelayCommand]
    private void ToggleVariable(VariableRow? row)
    {
        if (row is not null) row.IsExpanded = !row.IsExpanded;
    }

    [RelayCommand]
    private void SaveVariable(VariableRow? row)
    {
        if (row is null || !row.IsDirty) return;

        var before = row.Variable;
        var problem = EnvironmentVariables.Write(row.Variable, row.Value);

        if (problem is not null)
        {
            Show(problem, error: true);
            return;
        }

        Remember(new VariableChange(DateTimeOffset.Now, before.Name, before.Machine, before.RawValue, before.Kind, "saved"));

        Show($"\"{row.Name}\" saved. Running programs pick up the value after a restart.");

        ReloadVariables();
        PublishStatus();
    }

    [RelayCommand]
    private static void RevertVariable(VariableRow? row) => row?.Revert();

    /// <summary>Removes all dead and duplicate entries, pending, not written.</summary>
    [RelayCommand]
    private void CleanVariable(VariableRow? row)
    {
        if (row is null || !row.HasProblems) return;

        var count = row.ProblemCount;
        row.Clean();
        Show($"{count} entries marked for removal. Written only with \"Save\".");
    }

    [RelayCommand]
    private async Task AppendFolderAsync(VariableRow? row)
    {
        if (row is null) return;

        if (await _context.Shell.PickFolderAsync($"Add folder to \"{row.Name}\"") is { } folder)
            row.Append(folder);
    }

    [RelayCommand]
    private async Task RemoveVariableAsync(VariableRow? row)
    {
        if (row is null) return;

        var confirmed = await _context.Shell.ConfirmAsync(
            "Delete variable",
            $"\"{row.Name}\" is deleted from the {row.ScopeText} variables. "
            + "\"Undo\" restores it.",
            "Delete", danger: true);

        if (!confirmed) return;

        var removed = row.Variable;
        var problem = EnvironmentVariables.Remove(row.Variable);

        if (problem is not null)
        {
            Show(problem, error: true);
            return;
        }

        Remember(new VariableChange(DateTimeOffset.Now, removed.Name, removed.Machine, removed.RawValue, removed.Kind, "deleted"));
        Show($"\"{row.Name}\" deleted.");
        ReloadVariables();
        PublishStatus();
    }

    [RelayCommand]
    private void CreateVariable()
    {
        if (NewVariableName.Trim().Length == 0)
        {
            Show("Name missing.", error: true);
            return;
        }

        var exists = _allVariables.Any(v =>
            v.IsMachine == NewVariableForMachine
            && string.Equals(v.Name, NewVariableName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            Show($"\"{NewVariableName.Trim()}\" already exists there.", error: true);
            return;
        }

        var problem = EnvironmentVariables.Create(NewVariableName, NewVariableValue, NewVariableForMachine);

        if (problem is not null)
        {
            Show(problem, error: true);
            return;
        }

        Remember(new VariableChange(DateTimeOffset.Now, NewVariableName.Trim(), NewVariableForMachine,
            null, Microsoft.Win32.RegistryValueKind.String, "created"));

        Show($"\"{NewVariableName.Trim()}\" created.");
        NewVariableName = "";
        NewVariableValue = "";
        ReloadVariables();
    }

    /// <summary>The Windows dialog, for everything deliberately missing here.</summary>
    [RelayCommand]
    private static void OpenWindowsVariables() =>
        SystemControl.Open("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables");

    // --------------------------------------------------------------- Network

    /// <summary>Switches an adapter. Needs rights, and says so when they're missing.</summary>
    internal async Task ApplyAdapterAsync(AdapterRow row, bool enabled)
    {
        Show(enabled ? $"Enabling \"{row.Name}\"…" : $"Disabling \"{row.Name}\"…");

        var problem = await SystemControl.SetAdapterAsync(row.Name, enabled, _context.Shutdown);

        if (problem is not null)
        {
            row.ResetTo(!enabled);
            Show(problem, error: true);
            return;
        }

        Show(enabled ? $"\"{row.Name}\" is enabled." : $"\"{row.Name}\" is disabled.");

        // The adapter needs a moment until Windows reports its new state; without the pause the list
        // would show the old one again.
        await Task.Delay(TimeSpan.FromMilliseconds(800), _context.Shutdown);
        await RefreshAsync();
    }

    [RelayCommand]
    private static void OpenNetworkConnections() => SystemControl.Open("ncpa.cpl");

    // --------------------------------------------------------------- Sound

    internal void ApplyAudio(AudioRow row, double? volume, bool? mute)
    {
        var problem = SystemControl.SetVolume(row.Endpoint.Id, volume, mute);
        if (problem is not null) Show(problem, error: true);
    }

    [RelayCommand]
    private static void OpenSoundSettings() => SystemControl.Open("ms-settings:sound");

    // --------------------------------------------------------------- Autostart

    [RelayCommand]
    private static void OpenTaskManagerStartup() => SystemControl.Open("ms-settings:startupapps");

    [RelayCommand]
    private void OpenStartupFolder() =>
        _context.Shell.RevealInExplorer(Environment.GetFolderPath(Environment.SpecialFolder.Startup));

    // --------------------------------------------------------------- Helpers

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    public void Dispose()
    {
        _timer.Stop();

        // The audio device cache holds COM objects open. Dispose is the only release there is.
        SystemControl.Release();
    }
}
