using Avalonia.Controls;
using MultiTool.Abstractions;
using MultiTool.Modules.Soundboard.Services;
using MultiTool.Modules.Soundboard.ViewModels;
using MultiTool.Modules.Soundboard.Views;

namespace MultiTool.Modules.Soundboard;

/// <summary>The soundboard as a module.</summary>
/// <remarks>
/// <para>
/// Turning the standalone app into a module revolves around one question: <b>what must keep running when
/// nobody looks.</b>
/// </para>
/// <para>
/// Discord, TeamSpeak or OBS point permanently to <c>CABLE Output</c> as input, and what arrives there is
/// mixed by this module. If it stopped when someone closed the tab, the microphone in voice chat would go
/// silent in the same second.
/// </para>
/// <list type="table">
///   <item>
///     <term><see cref="OnInitialize"/> / <see cref="Dispose"/></term>
///     <description>Audio graph, keyboard hook, sound library. Live while the module is loaded.</description>
///   </item>
///   <item>
///     <term><see cref="ViewActivated"/> / <see cref="ViewDeactivated"/></term>
///     <description>The 120 ms display tick. Only runs while the view is visible.</description>
///   </item>
///   <item>
///     <term><see cref="ViewClosed"/></term>
///     <description>Cancel a running hotkey capture, otherwise the hook keeps waiting and swallows the next key press.</description>
///   </item>
/// </list>
/// <para>
/// The keyboard hook runs on its own thread with its own message loop since 0.9.0; see
/// <see cref="GlobalHotkeyService"/>.
/// </para>
/// </remarks>
public sealed class SoundboardModule : ModuleBase
{
    private AppServices? _services;
    private SoundboardModuleViewModel? _viewModel;

    protected override void OnInitialize()
    {
        // The breakdown goes to the log: the first open of a module is always the most expensive,
        // and speeding it up requires knowing which part costs what.
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var paths = new ModulePaths(Context);
        var afterPaths = watch.ElapsedMilliseconds;

        _services = new AppServices(paths);
        var afterServices = watch.ElapsedMilliseconds;

        _viewModel = new SoundboardModuleViewModel(_services, new ShellDialogService(Shell));
        var afterViewModel = watch.ElapsedMilliseconds;

        _viewModel.Initialize();

        Log.Info($"Start: paths {afterPaths} ms · services {afterServices - afterPaths} ms · "
            + $"view model {afterViewModel - afterServices} ms · "
            + $"Initialize {watch.ElapsedMilliseconds - afterViewModel} ms");

        // The audio graph's state also changes on its own: a device arrives, a sound ends, the
        // tunnel breaks. All of it goes to the tile and the shell's footer.
        _services.Engine.RoutesChanged += ReportActivity;
        _services.Engine.DesktopTunnelChanged += ReportActivity;
        _services.Engine.SoundStateChanged += OnSoundStateChanged;

        ReportActivity();
    }

    protected override Control Build() =>
        new ModuleView { DataContext = _viewModel };

    public override void ViewActivated(Control view) => _viewModel?.StartTicking();

    public override void ViewDeactivated(Control view) => _viewModel?.StopTicking();

    public override void ViewClosed(Control view)
    {
        _viewModel?.CancelHotkeyRecording();

        // The view isn't reused; the view model is.
        view.DataContext = null;
    }

    public override void Dispose()
    {
        if (_services is { } services)
        {
            services.Engine.RoutesChanged -= ReportActivity;
            services.Engine.DesktopTunnelChanged -= ReportActivity;
            services.Engine.SoundStateChanged -= OnSoundStateChanged;
        }

        _viewModel?.Shutdown();
        _services?.Dispose();

        _viewModel = null;
        _services = null;

        SetIdle();
        base.Dispose();
    }

    private void OnSoundStateChanged(Guid soundId, bool playing) => ReportActivity();

    /// <summary>Tells the shell whether audio is flowing.</summary>
    /// <remarks>
    /// More than cosmetics: while <c>IsBusy</c> holds, the host doesn't release the module and asks before quitting.
    /// A soundboard going silent mid voice chat because a tab was closed is exactly what the module contract
    /// prevents.
    /// </remarks>
    private void ReportActivity()
    {
        if (_services is not { } services || _viewModel is null)
        {
            SetIdle();
            return;
        }

        var playing = services.Engine.GetStatus();
        var micToCable = services.Engine.MicReachesVirtualOutput;
        var tunnel = services.Engine.DesktopTunnelActive;

        var text = (playing, micToCable, tunnel) switch
        {
            ({ } status, _, _) => "Soundboard: " + status.Name,
            (null, true, true) => "Soundboard: microphone and desktop audio in cable",
            (null, true, false) => "Soundboard: microphone in cable",
            (null, false, true) => "Soundboard: desktop audio in cable",
            _ => null,
        };

        SetActivity(text is null ? ModuleActivity.Idle : new ModuleActivity(true, text));

        // For the tile: what it should show at the next start without loading the module.
        PublishStatus(
            micToCable ? StatusLevel.Good : StatusLevel.Warn,
            micToCable ? $"{services.Sounds.Count} sounds · cable ready"
                       : $"{services.Sounds.Count} sounds · no cable",
            services.Engine.LastError ?? services.Engine.VirtualRouteDeviceName);
    }
}
