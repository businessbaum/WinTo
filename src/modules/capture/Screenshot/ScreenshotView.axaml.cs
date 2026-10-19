using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MultiTool.Modules.Capture.Screenshot;

public partial class ScreenshotView : UserControl
{
    public ScreenshotView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnShortcut);
    }

    /// <summary>Ctrl+Z, Ctrl+C and Ctrl+S on the picture; inside a text box they stay the text box's.</summary>
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { HasPreview: true } vm || e.Source is TextBox || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        switch (e.Key)
        {
            case Key.Z:
                vm.Editor?.Undo();
                break;
            case Key.C:
                vm.CopyImageCommand.Execute(null);
                break;
            case Key.S:
                vm.SaveCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private ScreenshotViewModel? ViewModel => DataContext as ScreenshotViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ViewModel is not { } vm) return;

        // On every attach, not just the first: popped out into its own window it is a different
        // TopLevel, which should leave the picture while capturing.
        var top = TopLevel.GetTopLevel(this);
        vm.Clipboard = top?.Clipboard;
        vm.HostWindow = top as Window;

        vm.RefreshSources();
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <remarks>A dropped image goes into the preview, so text can be recognized in any image.</remarks>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } vm) return;

        var path = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is { Length: > 0 }) await vm.OpenFileAsync(path);
    }
}
