using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace MultiTool.Modules.ClipboardTray;

public partial class ClipboardTrayView : UserControl
{
    private CancellationTokenSource? _pendingTap;

    public ClipboardTrayView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private ClipboardTrayViewModel? ViewModel => DataContext as ClipboardTrayViewModel;

    /// <summary>How long to wait for a second click before the first counts.</summary>
    /// <remarks>
    /// A double click arrives as <c>Tapped</c> and then <c>DoubleTapped</c>, several frames apart; opening
    /// the preview on the first would flash it on every double click.
    /// <see cref="IPlatformSettings.GetDoubleTapTime"/> is the same threshold Avalonia uses (the Windows
    /// setting, 500 ms by default).
    /// </remarks>
    private TimeSpan _doubleTapWindow = TimeSpan.FromMilliseconds(500);

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (ViewModel is not { } vm) return;

        // Clipboard and file access belong to the TopLevel, known only once the view is in a window.
        var top = TopLevel.GetTopLevel(this);
        vm.Clipboard = top?.Clipboard;
        vm.Storage = top?.StorageProvider;

        if (Application.Current?.PlatformSettings is { } settings)
            _doubleTapWindow = settings.GetDoubleTapTime(PointerType.Mouse);

        // Ctrl+V only works if keyboard input arrives here; focus may move here on every tab switch.
        Focus();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // Only the pending click: the module lives on, and a tab switch looks like closing to Avalonia.
        CancelPendingTap();

        base.OnDetachedFromVisualTree(e);
    }

    // ================================================================= Card

    /// <summary>A click shows the card large, once it's clear no second click follows.</summary>
    private async void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: TrayEntry entry }) return;
        if (ViewModel is not { } vm) return;

        CancelPendingTap();

        var pending = new CancellationTokenSource();
        _pendingTap = pending;

        try
        {
            await Task.Delay(_doubleTapWindow, pending.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await vm.ShowPreviewAsync(entry);
    }

    /// <summary>Two clicks copy back. The pending first one is dropped.</summary>
    private async void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        CancelPendingTap();

        if (sender is not Control { DataContext: TrayEntry entry }) return;
        if (ViewModel is not { } vm) return;

        e.Handled = true;
        await vm.CopyBackAsync(entry);
    }

    private void CancelPendingTap()
    {
        _pendingTap?.Cancel();
        _pendingTap?.Dispose();
        _pendingTap = null;
    }

    // ================================================================= Preview

    private void OnOverlayTapped(object? sender, TappedEventArgs e) => ViewModel?.ClosePreview();

    /// <summary>Stops the click on the card itself.</summary>
    /// <remarks>Otherwise every click <i>inside</i> the preview would close it, including selecting text.</remarks>
    private static void OnPreviewCardTapped(object? sender, TappedEventArgs e) => e.Handled = true;

    // ================================================================= Drag and drop

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepts = e.DataTransfer.Contains(DataFormat.File)
                      || e.DataTransfer.Contains(DataFormat.Bitmap)
                      || e.DataTransfer.Contains(DataFormat.Text);

        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <remarks>
    /// Same order as pasting: a dragged file almost always comes as text too, and that text is just
    /// its path.
    /// </remarks>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is not { } vm) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is { Length: > 0 })
        {
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
            {
                await vm.AddFilesAsync(paths);
                return;
            }
        }

        if (e.DataTransfer.TryGetBitmap() is { } bitmap)
        {
            await vm.AddBitmapAsync(bitmap);
            return;
        }

        if (e.DataTransfer.TryGetText() is { Length: > 0 } text) vm.AddText(text);
    }
}
