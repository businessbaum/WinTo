using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MultiTool.Modules.Files.Convert;

/// <summary>The view. Accepts dropped files and folders.</summary>
/// <remarks>Avalonia 12 moved drag and drop to <c>DataTransfer</c>; <c>e.Data</c> is gone.</remarks>
public partial class ImageConvertView : UserControl
{
    public ImageConvertView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private ImageConvertViewModel? ViewModel => DataContext as ImageConvertViewModel;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);

        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (ViewModel is { } vm) vm.IsDragOver = hasFiles;

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsDragOver = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is not { } vm) return;
        vm.IsDragOver = false;

        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;

        var paths = items
            .Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count > 0) vm.Add(paths);
    }
}
