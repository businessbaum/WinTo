using Avalonia.Controls;
using MultiTool.Abstractions;
using MultiTool.Modules.Storage.Cleanup;
using MultiTool.Modules.Storage.DiskUsage;
using MultiTool.Modules.Storage.Duplicates;

namespace MultiTool.Modules.Storage;

/// <summary>Where the space goes, and which files exist twice.</summary>
/// <remarks>
/// <para>
/// Since 0.13.0 the former disk usage and duplicate finder modules as two sections. Each keeps its settings
/// and data folder through <see cref="SectionContext"/>; "Find duplicates" in the map switches to the
/// duplicates section with the folder, which used to be a hand-over between two modules.
/// </para>
/// <para>
/// <b>Status <c>none</c>, <c>keepsRunning</c>:</b> neither result is stored anywhere, and a run over a drive
/// takes minutes.
/// </para>
/// </remarks>
public sealed class StorageModule : ModuleBase
{
    private StorageViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new StorageViewModel(
            new DiskUsageViewModel(new SectionContext(Context, "disk-usage")),
            new DuplicateFinderViewModel(new SectionContext(Context, "duplicate-finder")),
            new CleanupViewModel(new SectionContext(Context, "cleanup")));

        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build() => new StorageView { DataContext = _viewModel };

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>A folder to scan; <c>duplicates:&lt;folder&gt;</c> searches it for duplicates instead.</summary>
    /// <remarks>"cleanup" shows the cleanup section, e.g. from the low disk space card.</remarks>
    public override void OpenWith(string argument)
    {
        if (_viewModel is null) return;

        const string duplicates = "duplicates:";
        if (string.Equals(argument, "cleanup", StringComparison.OrdinalIgnoreCase))
            _viewModel.ShowCleanup();
        else if (argument.StartsWith(duplicates, StringComparison.OrdinalIgnoreCase))
            _viewModel.FindDuplicates(argument[duplicates.Length..]);
        else
            _viewModel.Scan(argument);
    }

    /// <summary>A finished duplicate search lives only in memory; it took minutes and is gone on close.</summary>
    public override bool HasUnsavedWork => _viewModel?.Duplicates.HasGroups == true;

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null)
        {
            _viewModel.ActivityChanged -= OnActivityChanged;
            _viewModel.Dispose();
            _viewModel = null;
        }

        SetIdle();
        base.Dispose();
    }
}
