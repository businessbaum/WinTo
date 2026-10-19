using Avalonia.Threading;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>Win+Shift+S opens WinTo's region capture instead of the Snipping Tool.</summary>
/// <remarks>
/// The hook lives here (<see cref="WinKeyShortcuts"/>); the capture tool is loaded only when the keys
/// are pressed and released again once the capture is done.
/// </remarks>
public sealed class ScreenshotShortcutFeature(ShellViewModel shell) : Feature(shell)
{
    public override string Id => "screenshot-shortcut";

    public override string Name => "Screenshot shortcut";

    public override string Description => "Win+Shift+S captures a region with WinTo: clipboard, Screenshots folder and a preview card.";

    public override string Group => "Capture";

    protected override string IconPath =>
        "M4 7a3 3 0 0 1 3-3h1a1 1 0 0 1 0 2H7a1 1 0 0 0-1 1v1a1 1 0 0 1-2 0V7Zm11-2a1 1 0 0 1 1-1h1a3 3 0 0 1 3 3v1a1 1 0 1 1-2 0V7a1 1 0 0 0-1-1h-1a1 1 0 0 1-1-1ZM5 15a1 1 0 0 1 1 1v1a1 1 0 0 0 1 1h1a1 1 0 1 1 0 2H7a3 3 0 0 1-3-3v-1a1 1 0 0 1 1-1Zm14 0a1 1 0 0 1 1 1v1a3 3 0 0 1-3 3h-1a1 1 0 1 1 0-2h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1ZM12 9a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z";

    protected override bool ReadEnabled() => Settings.ScreenshotShortcut;

    protected override void StoreEnabled(bool value) => Settings.ScreenshotShortcut = value;

    protected override void Start()
    {
        if (Shell.Catalog.Find(ModuleIds.Capture) is null)
            throw new InvalidOperationException("The capture tool is not installed.");

        WinKeyShortcuts.Set(WinKeyShortcuts.KeyS, shift: true, () => Dispatcher.UIThread.Post(() =>
        {
            Shell.OpenModule(ModuleIds.Capture, argument: "quick", quiet: true);
            ReportUsed();
        }));
        Detail = "Win+Shift+S";
    }

    protected override void Stop()
    {
        WinKeyShortcuts.Remove(WinKeyShortcuts.KeyS);
        Detail = "Off";
    }
}
