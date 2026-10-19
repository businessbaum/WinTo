using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MultiTool.Modules.Files.Metadata;

/// <summary>The view. Holds only what needs a window: dragging from Explorer.</summary>
/// <remarks>Drag and drop is event handling and can't be bound; the view passes the paths on to the view model.</remarks>
public partial class MetadataCleanerView : UserControl
{
    public MetadataCleanerView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);

        // Without this Windows shows the "not allowed" cursor and delivers no drop.
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;

        Zone.Classes.Set("dropActive", hasFiles);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => Zone.Classes.Set("dropActive", false);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Zone.Classes.Set("dropActive", false);

        if (DataContext is not MetadataCleanerViewModel viewModel) return;
        if (e.DataTransfer.TryGetFiles() is not { } items) return;

        var paths = items
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();

        if (paths.Count > 0) _ = viewModel.AddAsync(paths);
    }
}
