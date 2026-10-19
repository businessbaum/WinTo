using Avalonia.Controls;

namespace MultiTool.Modules.GameMode;

public partial class GameModeView : UserControl
{
    public GameModeView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // On every activation: the plan may have changed in Settings, and the list of running
        // programs is a snapshot anyway.
        if (DataContext is GameModeViewModel vm)
        {
            vm.RefreshPlans();
            _ = vm.RefreshAppsCommand.ExecuteAsync(null);
        }
    }
}
