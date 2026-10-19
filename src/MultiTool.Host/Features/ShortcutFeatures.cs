using System.Runtime.InteropServices;
using System.Text;
using MultiTool.Host.ControlCenter;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;
using MultiTool.Host.Views;
using MultiTool.UI;

namespace MultiTool.Host.Features;

/// <summary>One shortcut mutes every microphone, or turns them all back on.</summary>
/// <remarks>
/// All active microphones, not only the default: a headset and a webcam both record. The state follows the
/// default microphone, so a second press always does the opposite of what the card said.
/// </remarks>
public sealed class MicMuteFeature(ShellViewModel shell) : HotkeyFeature(shell)
{
    private const string MicPath =
        "M12 2a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Zm-7 9h2a5 5 0 0 0 10 0h2a7 7 0 0 1-6 6.9V21h-2v-3.1A7 7 0 0 1 5 11Z";

    public override string Id => "mic-mute";
    public override string Name => "Mute microphones";
    public override string Description => "One shortcut mutes every microphone, in every program, and turns them back on.";
    public override string Group => "Sound";
    protected override string IconPath => MicPath;

    protected override bool OnByDefault => false;

    public override IReadOnlyList<HotkeyChoice> Choices { get; } =
    [
        new("Ctrl+Alt+M", HotkeyChoice.Control | HotkeyChoice.Alt, 0x4D),
        new("Win+Alt+M", HotkeyChoice.Win | HotkeyChoice.Alt, 0x4D),
        new("Pause", 0, 0x13),
        new("Ctrl+Alt+F10", HotkeyChoice.Control | HotkeyChoice.Alt, 0x79),
    ];

    protected override void Pressed()
    {
        var mute = SystemAudio.MicrophoneMuted() != true;
        var count = SystemAudio.SetAllMicrophonesMuted(mute);

        Toast.Show(NoticeKind.Devices,
            count == 0 ? "No microphone found" : mute ? "Microphones muted" : "Microphones on",
            count > 1 ? $"{count} microphones" : "",
            IconCache.Get(MicPath));
    }
}

/// <summary>One shortcut moves sound to the next output: speakers, headphones, monitor.</summary>
public sealed class OutputSwitchFeature(ShellViewModel shell) : HotkeyFeature(shell)
{
    private const string SpeakerPath =
        "M4 9h4l5-4v14l-5-4H4V9Zm11.5-1.3a6 6 0 0 1 0 8.6l-1.4-1.4a4 4 0 0 0 0-5.8l1.4-1.4Zm2.8-2.8a10 10 0 0 1 0 14.2l-1.4-1.4a8 8 0 0 0 0-11.4l1.4-1.4Z";

    public override string Id => "output-switch";
    public override string Name => "Switch sound output";
    public override string Description => "One shortcut moves sound to the next output device.";
    public override string Group => "Sound";
    protected override string IconPath => SpeakerPath;

    protected override bool OnByDefault => false;

    public override IReadOnlyList<HotkeyChoice> Choices { get; } =
    [
        new("Ctrl+Alt+O", HotkeyChoice.Control | HotkeyChoice.Alt, 0x4F),
        new("Win+Alt+O", HotkeyChoice.Win | HotkeyChoice.Alt, 0x4F),
        new("Ctrl+Alt+F11", HotkeyChoice.Control | HotkeyChoice.Alt, 0x7A),
    ];

    protected override async void Pressed()
    {
        // IPolicyConfig blocks for a moment while the device switches.
        var name = await Task.Run(SystemAudio.CycleOutput);
        Toast.Show(NoticeKind.Devices, name is null ? "No other output" : name, name is null ? "" : "Sound output", IconCache.Get(SpeakerPath));
    }
}

/// <summary>One shortcut keeps the active window above all others, the same one releases it.</summary>
/// <remarks>
/// A window of a program running as administrator can't be changed from an unelevated WinTo (Windows
/// blocks it); that is said instead of failing silently. The desktop and the taskbar are left alone.
/// </remarks>
public sealed partial class AlwaysOnTopFeature(ShellViewModel shell) : HotkeyFeature(shell)
{
    private const string PinPath =
        "M14.7 2.3 21.7 9.3 20.3 10.7 19 9.4 15.4 13 15.8 17.4 14.4 18.8 10.5 14.9 4.4 21 3 19.6 9.1 13.5 5.2 9.6 6.6 8.2 11 8.6 14.6 5 13.3 3.7Z";

