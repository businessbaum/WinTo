using Avalonia;
using Avalonia.Controls;

namespace MultiTool.Modules.Capture.Recording;

public partial class RecorderView : UserControl
{
    public RecorderView() => InitializeComponent();

    /// <remarks>
    /// Screen geometry hangs on the <c>TopLevel</c>, which is only known once the view is really in a window;
    /// in the constructor it would be null.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is not RecorderViewModel viewModel) return;
        if (TopLevel.GetTopLevel(this)?.Screens is not { } screens) return;

        viewModel.SetScreens(
        [
            .. screens.All.Select(screen => (
                Name: screen.IsPrimary ? "  (primary)" : "",
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height))
        ]);
    }
}
