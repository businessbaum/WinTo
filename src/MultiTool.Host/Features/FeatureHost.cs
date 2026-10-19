using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>All features, in the order of the Features page.</summary>
/// <remarks>
/// Built with the shell, started by the app after the first frame (<see cref="StartAll"/>), stopped on quit.
/// The offscreen gallery and the self-test never start them: no hooks and watchers in a test.
/// </remarks>
public sealed class FeatureHost
{
    public FeatureHost(ShellViewModel shell)
    {
        All =
        [
            new ClipboardHistoryFeature(shell),
            new ScreenshotShortcutFeature(shell),
            new TextFromScreenFeature(shell),
            new ColorPickerFeature(shell),
            new MicMuteFeature(shell),
            new OutputSwitchFeature(shell),
            new HeadphoneNoticeFeature(shell),
            new AlwaysOnTopFeature(shell),
            new WindowKeeperFeature(shell),
            new UpdateGuardFeature(shell),
            new LowDiskFeature(shell),
            new ExplorerMenuFeature(shell),
            new GameModeFeature(shell),
            new KeepAwakeFeature(shell),
            new VencordGuardFeature(shell),
            .. TweakCatalog.All.Select(definition => new TweakFeature(shell, definition)),
        ];
    }

    public IReadOnlyList<Feature> All { get; }

    public T Get<T>() where T : Feature => All.OfType<T>().First();

    public void StartAll()
    {
        foreach (var feature in All) feature.Apply();
    }

    public void StopAll()
    {
        foreach (var feature in All) feature.Shutdown();
    }
}
