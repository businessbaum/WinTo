using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using SkiaSharp;

namespace MultiTool.Host.ControlCenter;

/// <summary>The control center's "Now playing" card, like the one in macOS's Control Center.</summary>
/// <remarks>
/// <para>
/// Cover, title, artist and app of what plays now, with previous, play/pause and next, and a progress bar that
/// seeks on click where the app allows it. Several apps with a session (Spotify and a browser): the arrow moves to
/// the next one; otherwise it follows the one Windows calls current.
/// </para>
/// <para>
/// <b>Read once a second while the control center is open</b> (<see cref="MediaSessions"/>); the bar moves in
/// between from the last position and the time since. The cover is read only when the track changes, and a
/// faint tint from its average color sits behind the card.
/// </para>
/// </remarks>
public sealed partial class NowPlaying : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tick;
    private string? _appId;
    private string? _preferred;
    private string? _trackKey;
    private IReadOnlyList<string> _appIds = [];
    private TimeSpan _start, _end, _position;
    private DateTime _updated;
    private bool _reading;
    private bool _disposed;

    public NowPlaying()
    {
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => _ = ReadAsync();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => UpdateTimeline();
    }

    [ObservableProperty]
    public partial bool HasMedia { get; set; }

    /// <summary>Fades the card in once the first read answered.</summary>
    [ObservableProperty]
    public partial bool Ready { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Artist { get; set; } = "";

    [ObservableProperty]
    public partial string AppName { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool CanPrevious { get; set; }

    [ObservableProperty]
    public partial bool CanNext { get; set; }

    [ObservableProperty]
    public partial bool CanPlayPause { get; set; }

    [ObservableProperty]
    public partial bool CanSeek { get; set; }

    [ObservableProperty]
    public partial bool HasTimeline { get; set; }

    /// <summary>0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "";

    [ObservableProperty]
    public partial string RemainingText { get; set; } = "";

    [ObservableProperty]
    public partial Bitmap? Art { get; set; }

    public bool HasArt => Art is not null;

    /// <summary>The cover's average color, faint, behind the card; transparent without a cover.</summary>
    [ObservableProperty]
    public partial IBrush Tint { get; set; } = Brushes.Transparent;

    /// <summary>"2 of 3" when several apps play, for the switch arrow.</summary>
    [ObservableProperty]
    public partial string? SessionText { get; set; }

    public bool HasSeveral => SessionText is not null;

    partial void OnArtChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(HasArt));
    }

    partial void OnSessionTextChanged(string? value) => OnPropertyChanged(nameof(HasSeveral));

    public void Start()
    {
        _ = ReadAsync();
        _poll.Start();
        _tick.Start();
    }

    private async Task ReadAsync()
    {
        if (_disposed || _reading) return;
        _reading = true;

        try
        {
            var preferred = _preferred;
            var known = _trackKey;
            var snapshot = await WinRt.RunAsync(() => MediaSessions.Read(preferred, known));
            if (_disposed) return;

            if (snapshot is null || (snapshot.Title.Length == 0 && snapshot.Status != MediaSnapshot.Playing))
            {
                HasMedia = false;
                _trackKey = null;
                _appId = null;
                Art = null;
                Tint = Brushes.Transparent;
            }
            else
            {
                await ApplyAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("control-center").Warn($"Now playing: {ex.Message}");
            HasMedia = false;
        }
        finally
        {
            _reading = false;
            Ready = true;
        }
    }

    private async Task ApplyAsync(MediaSnapshot snapshot)
    {
        _appId = snapshot.AppId;
        _appIds = snapshot.AppIds;
        Title = snapshot.Title.Length > 0 ? snapshot.Title : "Unknown title";
        Artist = snapshot.Artist;
        AppName = MediaSessions.AppName(snapshot.AppId);
        IsPlaying = snapshot.Status == MediaSnapshot.Playing;
        CanPrevious = snapshot.CanPrevious;
        CanNext = snapshot.CanNext;
        CanPlayPause = snapshot.CanPlayPause;
        CanSeek = snapshot.CanSeek;

        var index = snapshot.AppIds.ToList().IndexOf(snapshot.AppId);
        SessionText = snapshot.AppIds.Count > 1 && index >= 0 ? $"{index + 1} of {snapshot.AppIds.Count}" : null;

        (_start, _end, _position, _updated) = (snapshot.Start, snapshot.End, snapshot.Position, snapshot.Updated);
        HasTimeline = _end > _start;
        UpdateTimeline();

        if (!snapshot.SameTrack)
        {
            _trackKey = snapshot.TrackKey;
            var (art, tint) = await Task.Run(() => Decode(snapshot.Thumbnail));
            if (_disposed)
            {
                art?.Dispose();
                return;
            }

            Art = art;
            Tint = tint;
        }

        HasMedia = true;
    }

    /// <summary>Position now: the last reported one plus the time since, while playing.</summary>
    private void UpdateTimeline()
    {
        if (!HasTimeline) return;

        var position = _position;
        if (IsPlaying) position += DateTime.UtcNow - _updated;

        var length = _end - _start;
        var elapsed = position - _start;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed > length) elapsed = length;

        Progress = length.Ticks > 0 ? elapsed.Ticks / (double)length.Ticks : 0;
        ElapsedText = Humanize.Clock(elapsed);
        RemainingText = "−" + Humanize.Clock(length - elapsed);
    }

    /// <summary>The cover at card size, and a faint tint from its average color.</summary>
    private static (Bitmap? Art, IBrush Tint) Decode(byte[]? bytes)
    {
        if (bytes is null) return (null, Brushes.Transparent);

        try
        {
            using var stream = new MemoryStream(bytes);
            var art = Bitmap.DecodeToWidth(stream, 128, BitmapInterpolationMode.HighQuality);

            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null) return (art, Brushes.Transparent);

            long r = 0, g = 0, b = 0, n = 0;
            for (var y = 0; y < decoded.Height; y += Math.Max(1, decoded.Height / 12))
            {
                for (var x = 0; x < decoded.Width; x += Math.Max(1, decoded.Width / 12))
                {
                    var pixel = decoded.GetPixel(x, y);
                    r += pixel.Red;
                    g += pixel.Green;
                    b += pixel.Blue;
                    n++;
                }
            }

            var average = n == 0 ? Colors.Transparent : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            var tint = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x55, average.R, average.G, average.B), 0),
                    new GradientStop(Color.FromArgb(0x10, average.R, average.G, average.B), 1),
                },
            }.ToImmutable();

            return (art, tint);
        }
        catch (Exception)
        {
            return (null, Brushes.Transparent);
        }
    }

    [RelayCommand]
    private Task PlayPauseAsync()
    {
        // Answer the click at once; the next read confirms.
        IsPlaying = !IsPlaying;
        return SendAsync(MediaCommand.TogglePlayPause);
    }

    [RelayCommand]
    private Task NextAsync() => SendAsync(MediaCommand.Next);

    [RelayCommand]
    private Task PreviousAsync() => SendAsync(MediaCommand.Previous);

    /// <summary>The next app with a session, then back to the first.</summary>
    [RelayCommand]
    private Task OtherSessionAsync()
    {
        if (_appIds.Count < 2 || _appId is null) return Task.CompletedTask;

        var index = _appIds.ToList().IndexOf(_appId);
        _preferred = _appIds[(index + 1) % _appIds.Count];
        _trackKey = null;
        return ReadAsync();
    }

    /// <summary>From the bar: a fraction of the track's length.</summary>
    public Task SeekAsync(double fraction)
    {
        if (!CanSeek) return Task.CompletedTask;

        var target = _start + TimeSpan.FromTicks((long)((_end - _start).Ticks * Math.Clamp(fraction, 0, 1)));
        (_position, _updated) = (target, DateTime.UtcNow);
        UpdateTimeline();
        return SendAsync(MediaCommand.Seek, target.Ticks);
    }

    private async Task SendAsync(MediaCommand command, long position = 0)
    {
        if (_appId is not { } appId || _disposed) return;

        try
        {
            await WinRt.RunAsync(() => MediaSessions.Send(appId, command, position));
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("control-center").Warn($"Now playing {command}: {ex.Message}");
        }

        // Apps take a moment to report the new track or state.
        await Task.Delay(350);
        await ReadAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _tick.Stop();
        Art = null;
    }
}
