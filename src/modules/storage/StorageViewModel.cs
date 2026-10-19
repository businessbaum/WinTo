using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Modules.Storage.Cleanup;
using MultiTool.Modules.Storage.DiskUsage;
using MultiTool.Modules.Storage.Duplicates;

namespace MultiTool.Modules.Storage;

/// <summary>The two sections and which one is in front.</summary>
/// <remarks>
/// Both views stay alive while the tool is open; switching only changes which one is visible, so a running
/// scan and a finished search survive it. What runs is reported from either section, the busy one first.
/// </remarks>
public sealed partial class StorageViewModel : ObservableObject, IDisposable
{
    private ModuleActivity _mapActivity = ModuleActivity.Idle;
    private ModuleActivity _duplicatesActivity = ModuleActivity.Idle;
    private ModuleActivity _cleanupActivity = ModuleActivity.Idle;

    public StorageViewModel(DiskUsageViewModel map, DuplicateFinderViewModel duplicates, CleanupViewModel cleanup)
    {
        Map = map;
        Duplicates = duplicates;
        Cleanup = cleanup;

        Map.ActivityChanged += OnMapActivity;
        Duplicates.ActivityChanged += OnDuplicatesActivity;
        Cleanup.ActivityChanged += OnCleanupActivity;
        Map.DuplicatesRequested += FindDuplicates;
    }

    public DiskUsageViewModel Map { get; }

    public DuplicateFinderViewModel Duplicates { get; }

    public CleanupViewModel Cleanup { get; }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    /// <summary>0 the map, 1 duplicates, 2 cleanup.</summary>
    [ObservableProperty]
    public partial int Section { get; set; }

    public bool IsMap => Section == 0;

    public bool IsDuplicates => Section == 1;

    public bool IsCleanup => Section == 2;

    partial void OnSectionChanged(int value)
    {
        OnPropertyChanged(nameof(IsMap));
        OnPropertyChanged(nameof(IsDuplicates));
        OnPropertyChanged(nameof(IsCleanup));

        // Measured on first view, not on open: the map is what most opens are for.
        if (value == 2) Cleanup.EnsureMeasured();
    }

    [RelayCommand]
    private void ShowMap() => Section = 0;

    [RelayCommand]
    private void ShowDuplicates() => Section = 1;

    [RelayCommand]
    public void ShowCleanup() => Section = 2;

    public void Scan(string folder)
    {
        Section = 0;
        Map.ScanFromElsewhere(folder);
    }

    public void FindDuplicates(string folder)
    {
        Section = 1;
        Duplicates.UseFolder(folder);
    }

    private void OnMapActivity(object? sender, ModuleActivity activity)
    {
        _mapActivity = activity;
        Report();
    }

    private void OnDuplicatesActivity(object? sender, ModuleActivity activity)
    {
        _duplicatesActivity = activity;
        Report();
    }

    private void OnCleanupActivity(object? sender, ModuleActivity activity)
    {
        _cleanupActivity = activity;
        Report();
    }

    private void Report() => ActivityChanged?.Invoke(this,
        _cleanupActivity.IsBusy ? _cleanupActivity
        : _duplicatesActivity.IsBusy ? _duplicatesActivity
        : _mapActivity.IsBusy ? _mapActivity
        : ModuleActivity.Idle);

    public void Dispose()
    {
        Map.ActivityChanged -= OnMapActivity;
        Duplicates.ActivityChanged -= OnDuplicatesActivity;
        Cleanup.ActivityChanged -= OnCleanupActivity;
        Map.DuplicatesRequested -= FindDuplicates;
        Map.Dispose();
        Cleanup.Dispose();
    }
}
