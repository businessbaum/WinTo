using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MultiTool.Modules.Soundboard.ViewModels;

namespace MultiTool.Modules.Soundboard.Views;

public partial class SoundboardView : UserControl
{
    /// <summary>Beyond this distance a mouse move counts as a drag, not a click.</summary>
    private const double DragThreshold = 6;

    /// <summary>
    /// The dragged item. A field instead of a payload in the data object: the drag stays in-process, nothing is
    /// serialized, and the drop gets the same instance.
    /// </summary>
    /// <remarks>
    /// No longer <c>static</c>: as a module the code lives in the same process as everything else and the view
    /// can be built several times; a shared field would mix two drags.
    /// </remarks>
    private object? _dragged;

    private object? _pressed;

    /// <summary>The event that started the drag.</summary>
    /// <remarks>
    /// Since Avalonia 12 <c>DragDrop.DoDragDropAsync</c> requires <see cref="PointerPressedEventArgs"/>, not any
    /// <c>PointerEventArgs</c>. The drag only begins on movement here (otherwise every click would be a drag), so
    /// the press is kept until the movement is far enough.
    /// </remarks>
    private PointerPressedEventArgs? _pressEvent;

    private Point _pressedAt;
    private FolderNodeViewModel? _highlighted;

    public SoundboardView()
    {
        InitializeComponent();

        AddHandler(PointerPressedEvent, OnPointerPressedPreview, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedPreview, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _pressed = null;
            _pressEvent = null;
        }, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearHighlight());
    }

    private SoundboardViewModel? ViewModel => DataContext as SoundboardViewModel;

    // ================= Starting a drag =================

    private void OnPointerPressedPreview(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        _pressEvent = null;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        // No drag may start on sliders, switches and text fields, or they'd be unusable.
        if (IsInteractive(source)) return;

        _pressed = FindNode(source);
        _pressedAt = e.GetPosition(this);
        _pressEvent = e;
    }

    private async void OnPointerMovedPreview(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || _pressEvent is null) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = null;
            _pressEvent = null;
            return;
        }

        var delta = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        var payload = _pressed;
        var start = _pressEvent;
        _pressed = null;
        _pressEvent = null;
        _dragged = payload;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(DataFormat.Text, "soundboard-node"));
            await DragDrop.DoDragDropAsync(start, transfer, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // A cancelled drag is not an error.
        }
        finally
        {
            _dragged = null;
            ClearHighlight();
        }
    }

    // ================= Dropping =================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Files from outside belong to the window; don't intercept them here, or importing by
        // drag & drop onto the list breaks.
        if (_dragged is null) return;

        var target = ResolveDropTarget(e.Source as Visual);
        var allowed = IsAllowed(_dragged, target);

        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        Highlight(allowed ? target : null);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dragged is null) return;

        e.Handled = true;

        var payload = _dragged;
        var target = ResolveDropTarget(e.Source as Visual);
        ClearHighlight();

        if (!IsAllowed(payload, target) || ViewModel is not { } vm) return;

        switch (payload)
        {
            case SoundItemViewModel sound:
                vm.MoveSound(sound, target?.Id);
                break;

            case FolderNodeViewModel folder:
                vm.MoveFolder(folder, target?.Id);
                break;
        }
    }

    private bool IsAllowed(object payload, FolderNodeViewModel? target) => payload switch
    {
        SoundItemViewModel sound => sound.Entry.FolderId != target?.Id,
        FolderNodeViewModel folder => ViewModel?.CanMoveFolder(folder, target?.Id) ?? false,
        _ => false,
    };

    /// <summary>
    /// Finds the target folder under the pointer. Over a sound its folder is the target, so you can drop next to
    /// a sound without hitting the folder itself. Over empty space the target is the top level.
    /// </summary>
    private FolderNodeViewModel? ResolveDropTarget(Visual? source) => FindNode(source) switch
    {
        FolderNodeViewModel folder => folder,
        SoundItemViewModel sound when sound.Entry.FolderId is { } id => FindFolder(id),
        _ => null,
    };

    private FolderNodeViewModel? FindFolder(Guid id) =>
        ViewModel?.VisibleItems.OfType<FolderNodeViewModel>().FirstOrDefault(f => f.Id == id);

    private static object? FindNode(Visual? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ListBoxItem item) return item.DataContext;
            current = current.GetVisualParent();
        }

        return null;
    }

    /// <summary>Controls that need the pointer for themselves.</summary>
    private static bool IsInteractive(Visual source)
    {
        var current = source;
        while (current is not null and not ListBoxItem)
        {
            if (current is Slider or Button or ToggleButton or TextBox or ComboBox or Thumb) return true;
            current = current.GetVisualParent();
        }

        return false;
    }

    private void Highlight(FolderNodeViewModel? folder)
    {
        if (ReferenceEquals(_highlighted, folder)) return;

        if (_highlighted is not null) _highlighted.IsDropTarget = false;
        _highlighted = folder;
        if (_highlighted is not null) _highlighted.IsDropTarget = true;
    }

    private void ClearHighlight() => Highlight(null);
}
