using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MultiTool.Modules.Video;

public partial class VideoView : UserControl
{
    public VideoView()
    {
        InitializeComponent();

        // The player's window comes and goes with this view; the view model keeps the state in between.
        Surface.WindowCreated += window => ViewModel?.AttachWindow(window);
        Surface.WindowDestroying += () => ViewModel?.DetachWindow();
        Surface.Resized += () => ViewModel?.UpdateVideo();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKey);
    }

    private VideoViewModel? ViewModel => DataContext as VideoViewModel;

    /// <summary>Keys as in common players; inside a text field or list they stay the control's.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is TextBox or ComboBox) return;

        switch (e.Key)
        {
            case Key.Space:
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                vm.SkipCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "-1" : "-5");
                break;
            case Key.Right:
                vm.SkipCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "1" : "5");
                break;
            case Key.I:
                vm.MarkStartCommand.Execute(null);
                break;
            case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.OpenFileCommand.Execute(null);
                break;
            case Key.O:
                vm.MarkEndCommand.Execute(null);
                break;
            case Key.M:
                vm.ToggleMuteCommand.Execute(null);
                break;
            case Key.E:
                vm.TogglePanelCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } vm) return;

        var path = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is { Length: > 0 } && VideoTools.IsSupported(path)) await vm.OpenAsync(path);
    }
}
