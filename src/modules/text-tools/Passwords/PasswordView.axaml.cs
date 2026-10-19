using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;

namespace MultiTool.Modules.TextTools.Passwords;

public partial class PasswordView : UserControl
{
    /// <summary>How long a single row takes to fade in.</summary>
    /// <remarks>Short enough not to get in the way when generating repeatedly.</remarks>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);

    /// <summary>Delay per row, so the list flows in instead of flashing.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(22);

    /// <summary>No further delay from the ninth row on.</summary>
    /// <remarks>With 25 passwords the last row would otherwise start after 550 ms.</remarks>
    private const int MaxStaggered = 8;

    /// <summary>Minimum gap between two animations.</summary>
    /// <remarks>
    /// <b>Because of the slider:</b> every change to length, count or groups regenerates, dozens of times
    /// a second while dragging. Without this lock the animation would restart on every tick and
    /// flicker. Single clicks are far enough apart to get through.
    /// </remarks>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(260);

    private DateTime _lastRun = DateTime.MinValue;
    private bool _animateBatch;
    private int _indexInBatch;

    public PasswordView()
    {
        InitializeComponent();

        // ContainerPrepared instead of Style.Animations: rows are rebuilt on every generate, and a
        // style would also run while dragging a slider.
        Rows.ContainerPrepared += OnRowPrepared;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The clipboard belongs to the TopLevel, known only once the view is in a window.
        if (DataContext is PasswordViewModel vm)
        {
            var top = TopLevel.GetTopLevel(this);
            vm.Clipboard = top?.Clipboard;

            // On every attach: popped out into its own window it is a different handle.
            vm.WindowHandle = top?.TryGetPlatformHandle()?.Handle ?? 0;
        }
    }

    /// <summary>A row was created, and maybe a new set with it.</summary>
    /// <remarks>
    /// Rows are created in order, so <c>index 0</c> starts a set. It decides whether to animate; the rest
    /// of the set follows so half the list doesn't move while the other half stands still.
    /// </remarks>
    private void OnRowPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Index == 0)
        {
            var now = DateTime.UtcNow;

            _animateBatch = now - _lastRun > Cooldown;
            _indexInBatch = 0;

            if (_animateBatch) _lastRun = now;
        }

        if (!_animateBatch) return;

        var delay = Stagger * Math.Min(_indexInBatch, MaxStaggered);
        _indexInBatch++;

        _ = RunAsync(e.Container, delay);
    }

    /// <summary>Fades a row in while it rises slightly.</summary>
    /// <remarks>
    /// Via <see cref="Visual.RenderTransformProperty"/> and <see cref="TransformOperations"/>: a render
    /// transform costs no layout pass, an animated <c>Margin</c> would relayout the list every frame.
    /// Eight pixels, not thirty: it should say "new", not demand attention.
    /// </remarks>
    private static async Task RunAsync(Control container, TimeSpan delay)
    {
        var animation = new Animation
        {
            Duration = Duration,
            Delay = delay,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Backward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Avalonia.Styling.Setter(OpacityProperty, 0d),
                        new Avalonia.Styling.Setter(RenderTransformProperty, TransformOperations.Parse("translateY(8px)")),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Avalonia.Styling.Setter(OpacityProperty, 1d),
                        new Avalonia.Styling.Setter(RenderTransformProperty, TransformOperations.Parse("none")),
                    },
                },
            },
        };

        try
        {
            await animation.RunAsync(container);
        }
        catch (Exception)
        {
            // A row disappearing mid-animation is normal (a second click on "Generate again").
        }
    }
}
