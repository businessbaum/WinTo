using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MultiTool.Host.Views;

/// <summary>Notice and confirmation dialogs in the app's look.</summary>
/// <remarks>
/// <para>Built in code because the layout depends on the number of buttons.</para>
/// <para>
/// The window is transparent and borderless; what is visible is a <see cref="Border"/> card with
/// <see cref="ShadowMargin"/> around it for the drop shadow. Without the shadow a dialog in the
/// same grays as the page below blends into it.
/// </para>
/// </remarks>
internal static class MessageDialog
{
    /// <summary>Room around the card for the shadow.</summary>
    private const int ShadowMargin = 30;

    private const int CardWidth = 440;

    public static Task AlertAsync(Window? owner, string title, string message) =>
        ShowAsync(owner, title, message, confirmText: null, danger: false);

    public static async Task<bool> ConfirmAsync(
        Window? owner, string title, string message, string confirmText = "OK", bool danger = false) =>
        await ShowAsync(owner, title, message, confirmText, danger);

    /// <summary>Two ways, no cancel. Returns <c>true</c> for the first.</summary>
    /// <remarks>
    /// For questions where both answers do something. Escape picks the first, the more cautious one.
    /// </remarks>
    public static Task<bool> ChooseAsync(
        Window? owner, string title, string message, string primaryText, string secondaryText) =>
        ShowAsync(owner, title, message, secondaryText, danger: false, primaryText: primaryText);

    private static async Task<bool> ShowAsync(
        Window? owner, string title, string message, string? confirmText, bool danger,
        string? primaryText = null)
    {
        var result = primaryText is not null;

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = CardWidth + 2 * ShadowMargin,
            CanResize = false,
            ShowInTaskbar = false,

            // Borderless and transparent: the visible shape is the card below.
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],

            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 22, 0, 0),
        };

        if (confirmText is not null)
        {
            // With two equal ways, the more cautious one sits on the left and is what Escape picks.
            var cancel = new Button { Content = primaryText ?? "Cancel", MinWidth = 110 };
            cancel.Click += (_, _) =>
            {
                result = primaryText is not null;
                dialog.Close();
            };
            buttons.Children.Add(cancel);
        }

        var ok = new Button
        {
            Content = confirmText ?? "OK",
            MinWidth = 110,
            IsDefault = primaryText is null,
            Classes = { "accent" },
        };

        if (danger) ok.Background = Resource("DangerBrush");

        ok.Click += (_, _) =>
        {
            result = primaryText is null;
            dialog.Close();
        };
        buttons.Children.Add(ok);

        var body = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 22),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h1" }, FontSize = 17 },
                new TextBlock { Text = message, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };

        dialog.Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Width = CardWidth,

            // One step lighter than any surface below plus a clear border, so the card stands out
            // even when the shadow is missing (no compositor).
            Background = Resource("BgElevatedBrush") ?? Brushes.Black,
            BorderBrush = Resource("BorderStrongBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),

            // Wide and soft: a tight, hard shadow looks like a frame. High alpha because a faint
            // shadow does nothing on a dark background.
            BoxShadow = BoxShadows.Parse("0 18 44 0 #99000000"),

            Child = body,
        };

        // Escape declines; a window without title bar would otherwise need the mouse.
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close();
        };

        await ShowAndWaitAsync(dialog, owner);
        return result;
    }

    /// <summary>Shows a window and waits until it closes, with or without owner.</summary>
    /// <remarks>
    /// Without an owner this used to be a bare <c>Show()</c> that returned before anyone clicked.
    /// </remarks>
    internal static async Task ShowAndWaitAsync(Window dialog, Window? owner)
    {
        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
            return;
        }

        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        await closed.Task;
    }

    private static IBrush? Resource(string key) =>
        Application.Current?.FindResource(key) as IBrush;
}
