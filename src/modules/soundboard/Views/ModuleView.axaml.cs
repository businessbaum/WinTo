using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MultiTool.Modules.Soundboard.ViewModels;

namespace MultiTool.Modules.Soundboard.Views;

/// <summary>The module root. Was the main window in the standalone app.</summary>
/// <remarks>
/// The lifecycle is <b>no longer</b> here: <c>Opened</c> used to call <c>Initialize</c>, <c>Closing</c> called
/// <c>Shutdown</c>. Both now belong to the module, not the view. A tab switch detaches this control from the
/// tree, which Avalonia can't tell from closing; a keyboard hook tied to it would be lost on a mere switch.
/// </remarks>
public partial class ModuleView : UserControl
{
    public ModuleView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Only check which programs make sound when the list drops down. The query walks all
        // devices and sessions over COM; repeating it on the display tick would be waste.
        AudioAppPicker.DropDownOpened += (_, _) => ViewModel?.RefreshAudioApps();
    }

    private SoundboardModuleViewModel? ViewModel => DataContext as SoundboardModuleViewModel;

    // ================= Drag and drop =================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;

        if (hasFiles && ViewModel is { } vm)
        {
            vm.Soundboard.IsDragOver = true;
            vm.SelectedTab = 0;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm) vm.Soundboard.IsDragOver = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is not { } vm) return;
        vm.Soundboard.IsDragOver = false;

        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;

        var paths = items
            .Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count == 0) return;

        await vm.Soundboard.ImportAsync(paths);
    }
}
