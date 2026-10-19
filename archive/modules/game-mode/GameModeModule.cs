using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.GameMode;

/// <summary>The power plan for games: manually, or automatically while a listed game runs.</summary>
/// <remarks>
/// <para>
/// <b>Status <c>none</c>.</b> The active plan can be changed by anyone (Settings, vendor tools, a
/// game), so a cached tile text would soon be wrong. While game mode applies, the module reports
/// it as running activity instead.
/// </para>
/// <para>
/// <b><c>keepsRunning</c>, and closing means back.</b> Automatic switching needs the loaded module,
/// so closing restores the previous plan; a crash that prevents it is caught up on the next open
/// (see <see cref="GameModeSettings.RestorePlan"/>).
/// </para>
/// </remarks>
public sealed class GameModeModule : ModuleBase
{
    private GameModeViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new GameModeViewModel(Context);
        _viewModel.ActivityChanged += OnActivityChanged;
        _viewModel.Start();
    }

    protected override Control Build() => new GameModeView { DataContext = _viewModel };

    public override void ViewClosed(Control view) => view.DataContext = null;

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        _viewModel?.Dispose();

        if (_viewModel is not null) _viewModel.ActivityChanged -= OnActivityChanged;
        _viewModel = null;

        SetIdle();
        base.Dispose();
    }
}
