using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Views;

public partial class MainWindow : Window
{
    /// <summary>What happens after closing. Decided on the first close attempt.</summary>
    private enum Exit
    {
        Undecided,

        /// <summary>Release modules, drop the window, the app stays in the tray.</summary>
        Background,

        /// <summary>The app ends.</summary>
        Quit,
    }

    private Exit _exit;

    /// <summary>The first frame is measured once, not on every return from the tray.</summary>
    private static bool _firstFrameLogged;

    public MainWindow()
    {
        InitializeComponent();
        Root.Classes.Set("compact", ClientSize.Width < CompactWidth);
        WindowCorners.Round(this);
        WindowResizeGrips.Attach(this, Root);

        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;

        // Browser habits: middle-click closes a tool's tab, Ctrl+W the current one, Ctrl+Tab moves on.
        ToolTabs.AddHandler(PointerReleasedEvent, OnToolTabReleased, RoutingStrategies.Tunnel);
        // Tunnel: Avalonia's focus navigation takes every Tab, Ctrl or not, before a bubbling handler sees it.
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    /// <summary>Below this width the page tabs show only their icons.</summary>
    private const double CompactWidth = 1000;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Root is null while Window's own constructor sets the first size, before InitializeComponent.
        if (change.Property == ClientSizeProperty) Root?.Classes.Set("compact", ClientSize.Width < CompactWidth);
    }

    private void OnToolTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle) return;
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not { DataContext: ShellTab tab }) return;

        Shell?.CloseTool(tab);
        e.Handled = true;
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        switch (e.Key)
        {
            case Key.Tab:
                shell.CycleTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            case Key.W:
                shell.CloseTool(shell.SelectedTab);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnOpened(object? sender, EventArgs e)
    {
        if (Shell is not { } shell) return;

        RestorePlacement(shell.Settings.Window);

        // Background priority, i.e. after the first rendered frame. Logged to
        // %APPDATA%\WinTo\logs\shell.log and shown on the diagnostics page. Only on the first
        // open and not after a background start: the clock runs from process start.
        if (!_firstFrameLogged && !Program.StartInBackground)
        {
            _firstFrameLogged = true;
            Dispatcher.UIThread.Post(() =>
            {
                new Services.ModuleLog("shell").Info(
                    $"First frame after {Program.Uptime.ElapsedMilliseconds} ms · "
                    + $"{Services.LoadedAssemblies.Count} assemblies · "
                    + $"{Environment.WorkingSet / (1024 * 1024)} MB working set · "
                    + $"{shell.Catalog.Modules.Count} modules in the catalog, 0 loaded");
            }, DispatcherPriority.Background);
        }

        // First start (or the first after an update that added the tour): show it once.
        if (!shell.Settings.IntroShown && shell.Intro is null) shell.ShowIntro();

        // Status probes also run afterwards on a background thread.
        if (shell.Settings.ScanStatusOnStart)
            Dispatcher.UIThread.Post(
                () => _ = shell.ToolList.RefreshStatusAsync(),
                DispatcherPriority.Background);
    }

    /// <summary>Asks before closing only if something would be lost.</summary>
    /// <remarks>
    /// <para>
    /// <b>Background mode releases every module too.</b> A <c>keepsRunning</c> module never works
    /// without a visible tab (see <see cref="ShellViewModel.CloseWithPromptAsync"/>); the tray keeps
    /// the shell, not a soundboard with live hotkeys nobody can see.
    /// </para>
    /// <para>
    /// Open modules alone are no reason to ask: their actions are saved as they happen. Only running
    /// work and results that live in memory are (see <see cref="ShellViewModel.PendingLosses"/>).
    /// </para>
    /// </remarks>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (App.Quitting || Shell is not { } shell || _exit != Exit.Undecided) return;

        SavePlacement(shell.Settings);

        var background = shell.Settings.RunInBackground;
        var losses = shell.PendingLosses();

        if (losses.Count == 0)
        {
            _exit = background ? Exit.Background : Exit.Quit;
            return;
        }

        e.Cancel = true;

        var message = string.Join(Environment.NewLine, losses.Select(line => "· " + line))
            + Environment.NewLine + Environment.NewLine
            + (background ? "Closing stops this. WinTo stays in the tray." : "Quitting stops this.");

        var confirmed = background
            ? await MessageDialog.ConfirmAsync(this, "Close WinTo", message, "Close")
            : await MessageDialog.ConfirmAsync(this, "Quit WinTo", message, "Quit", danger: true);

        if (!confirmed) return;

        _exit = background ? Exit.Background : Exit.Quit;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (App.Quitting) return;

        var exit = _exit != Exit.Undecided
            ? _exit
            : Shell?.Settings.RunInBackground == true ? Exit.Background : Exit.Quit;

        if (exit == Exit.Background) App.EnterBackground();
        else App.Quit();
    }

    /// <summary>Saves size and position. For quitting from the tray icon.</summary>
    internal void RememberPlacement()
    {
        if (Shell is { } shell) SavePlacement(shell.Settings);
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null) return;

        if (placement.Width > 200 && placement.Height > 200)
        {
            Width = placement.Width;
            Height = placement.Height;
        }

        // Only restore a position that is still on a screen, e.g. after unplugging a monitor.
        var point = new PixelPoint(placement.X, placement.Y);
        if (Screens.All.Any(s => s.Bounds.Contains(point)))
        {
            Position = point;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement(AppSettings settings)
    {
        var maximized = WindowState == WindowState.Maximized;

        settings.Window = new WindowPlacement
        {
            // Maximized, the current size is the screen's; save the last normal size instead.
            Width = maximized ? settings.Window?.Width ?? Width : Width,
            Height = maximized ? settings.Window?.Height ?? Height : Height,
            X = maximized ? settings.Window?.X ?? Position.X : Position.X,
            Y = maximized ? settings.Window?.Y ?? Position.Y : Position.Y,
            Maximized = maximized,
        };
    }


    // ================= Custom title bar =================
    //
    // With WindowDecorations="None" Windows draws nothing around this window. Dragging,
    // double-click to maximize and the three dots on the right live here.

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Drag anywhere except on controls. Walk up from the hit element and stop only if a
        // control lies in between; the tab ScrollViewer covers almost the whole header, so
        // comparing e.Source with sender never matched.
        if (e.Source is Visual hit && HitsControl(hit, sender as Visual)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>Is there a control between the hit element and the header?</summary>
    private static bool HitsControl(Visual from, Visual? stopAt)
    {
        for (var visual = from; visual is not null && visual != stopAt; visual = visual.GetVisualParent())
        {
            if (visual is Button or ToggleButton or TextBox or ComboBox or Slider or CheckBox or RadioButton)
                return true;
        }

        return false;
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

}
