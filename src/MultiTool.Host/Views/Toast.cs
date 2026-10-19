using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MultiTool.Abstractions;
using MultiTool.Host.Services;
using MultiTool.UI;

namespace MultiTool.Host.Views;

/// <summary>A small card at the edge of the screen: a notice or an announcement.</summary>
/// <remarks>
/// <para>
/// Two callers. <see cref="ViewModels.ShellViewModel.ShowNotice"/> while no WinTo window is visible, since
/// the footer reaches nobody then; and <see cref="IShell.Announce"/>, always, for results of work started
/// outside the window (a Win+Shift+S capture). Features add their own (headphones, low disk space).
/// </para>
/// <para>
/// The look follows the Bluetooth module's connection notice: elevated card, title over a muted detail line,
/// something recognizable on the left. It takes no focus, stays while the pointer is on it, and a click runs
/// the action.
/// </para>
/// <para>
/// <b>Settings → Notifications</b> (0.13.0) sets the corner, the display, how long, size, an accent edge, a
/// sound, quiet during games and full screen, and which <see cref="NoticeKind"/> appear at all. Top right by
/// default, so it doesn't stack with Windows' own notifications at the bottom right.
/// </para>
/// </remarks>
internal static partial class Toast
{
    private const int ShadowMargin = 24;

    private static Window? _current;

    private static NotificationSettings Settings => App.Shell?.Settings.Notifications ?? new NotificationSettings();

    /// <summary>A plain notice; a click opens WinTo.</summary>
    public static void Show(string message, StatusLevel level) =>
        Show(NoticeKind.App, message, "", level, thumbnail: null, icon: null, App.ShowMainWindow);

    /// <summary>A notice with title and detail next to the logo.</summary>
    public static void Show(string title, string detail, Action open) =>
        Show(NoticeKind.App, title, detail, StatusLevel.Neutral, thumbnail: null, icon: null, open);

    /// <summary>A notice with its own icon instead of the logo, e.g. headphones for a connection change.</summary>
    public static void Show(NoticeKind kind, string title, string detail, Geometry icon, Action? open = null) =>
        Show(kind, title, detail, StatusLevel.Neutral, thumbnail: null, icon, open ?? App.ShowMainWindow);

    /// <summary>An announcement with an optional picture and action.</summary>
    public static async void Show(Announcement announcement, Action open)
    {
        try
        {
            if (Suppressed(NoticeKind.Results)) return;

            var thumbnail = announcement.ImagePath is { } path ? await LoadThumbnailAsync(path) : null;
            Show(NoticeKind.Results, announcement.Title, announcement.Detail, announcement.Level, thumbnail, icon: null, open);
        }
        catch (Exception ex)
        {
            new ModuleLog("shell").Warn($"Announcement: {ex.Message}");
        }
    }

    /// <summary>From the settings page: a card with the current settings, whatever is switched off.</summary>
    public static void ShowTest() =>
        Show(null, "This is how cards look", "Settings → Notifications changes it.", StatusLevel.Neutral,
            thumbnail: null, icon: null, () => { });

    /// <summary>Switched off, or a game or full-screen program runs and quiet is on.</summary>
    private static bool Suppressed(NoticeKind kind)
    {
        var settings = Settings;
        if (settings.Hidden.Contains(kind)) return true;
        return settings.QuietInFullScreen && FullScreenBusy();
    }

