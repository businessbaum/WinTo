using Avalonia.Controls;
using MultiTool.Abstractions;
using MultiTool.Modules.Apps.Packages;
using MultiTool.Modules.Apps.Uninstaller;

namespace MultiTool.Modules.Apps;

/// <summary>Update, install and remove programs.</summary>
/// <remarks>
/// <para>
/// Since 0.13.0 the former packages (winget) and uninstaller modules as two sections, each with its own
/// settings and data folder through <see cref="SectionContext"/>; the uninstaller's registry backups stay in
/// <c>modules\uninstaller</c>.
/// </para>
/// <para>
/// <b>The tile shows the update count</b>, a state that stays true until something is updated; only the
/// packages section reports it. The program list is read when its section is first shown, not on every open.
/// </para>
/// </remarks>
public sealed class AppsModule : ModuleBase
{
    private AppsViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new AppsViewModel(
            new PackagesViewModel(new SectionContext(Context, "packages", publishesStatus: true)),
            new UninstallerViewModel(new SectionContext(Context, "uninstaller")));

        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build()
    {
        var view = new AppsView { DataContext = _viewModel };

        // `winget list` enumerates every installed program and takes seconds; after the first frame.
        _ = _viewModel?.Packages.PrepareAsync();

        return view;
    }

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>"uninstall" opens the uninstall section.</summary>
    /// <remarks>"uninstall" shows the uninstall section, "updates" the list of available updates (from the
    /// dashboard's update card).</remarks>
    public override void OpenWith(string argument)
    {
        if (_viewModel is not { } viewModel) return;

        if (string.Equals(argument, "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.ShowUninstallCommand.Execute(null);
        }
        else if (string.Equals(argument, "updates", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.ShowPackagesCommand.Execute(null);
            viewModel.Packages.Section = 1;
        }
    }

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null) _viewModel.ActivityChanged -= OnActivityChanged;
        _viewModel = null;

        SetIdle();
        base.Dispose();
    }
}
