using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MultiTool.Modules.BluetoothAudio;

/// <summary>The small notification at the bottom right when headphones switch connection.</summary>
/// <remarks>
/// <para>
/// Steals no focus (<see cref="Window.ShowActivated"/> off) and takes no taskbar space; a notification
/// that pulls a game out of fullscreen would be worse than none.
/// </para>
/// <para>
/// Like the shell's dialogs, the window is transparent with a shadowed card inside. Closing only hides
/// it (<see cref="Window.Hide"/>) so it can be reused on the next change.
/// </para>
/// </remarks>
internal sealed class SwitchToast : Window
{
    /// <summary>Room around the card for the shadow.</summary>
    private const int ShadowMargin = 24;

    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(4.5);

    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly DispatcherTimer _timer;

    public SwitchToast()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = Brush("TextPrimaryBrush", "#F2F3F7"),
        };

        _detail = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush("TextMutedBrush", "#9BA0B0"),
        };

        Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            MinWidth = 240,
            Background = Brush("BgElevatedBrush", "#1B1C24"),
            BorderBrush = Brush("BorderStrongBrush", "#33364A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12),
            BoxShadow = BoxShadows.Parse("0 10 28 0 #99000000"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new PathIcon
                    {
                        Width = 20,
                        Height = 20,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brush("TextMutedBrush", "#9BA0B0"),
                        Data = Geometry.Parse(
                            "M12 3a9 9 0 0 0-9 9v7a2 2 0 0 0 2 2h2a2 2 0 0 0 2-2v-4a2 2 0 0 0-2-2H5v-1a7 7 0 0 1 14 0v1h-2a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h2a2 2 0 0 0 2-2v-7a9 9 0 0 0-9-9Z"),
                    },
                    new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { _title, _detail },
                    },
                },
            },
        };

        _timer = new DispatcherTimer { Interval = ShowFor };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };

        // A click dismisses the notification right away.
        PointerPressed += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };

        Opened += (_, _) => Reposition();
    }

    /// <summary>Shows the notification or refreshes the visible one.</summary>
    public void Report(string title, string detail)
    {
        _title.Text = title;
        _detail.Text = detail;

        _timer.Stop();
        _timer.Start();

        if (!IsVisible) Show();

        // After layout: the width depends on the new text.
        Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Background);
    }

    /// <summary>Bottom right of the primary screen's work area.</summary>
    private void Reposition()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null || Bounds.Width <= 0) return;

        var scale = RenderScaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var area = screen.WorkingArea;

        // The shadow margin isn't part of the card visually, so the window moves closer to the corner
        // by that amount.
        var inset = (int)((16 - ShadowMargin) * scale);

        Position = new PixelPoint(
            area.X + area.Width - width - inset,
            area.Y + area.Height - height - inset);
    }

    private static IBrush Brush(string key, string fallback)
    {
        var theme = Application.Current?.ActualThemeVariant;
        return Application.Current?.TryGetResource(key, theme, out var value) == true
            && value is IBrush brush
                ? brush
                : new SolidColorBrush(Color.Parse(fallback));
    }
}
