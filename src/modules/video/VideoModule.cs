using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Video;

/// <summary>Plays a video and exports a shorter, smaller or different one.</summary>
/// <remarks>
/// <para>
/// Opened mostly from Explorer with the file as argument. Playback through Windows (<see cref="MediaPlayer"/>),
/// every export through ffmpeg (<see cref="VideoTools"/>).
/// </para>
/// <para>
/// <b>Status <c>none</c></b>; an export in progress is reported as activity, so closing asks.
/// </para>
/// </remarks>
public sealed class VideoModule : ModuleBase
{
    private VideoViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new VideoViewModel(Context);
        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build() => new VideoView { DataContext = _viewModel };

    /// <summary>Hidden behind another tab: the sound stops too.</summary>
    public override void ViewDeactivated(Control view) => _viewModel?.Pause();

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>A file path: opens that video.</summary>
    public override void OpenWith(string argument)
    {
        if (_viewModel is not null && File.Exists(argument)) _ = _viewModel.OpenAsync(argument);
    }

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null)
        {
            _viewModel.ActivityChanged -= OnActivityChanged;
            _viewModel.Dispose();
            _viewModel = null;
        }

        SetIdle();
        base.Dispose();
    }
}
