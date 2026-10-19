using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Modules.Capture.Recording;
using MultiTool.Modules.Capture.Screenshot;

namespace MultiTool.Modules.Capture;

/// <summary>The two sections and which one is in front.</summary>
/// <remarks>
/// Both views stay alive while the tool is open, so a running recording survives switching. The recorder's
/// encoder and device probes are two ffmpeg calls; they run when its section is first shown, not when the
/// tool opens for a screenshot.
/// </remarks>
public sealed partial class CaptureViewModel : ObservableObject, IDisposable
{
    private ModuleActivity _screenshotActivity = ModuleActivity.Idle;
    private ModuleActivity _recorderActivity = ModuleActivity.Idle;
    private bool _recorderPrepared;

    public CaptureViewModel(ScreenshotViewModel screenshot, RecorderViewModel recorder)
    {
        Screenshot = screenshot;
        Recorder = recorder;

        Screenshot.ActivityChanged += OnScreenshotActivity;
        Recorder.ActivityChanged += OnRecorderActivity;
    }

    public ScreenshotViewModel Screenshot { get; }

    public RecorderViewModel Recorder { get; }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    /// <summary>0 screenshot, 1 recording.</summary>
    [ObservableProperty]
    public partial int Section { get; set; }

    public bool IsScreenshot => Section == 0;

    public bool IsRecording => Section == 1;

    partial void OnSectionChanged(int value)
    {
        OnPropertyChanged(nameof(IsScreenshot));
        OnPropertyChanged(nameof(IsRecording));

        if (value == 1 && !_recorderPrepared)
        {
            _recorderPrepared = true;
            _ = Recorder.PrepareAsync();
        }
    }

    [RelayCommand]
    private void ShowScreenshot() => Section = 0;

    [RelayCommand]
    private void ShowRecording() => Section = 1;

    private void OnScreenshotActivity(object? sender, ModuleActivity activity)
    {
        _screenshotActivity = activity;
        Report();
    }

    private void OnRecorderActivity(object? sender, ModuleActivity activity)
    {
        _recorderActivity = activity;
        Report();
    }

    /// <summary>A recording outranks a screenshot: it runs longer and holds devices.</summary>
    private void Report() => ActivityChanged?.Invoke(this,
        _recorderActivity.IsBusy ? _recorderActivity
        : _screenshotActivity.IsBusy ? _screenshotActivity
        : ModuleActivity.Idle);

    public void Dispose()
    {
        Screenshot.ActivityChanged -= OnScreenshotActivity;
        Recorder.ActivityChanged -= OnRecorderActivity;

        Screenshot.Dispose();
        Recorder.Dispose();
    }
}
