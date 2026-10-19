using Avalonia.Controls;
using Avalonia.Input;

namespace MultiTool.Modules.Storage.DiskUsage;

public partial class DiskUsageView : UserControl
{
    public DiskUsageView()
    {
        InitializeComponent();

        // Events instead of bindings: the map's mouse move and double click have no property to bind to.
        Map.Hovered += item => ViewModel?.Hover(item);
        Map.Opened += item => ViewModel?.Open(item);
    }

    private DiskUsageViewModel? ViewModel => DataContext as DiskUsageViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Reload the drive bar on every attach: a USB stick may have appeared, and used space changes anyway.
        if (ViewModel is { } vm) _ = vm.LoadDrivesAsync();
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { SelectedRow: { } row } vm) vm.OpenRowCommand.Execute(row);
    }
}
