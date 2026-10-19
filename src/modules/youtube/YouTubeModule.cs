using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.YouTube;

/// <summary>Downloads video and audio via yt-dlp and ffmpeg.</summary>
/// <remarks>
/// <para>
/// <c>keepsRunning</c> is <c>true</c>: a download aborted because a tab was closed is exactly what a
/// queue must not do. With <c>IsBusy</c> the shell keeps the module alive and asks before quitting.
/// </para>
/// <para>
/// The run still follows <c>Context.Shutdown</c>, and <see cref="ExternalProcess"/> kills the whole
/// process tree on cancel; yt-dlp's ffmpeg child would otherwise keep the half-written file open.
/// </para>
/// </remarks>
public sealed class YouTubeModule : ModuleBase
{
    private YouTubeViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new YouTubeViewModel(Context);
        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build()
    {
        var view = new YouTubeView { DataContext = _viewModel };

        // Only once the view exists: "yt-dlp --version" starts a process and can take half a second
        // the first time.
        _ = _viewModel?.CheckToolsAsync();

        return view;
    }

    public override void ViewClosed(Control view) => view.DataContext = null;

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null) _viewModel.ActivityChanged -= OnActivityChanged;
        _viewModel = null;

        SetIdle();
        base.Dispose();
    }
}
