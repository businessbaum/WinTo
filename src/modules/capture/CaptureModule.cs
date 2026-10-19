using Avalonia.Controls;
using Avalonia.Threading;
using MultiTool.Abstractions;
using MultiTool.Modules.Capture.Recording;
using MultiTool.Modules.Capture.Screenshot;

namespace MultiTool.Modules.Capture;

/// <summary>Screenshots with text recognition, screen recording and a clip buffer.</summary>
/// <remarks>
/// <para>
/// Since 0.13.0 the former screenshot and recorder modules as two sections, each with its own settings and
/// data folder through <see cref="SectionContext"/>; the ring buffer stays in <c>modules\recorder</c>.
/// </para>
/// <para>
/// <b>The tile shows the recorder's state</b> (what it last saved); screenshots have no state worth a tile.
/// </para>
/// <para>
/// <b><c>keepsRunning</c>:</b> the capture hotkey, a recording and the clip buffer belong to the loaded module.
/// Win+Shift+S is not one of them: that hook is in the app and loads this module quietly with "quick".
/// </para>
/// </remarks>
public sealed class CaptureModule : ModuleBase
{
    private CaptureViewModel? _viewModel;

    protected override void OnInitialize()
    {
        var screenshot = new ScreenshotViewModel(new SectionContext(Context, "screenshot"));
        var recorder = new RecorderViewModel(new SectionContext(Context, "recorder", publishesStatus: true));

        _viewModel = new CaptureViewModel(screenshot, recorder);
        _viewModel.ActivityChanged += OnActivityChanged;

        screenshot.Start();
    }

    protected override Control Build() => new CaptureView { DataContext = _viewModel };

    public override void ViewActivated(Control view) => _viewModel?.Recorder.StartTicking();

    public override void ViewDeactivated(Control view) => _viewModel?.Recorder.StopTicking();

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>What another part of the app wants captured or shown.</summary>
    /// <remarks>
    /// "quick" comes from Win+Shift+S, "ocr" and "color" from the app's text and color shortcuts: the module
    /// may have no view at all, does its one thing, and reports idle when done so the shell can release it
    /// again. "region" comes from the control center and
    /// starts the region picker. "record" shows the recording section. A file path opens that image in the
    /// screenshot preview, e.g. from the dashboard or the preview card.
    /// </remarks>
    public override void OpenWith(string argument)
    {
        if (_viewModel is not { } viewModel) return;

        switch (argument)
        {
            case "quick":
                _ = viewModel.Screenshot.QuickRegionAsync();
                return;

            case "region":
                viewModel.ShowScreenshotCommand.Execute(null);

                // After the view is attached, so the module can hide its window before capturing.
                Dispatcher.UIThread.Post(
                    () => viewModel.Screenshot.CaptureRegionCommand.Execute(null),
                    DispatcherPriority.Background);
                return;

            case "record":
                viewModel.ShowRecordingCommand.Execute(null);
                return;

            case "ocr":
                _ = viewModel.Screenshot.QuickTextAsync();
                return;

            case "color":
                _ = viewModel.Screenshot.QuickColorAsync();
                return;
        }

        if (File.Exists(argument))
        {
            viewModel.ShowScreenshotCommand.Execute(null);
            _ = viewModel.Screenshot.OpenFileAsync(argument);
        }
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