    /// <summary>Whether Windows itself would hold back notifications now.</summary>
    /// <remarks>
    /// <c>SHQueryUserNotificationState</c>: busy is a full-screen program, D3D a game in exclusive full screen,
    /// presentation mode a slide show. The same signal Windows uses for its own banners.
    /// </remarks>
    private static bool FullScreenBusy()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Show(NoticeKind? kind, string title, string detail, StatusLevel level, Bitmap? thumbnail,
        Geometry? icon, Action open)
    {
        if (kind is { } k && Suppressed(k))
        {
            thumbnail?.Dispose();
            return;
        }

        var settings = Settings;
        var large = settings.Large;
        _current?.Close();

        var text = new StackPanel
        {
            Spacing = large ? 3 : 2,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = large ? 360 : 280,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = large ? 16 : 14,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Resource("TextPrimaryBrush"),
                },
            },
        };

        if (detail.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = large ? 13.5 : 12,
                TextWrapping = large ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 3,
                Foreground = Resource("TextMutedBrush"),
            });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = large ? 14 : 12 };

        if (settings.AccentEdge)
        {
            row.Children.Add(new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(-4, 0, 0, 0),
                Background = Resource("AccentBrush"),
            });
        }

        row.Children.Add(Leading(thumbnail, icon, large));

        // Only a warning or an error gets a marker; a plain success needs none.
        if (level is StatusLevel.Warn or StatusLevel.Bad)
        {
            row.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Resource(level == StatusLevel.Bad ? "DangerBrush" : "WarningBrush"),
            });
        }

        row.Children.Add(text);

        var pad = large ? 1.25 : 1.0;
        var card = new Border
        {
            Margin = new Thickness(ShadowMargin),
            MinWidth = large ? 300 : 240,
            Padding = thumbnail is null
                ? new Thickness(16 * pad, 12 * pad, 18 * pad, 12 * pad)
                : new Thickness(10 * pad, 10 * pad, 18 * pad, 10 * pad),
            CornerRadius = new CornerRadius(large ? 14 : 12),
            Background = Resource("BgElevatedBrush"),
            BorderBrush = Resource("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 10 28 0 #99000000"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        };

        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.Manual,
            Opacity = 0,
            Content = card,
            Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(settings.Seconds, 2, 60)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };

        // Whoever is reading it or about to click keeps it.
        card.PointerEntered += (_, _) => timer.Stop();
        card.PointerExited += (_, _) => timer.Start();

        card.PointerPressed += (_, _) =>
        {
            window.Close();
            open();
        };

        // After layout: the width depends on the text and the picture, and the position on the width.
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Place(window, settings);
            window.Opacity = 1;
        }, DispatcherPriority.Background);

        window.Closed += (_, _) =>
        {
            timer.Stop();
            thumbnail?.Dispose();
            if (_current == window) _current = null;
        };

        _current = window;
        window.Show();
        timer.Start();

        if (settings.Sound) PlayNotificationSound();
    }

    /// <summary>The picture if there is one, then the given icon, otherwise the logo.</summary>
    private static Control Leading(Bitmap? thumbnail, Geometry? icon, bool large)
    {
        if (thumbnail is null && icon is not null)
        {
            return new PathIcon
            {
                Data = icon,
                Width = large ? 24 : 20,
                Height = large ? 24 : 20,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Resource("TextSecondaryBrush"),
            };
        }

        if (thumbnail is null)
        {
            var side = large ? 28 : 22;
            return new LogoMark { Width = side, Height = side, VerticalAlignment = VerticalAlignment.Center };
        }

        double height = large ? 64 : 48;
        var size = thumbnail.Size;
        var width = Math.Clamp(height * size.Width / Math.Max(1, size.Height), height, height * 2);

        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            BorderBrush = Resource("BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill },
        };
    }

    /// <summary>Decoded small and off the UI thread: a full desktop capture is 7 million pixels.</summary>
    private static Task<Bitmap?> LoadThumbnailAsync(string path) => Task.Run(() =>
    {
        try
        {
            using var stream = File.OpenRead(path);
            return (Bitmap?)Bitmap.DecodeToHeight(stream, 128, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            // Without a picture the card still says what happened.
            return null;
        }
    });

    /// <summary>Card gap to the side edges and the bottom, and to the top of the work area, in DIPs.</summary>
    private const int SideGap = 16;
    private const int TopGap = 40;

    /// <summary>The chosen corner of the work area, in screen pixels like the control center.</summary>
    /// <remarks>
    /// Measured from the card, not the window: the shadow margin around it is transparent. The top gap is
    /// larger than the others so the card doesn't sit on the screen edge or on a title bar's buttons.
    /// </remarks>
    private static void Place(Window window, NotificationSettings settings)
    {
        Avalonia.Platform.Screen? screen = null;
        if (settings.FollowPointer && GetCursorPos(out var cursor))
            screen = window.Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y));
        screen ??= window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null) return;

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(window.ClientSize.Width * scale);
        var height = (int)Math.Ceiling(window.ClientSize.Height * scale);
        int Px(double dips) => (int)Math.Round(dips * scale);

        var left = area.X + Px(SideGap - ShadowMargin);
        var right = area.Right - width - Px(SideGap - ShadowMargin);
        var top = area.Y + Px(TopGap - ShadowMargin);
        var bottom = area.Bottom - height - Px(SideGap - ShadowMargin);

        window.Position = settings.Corner switch
        {
            NoticeCorner.TopLeft => new PixelPoint(left, top),
            NoticeCorner.TopCenter => new PixelPoint(area.X + (area.Width - width) / 2, top),
            NoticeCorner.BottomRight => new PixelPoint(right, bottom),
            NoticeCorner.BottomLeft => new PixelPoint(left, bottom),
            _ => new PixelPoint(right, top),
        };
    }

    /// <summary>Windows' "Notification" sound as set in the sound settings, asynchronous.</summary>
    private static void PlayNotificationSound()
    {
        const uint async = 0x0001, noDefault = 0x0002, alias = 0x00010000, system = 0x00200000;

        try
        {
            PlaySoundW("Notification.Default", 0, async | noDefault | alias | system);
        }
        catch (Exception)
        {
            // No sound is no reason to lose the card.
        }
    }

    private static IBrush? Resource(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as IBrush : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(string sound, nint module, uint flags);
}