    public override string Id => "always-on-top";
    public override string Name => "Always on top";
    public override string Description => "One shortcut keeps the active window above all others; pressed again, it lets go.";
    public override string Group => "Windows";
    protected override string IconPath => PinPath;

    public override IReadOnlyList<HotkeyChoice> Choices { get; } =
    [
        new("Win+Ctrl+T", HotkeyChoice.Win | HotkeyChoice.Control, 0x54),
        new("Ctrl+Alt+T", HotkeyChoice.Control | HotkeyChoice.Alt, 0x54),
        new("Win+Alt+P", HotkeyChoice.Win | HotkeyChoice.Alt, 0x50),
        new("Ctrl+Alt+F9", HotkeyChoice.Control | HotkeyChoice.Alt, 0x78),
    ];

    protected override void Pressed()
    {
        var window = GetForegroundWindow();
        if (window == 0 || IsShell(window)) return;

        var title = Title(window);
        var topmost = (GetWindowLongPtrW(window, -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) != 0;

        if (!SetWindowPos(window, topmost ? -2 /* HWND_NOTOPMOST */ : -1 /* HWND_TOPMOST */, 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */))
        {
            Toast.Show(NoticeKind.Shortcuts, "Not possible for this window", "It runs as administrator.", IconCache.Get(PinPath));
            return;
        }

        Toast.Show(NoticeKind.Shortcuts, topmost ? "No longer on top" : "Always on top", title, IconCache.Get(PinPath));
    }

    private static bool IsShell(nint window)
    {
        var name = new StringBuilder(64);
        GetClassNameW(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static string Title(nint window)
    {
        var text = new StringBuilder(256);
        GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int length);
}

/// <summary>One shortcut: drag over anything on screen, the text in it lands in the clipboard.</summary>
/// <remarks>Windows' own text recognition, from the capture tool, loaded quietly with "ocr".</remarks>
public sealed class TextFromScreenFeature(ShellViewModel shell) : HotkeyFeature(shell)
{
    public override string Id => "text-from-screen";
    public override string Name => "Copy text from screen";
    public override string Description => "Drag over text anywhere on screen, an image or a video included; it lands in the clipboard.";
    public override string Group => "Capture";

    protected override string IconPath =>
        "M4 4h5v2H6v3H4V4Zm11 0h5v5h-2V6h-3V4ZM4 15h2v3h3v2H4v-5Zm14 3v-3h2v5h-5v-2h3ZM8 8h8v2h-3v6h-2v-6H8V8Z";

    public override IReadOnlyList<HotkeyChoice> Choices { get; } =
    [
        new("Win+Shift+T", HotkeyChoice.Win | HotkeyChoice.Shift, 0x54),
        new("Win+Shift+X", HotkeyChoice.Win | HotkeyChoice.Shift, 0x58),
        new("Ctrl+Alt+F12", HotkeyChoice.Control | HotkeyChoice.Alt, 0x7B),
    ];

    protected override void Pressed() => Shell.OpenModule(ModuleIds.Capture, argument: "ocr", quiet: true);
}

/// <summary>One shortcut: click any pixel on screen, its color lands in the clipboard as hex.</summary>
public sealed class ColorPickerFeature(ShellViewModel shell) : HotkeyFeature(shell)
{
    public override string Id => "color-picker";
    public override string Name => "Pick a color";
    public override string Description => "Click any pixel on screen; its color lands in the clipboard as #RRGGBB.";
    public override string Group => "Capture";

    protected override string IconPath =>
        "M19.4 3.6a2.2 2.2 0 0 1 0 3.1l-2.2 2.2.9.9-1.4 1.4-.9-.9-6.6 6.6a2 2 0 0 1-1 .5L6 18l-2 2-1.4-1.4 2-2 .5-2.2a2 2 0 0 1 .5-1l6.6-6.6-.9-.9 1.4-1.4.9.9 2.2-2.2a2.2 2.2 0 0 1 3.1 0ZM12.8 9.6l-6 6-.2 1 1-.2 6-6-.8-.8Z";

    public override IReadOnlyList<HotkeyChoice> Choices { get; } =
    [
        new("Win+Shift+C", HotkeyChoice.Win | HotkeyChoice.Shift, 0x43),
        new("Win+Alt+C", HotkeyChoice.Win | HotkeyChoice.Alt, 0x43),
        new("Ctrl+Alt+F8", HotkeyChoice.Control | HotkeyChoice.Alt, 0x77),
    ];

    protected override void Pressed() => Shell.OpenModule(ModuleIds.Capture, argument: "color", quiet: true);
}
