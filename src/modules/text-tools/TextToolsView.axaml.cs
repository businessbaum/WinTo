using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MultiTool.Modules.TextTools;

public partial class TextToolsView : UserControl
{
    public TextToolsView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private TextToolsViewModel? ViewModel => DataContext as TextToolsViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The clipboard belongs to the TopLevel, known only once the view is in a window.
        if (ViewModel is { } vm) vm.Clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <remarks>A dropped file means one thing here: its checksum, so the module switches to that tool.</remarks>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is not { } vm) return;

        var path = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is { Length: > 0 }) await vm.HashPathAsync(path, HashKind.Sha256);
    }
}
