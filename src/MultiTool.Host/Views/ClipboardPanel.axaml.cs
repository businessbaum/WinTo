using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MultiTool.Host.Features.Clipboard;
using MultiTool.Host.Services;

namespace MultiTool.Host.Views;

/// <summary>The clipboard history at the mouse pointer: type to search, Enter pastes.</summary>
/// <remarks>
/// Keys are taken in the tunnel phase on the window, so arrows, Enter and Delete work while the search box
/// has the focus. Like the control center, clicking elsewhere closes it. Where it opens is a setting
/// (<see cref="ClipPlacement"/>): at the mouse pointer, at the text cursor of the window it pastes into, or
/// in the middle of the screen.
/// </remarks>
public partial class ClipboardPanel : Window
{
    /// <summary>Room around the card for the shadow; matches <c>Margin</c> in the XAML.</summary>
    private const double ShadowMargin = 24;

    public ClipboardPanel()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        Deactivated += (_, _) => Close();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        // One click pastes, like Win+V; the pin and remove buttons keep their own click. Shift+click as plain text.
        List.AddHandler(TappedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is ClipItem item)
                ViewModel?.PasteItem(item, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        });
    }

    /// <summary>Set before showing.</summary>
    internal ClipPlacement Placement { get; init; }

    /// <summary>The window that had the focus before; its text cursor is where <see cref="ClipPlacement.Caret"/> opens.</summary>
    internal nint Target { get; init; }

    private ClipboardPanelViewModel? ViewModel => DataContext as ClipboardPanelViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Place();
        Opacity = 1;
        Activate();
        Dispatcher.UIThread.Post(() => Search.Focus(), DispatcherPriority.Input);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && Digit(e.Key) is { } number)
        {
            vm.PasteNumber(number, shift);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Down:
                vm.Move(1);
                break;
            case Key.Up:
                vm.Move(-1);
                break;
            case Key.PageDown:
                vm.Move(6);
                break;
            case Key.PageUp:
                vm.Move(-6);
                break;
            case Key.Enter:
                vm.PasteItem(null, shift);
                break;
            case Key.Delete when vm.Query.Length == 0 || !Search.IsFocused:
                vm.Remove(null);
                break;
            case Key.P when ctrl:
                vm.TogglePin(null);
                break;
            default:
                return;
        }

        e.Handled = true;
        if (vm.Selected is { } selected) List.ScrollIntoView(selected);
    }

    private static int? Digit(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
        _ => null,
    };

    /// <summary>The card's top left at the anchor, kept inside the work area of that display.</summary>
    private void Place()
    {
        GetCursorPos(out var cursor);
        var point = new PixelPoint(cursor.X, cursor.Y);

        if (Placement == ClipPlacement.Caret && CaretPosition() is { } caret) point = caret;

        var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling(Height * scale);
        var margin = (int)Math.Round(ShadowMargin * scale);

        if (Placement == ClipPlacement.Center)
        {
            Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
            return;
        }

        var x = Math.Clamp(point.X - margin, area.X - margin, Math.Max(area.X - margin, area.Right - width + margin));
        var y = Math.Clamp(point.Y - margin, area.Y - margin, Math.Max(area.Y - margin, area.Bottom - height + margin));
        Position = new PixelPoint(x, y);
    }

    /// <summary>Below the text cursor of the target window, in screen pixels; <c>null</c> where none is reported.</summary>
    /// <remarks>
    /// Win32 programs (Notepad, Office, most dialogs) report their caret through <c>GetGUIThreadInfo</c>. Browsers
    /// and Electron apps often draw their own and report none; the panel then opens at the mouse pointer.
    /// </remarks>
    private PixelPoint? CaretPosition()
    {
        if (Target == 0) return null;

        var thread = GetWindowThreadProcessId(Target, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Caret == 0) return null;

        var point = new NativePoint { X = info.CaretRect.Left, Y = info.CaretRect.Bottom };
        if (!ClientToScreen(info.Caret, ref point)) return null;

        return new PixelPoint(point.X, point.Y + 4);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public NativeRect CaretRect;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);
}
