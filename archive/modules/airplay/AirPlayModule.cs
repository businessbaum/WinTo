using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.AirPlay;

/// <summary>AirPlay: receive the screen of a Mac or iPhone on this PC.</summary>
/// <remarks>
/// Manages an external receiver (see <see cref="AirPlayViewModel"/>), keeps it alive while loaded
/// (<c>keepsRunning</c>) and returns it on <see cref="Dispose"/>, freeing the Bonjour name and port.
/// </remarks>
public sealed class AirPlayModule : ModuleBase
{
    private AirPlayViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new AirPlayViewModel(Context);
        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build()
    {
        var view = new AirPlayView { DataContext = _viewModel };

        // After the first frame: locating the receiver is a few File.Exists, but not inside the click.
        _viewModel?.Locate();

        return view;
    }

    public override void ViewClosed(Control view) => view.DataContext = null;

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null) _viewModel.ActivityChanged -= OnActivityChanged;

        _viewModel?.Dispose();
        _viewModel = null;

        SetIdle();
        base.Dispose();
    }
}
