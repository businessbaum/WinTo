using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MultiTool.Modules.Photos;

public partial class PhotosView : UserControl
{
    public PhotosView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKey);
    }

    private PhotosViewModel? ViewModel => DataContext as PhotosViewModel;

    /// <summary>Keys as in the Windows Photos app; inside a text field they stay the field's.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is TextBox) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Left when !vm.IsEditing:
                vm.PreviousCommand.Execute(null);
                break;
            case Key.Right when !vm.IsEditing:
                vm.NextCommand.Execute(null);
                break;
            case Key.O when ctrl:
                vm.OpenFileCommand.Execute(null);
                break;
            case Key.C when ctrl:
                vm.CopyCommand.Execute(null);
                break;
            case Key.S when ctrl:
                vm.SaveCopyCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                if (vm.Markup is { CanUndo: true } markup) markup.Undo();
                else vm.UndoCommand.Execute(null);
                break;
            case Key.Delete when !vm.IsEditing:
                vm.DeleteCommand.Execute(null);
                break;
            case Key.E when !ctrl:
                vm.ToggleEditCommand.Execute(null);
                break;
            case Key.I when !ctrl:
                vm.ToggleInfoCommand.Execute(null);
                break;
            case Key.OemPlus or Key.Add:
                vm.ZoomInCommand.Execute(null);
                break;
            case Key.OemMinus or Key.Subtract:
                vm.ZoomOutCommand.Execute(null);
                break;
            case Key.D0 or Key.NumPad0:
                vm.ZoomFitCommand.Execute(null);
                break;
            case Key.D1 or Key.NumPad1:
                vm.ZoomActualCommand.Execute(null);
                break;
            case Key.Escape when vm.IsEditing:
                vm.Section = 0;
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
        if (path is { Length: > 0 } && PhotoDecoder.IsSupported(path)) await vm.OpenAsync(path);
    }
}
