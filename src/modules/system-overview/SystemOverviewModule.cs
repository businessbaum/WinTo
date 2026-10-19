using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.SystemOverview;

/// <summary>Windows overview: autostart, load, network, audio devices.</summary>
/// <remarks>
/// <para>
/// Needs <see cref="ViewActivated"/> and <see cref="ViewDeactivated"/>: it shows a one-second tick that
/// would burn CPU behind another tab, and <c>ViewClosed</c> would be too late (a tab switch closes
/// nothing).
/// </para>
/// <para>
/// <b>Nothing is queried on load.</b> <see cref="OnInitialize"/> only creates the view model; the view
/// triggers reading once it exists (see <c>Devices</c>).
/// </para>
/// </remarks>
public sealed class SystemOverviewModule : ModuleBase
{
    private SystemOverviewViewModel? _viewModel;

    protected override void OnInitialize() => _viewModel = new SystemOverviewViewModel(Context);

    protected override Control Build()
    {
        var view = new SystemOverviewView { DataContext = _viewModel };

        // Only once the view is attached is there a window, and without one no dialogs. Reading
        // itself doesn't wait for it.
        _ = _viewModel?.RefreshAsync();

        return view;
    }

    public override void ViewActivated(Control view) => _viewModel?.StartTicking();

    public override void ViewDeactivated(Control view) => _viewModel?.StopTicking();

    public override void ViewClosed(Control view)
    {
        _viewModel?.StopTicking();
        view.DataContext = null;
    }

    public override void Dispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        base.Dispose();
    }
}
