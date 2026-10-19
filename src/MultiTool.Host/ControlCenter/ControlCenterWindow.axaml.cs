using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.ControlCenter;

public partial class ControlCenterWindow : Window
{
    /// <summary>Room around the card for the shadow; matches <c>Margin</c> in the XAML.</summary>
    private const double ShadowMargin = 24;

    /// <summary>Distance from the card to the edge of the work area.</summary>
    private const double Gap = 12;

    public ControlCenterWindow()
    {
        InitializeComponent();

        // In code because the property is a list. See MessageDialog.
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        // Like the Windows flyouts: clicking elsewhere closes it.
        Deactivated += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        // Brightness is written only after the user touched a slider; see DisplayItem.Arm.
        DisplayList.AddHandler(PointerPressedEvent, (_, e) => Arm(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        DisplayList.AddHandler(KeyDownEvent, (_, e) => Arm(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        AppList.AddHandler(PointerPressedEvent, (_, e) => Arm(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        AppList.AddHandler(KeyDownEvent, (_, e) => Arm(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);

        // A click on the progress line seeks; the bar itself writes nothing back.
        MediaBar.Seek += fraction => _ = (DataContext as ControlCenterViewModel)?.Media.SeekAsync(fraction);
    }

    private static void Arm(object? source)
    {
        switch ((source as Visual)?.FindAncestorOfType<Slider>(includeSelf: true)?.DataContext)
        {
            case DisplayItem display:
                display.Arm();
                break;
            case AppVolumeItem app:
                app.Arm();
                break;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Place();
    }

    /// <summary>
    /// The height changes once brightness and radios are read; the card stays anchored to the tray
    /// instead of growing off screen.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty && IsVisible) Place();
    }

    /// <summary>Bottom right above the tray, or top right if the taskbar is at the top.</summary>
    /// <remarks>
    /// In screen pixels: <see cref="Window.Position"/> is in pixels, so at 125 % or 150 % scaling
    /// DIPs would land off by a quarter or half.
    /// </remarks>
    private void Place()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            Opacity = 1;
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(ClientSize.Width * scale);
        var height = (int)Math.Ceiling(ClientSize.Height * scale);
        var inset = (int)Math.Round((ShadowMargin - Gap) * scale);

        var x = area.Right - width + inset;
        var y = area.Y > screen.Bounds.Y
            ? area.Y - inset
            : area.Bottom - height + inset;

        Position = new PixelPoint(x, y);

        // Visible only once placed, or the card would flash at the top left for a frame. Then the card
        // slides in (styles in the XAML).
        if (Opacity < 1)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Opacity = 1;
                Card.Classes.Add("shown");
            }, DispatcherPriority.Render);
        }
    }
}

/// <summary>Opens and closes the control center. At most one at a time.</summary>
internal static class ControlCenterHost
{
    private static ControlCenterWindow? _window;
    private static long _closedAt;

    /// <summary>It opened; the tour listens to tick off "try it".</summary>
    public static event Action? Opened;

    /// <summary>The click on the tray icon.</summary>
    /// <remarks>
    /// Clicking the icon while the control center is open deactivates (and closes) it before the
    /// click arrives, so a plain toggle would reopen it. A click right after closing counts as that
    /// close click.
    /// </remarks>
    public static void Toggle(ShellViewModel shell)
    {
        if (_window is not null)
        {
            _window.Close();
            return;
        }

        if (Environment.TickCount64 - _closedAt < 400) return;
        Show(shell);
    }

    public static void Show(ShellViewModel shell)
    {
        if (_window is { } open)
        {
            open.Activate();
            return;
        }

        var viewModel = new ControlCenterViewModel(shell);
        var window = new ControlCenterWindow { DataContext = viewModel };
        viewModel.RequestClose = window.Close;

        window.Closed += (_, _) =>
        {
            _closedAt = Environment.TickCount64;
            _window = null;

            viewModel.Dispose();
            window.DataContext = null;

            App.TrimSoon();
        };

        _window = window;
        window.Show();
        window.Activate();
        viewModel.Start();
        Opened?.Invoke();
    }

    public static void Close() => _window?.Close();
}
