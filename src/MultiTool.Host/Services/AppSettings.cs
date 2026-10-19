namespace MultiTool.Host.Services;

/// <summary>Shell settings, stored as readable JSON in <c>%APPDATA%\WinTo</c>.</summary>
public sealed class AppSettings
{
    /// <summary>How a module opens by default.</summary>
    public ModuleOpenMode OpenMode { get; set; } = ModuleOpenMode.Tab;

    /// <summary>Per-module overrides; win over <see cref="OpenMode"/>.</summary>
    /// <remarks>
    /// A module kept in view on the side wants its own window; one opened briefly wants a tab.
    /// </remarks>
    public Dictionary<string, ModuleOpenMode> OpenModePerModule { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modules pinned to the top of the home page.</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>Whether status probes run at startup (after the first frame, in the background).</summary>
    public bool ScanStatusOnStart { get; set; } = true;

    /// <summary>Whether the module instance is released when its view closes.</summary>
    /// <remarks>
    /// Releasing disposes the module and returns devices, threads, buffers and files. The module's
    /// <i>code</i> stays loaded (see ModuleLoadContext). When off, reopening shows the same state.
    /// </remarks>
    public bool ReleaseOnClose { get; set; } = true;

    /// <summary>Keep the computer (not the screen) awake while the app runs. See <see cref="StayAwake"/>.</summary>
    public bool KeepAwake { get; set; }

    /// <summary>Closing the main window keeps WinTo in the tray instead of quitting.</summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>A start shows the window. Off since 0.13.0: WinTo starts into the tray, like a menu bar app;
    /// a second start or the control center opens the window.</summary>
    public bool OpenWindowOnStart { get; set; }

    /// <summary>Last window size and position.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Win+Shift+S opens WinTo's region capture instead of the Snipping Tool.</summary>
    public bool ScreenshotShortcut { get; set; } = true;

    /// <summary>Whether the introduction was shown. Missing in older files, so it shows once after an update.</summary>
    public bool IntroShown { get; set; }

    /// <summary>Displays with DDC/CI at the last read: the control center reserves their rows while it reads.</summary>
    public int ControlCenterDisplays { get; set; } = 1;

    /// <summary>
    /// The accent color: empty for the default violet, <c>windows</c> for the Windows accent color, otherwise
    /// <c>#RRGGBB</c>. Applied by <see cref="Accent.Apply"/>.
    /// </summary>
    public string Accent { get; set; } = "";

    /// <summary>Every start elevated through a scheduled task; see <see cref="ElevatedStart"/>.</summary>
    public bool RunElevated { get; set; }

    /// <summary>Feature: a card when Bluetooth headphones connect or disconnect.</summary>
    public bool HeadphoneNotice { get; set; } = true;

    /// <summary>Feature: a faster power plan while a listed game runs.</summary>
    public GameModeSettings GameMode { get; set; } = new();

    /// <summary>How the cards at the screen edge look and which ones appear; see <c>Views.Toast</c>.</summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>Feature: the clipboard history's options.</summary>
    public ClipboardSettings Clipboard { get; set; } = new();

    /// <summary>Feature: Vencord is patched into a new Discord version as soon as Discord downloads it.</summary>
    public bool VencordGuard { get; set; } = true;

    /// <summary>On/off of features that have no property of their own, by feature id.</summary>
    public Dictionary<string, bool> Switches { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chosen combination of shortcut features, by feature id (the choice's label).</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per Discord branch, the newest version seen patched. Only these branches are kept patched.</summary>
    public Dictionary<string, string> VencordPatched { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registry values as they were before a Windows tweak was switched on, per tweak id (0.13.0).</summary>
    /// <remarks>Switching the tweak off writes them back; without an entry it writes Windows' defaults.</remarks>
    public Dictionary<string, List<RegistryBackup>> TweakBackups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The low disk notice: below this many percent free on a fixed drive (0.13.0).</summary>
    public int LowDiskPercent { get; set; } = 10;
}

/// <summary>One registry value as it was. <see cref="Kind"/> <c>null</c>: the value didn't exist.</summary>
/// <param name="Key">Below <c>HKEY_CURRENT_USER</c>.</param>
public sealed record RegistryBackup(string Key, string Name, string? Kind, string? Value);

/// <summary>Where a card appears on its display.</summary>
public enum NoticeCorner
{
    TopRight,
    TopLeft,
    TopCenter,
    BottomRight,
    BottomLeft,
}

/// <summary>What a card is about; each kind can be switched off.</summary>
public enum NoticeKind
{
    /// <summary>Results of work started outside the window: a screenshot, a saved clip, copied text.</summary>
    Results,

    /// <summary>Headphones connected, sound output switched, microphones muted.</summary>
    Devices,

    /// <summary>Confirmations of a shortcut: always on top.</summary>
    Shortcuts,

    /// <summary>Low disk space, a pending restart.</summary>
    System,

    /// <summary>The app's own messages while its window is closed.</summary>
    App,
}

/// <summary>The cards at the screen edge (since 0.13.0, Settings → Notifications).</summary>
public sealed class NotificationSettings
{
    public NoticeCorner Corner { get; set; } = NoticeCorner.TopRight;

    /// <summary>On the display with the mouse pointer instead of the primary one.</summary>
    public bool FollowPointer { get; set; }

    public int Seconds { get; set; } = 5;

    public bool Large { get; set; }

    /// <summary>A bar in the accent color at the card's left edge.</summary>
    public bool AccentEdge { get; set; }

    /// <summary>Windows' notification sound with each card.</summary>
    public bool Sound { get; set; }

    /// <summary>No cards while a game, a full-screen program or a presentation runs.</summary>
    public bool QuietInFullScreen { get; set; } = true;

    /// <summary>Kinds switched off; all are on by default.</summary>
    public List<NoticeKind> Hidden { get; set; } = [];
}

/// <summary>How the clipboard panel finds entries.</summary>
public enum ClipSearch
{
    /// <summary>The words as typed, anywhere in the entry.</summary>
    Exact,

    /// <summary>Exact matches first, then entries holding the letters in order ("gthb" finds "github").</summary>
    Fuzzy,

    /// <summary>A .NET regular expression.</summary>
    Regex,
}

/// <summary>The order below the pinned entries.</summary>
public enum ClipOrder
{
    LastCopied,
    FirstCopied,
    MostCopied,
}

/// <summary>Where the clipboard panel opens.</summary>
public enum ClipPlacement
{
    Pointer,

    /// <summary>At the text cursor where a program reports one, otherwise at the mouse pointer.</summary>
    Caret,

    Center,
}

/// <summary>The clipboard history (0.13.0, from what Maccy offers on the Mac).</summary>
public sealed class ClipboardSettings
{
    public int MaxEntries { get; set; } = 200;

    public bool KeepText { get; set; } = true;

    public bool KeepImages { get; set; } = true;

    public bool KeepFiles { get; set; } = true;

    /// <summary>Enter pastes with formatting (HTML, RTF) where it was copied with some; Shift+Enter the other way.</summary>
    public bool KeepFormatting { get; set; } = true;

    public ClipSearch Search { get; set; } = ClipSearch.Fuzzy;

    public ClipOrder Order { get; set; } = ClipOrder.LastCopied;

    public ClipPlacement Placement { get; set; } = ClipPlacement.Pointer;

    /// <summary>Process names whose copies are not kept, e.g. a banking app or a terminal.</summary>
    public List<string> IgnoredApps { get; set; } = [];

    /// <summary>Regular expressions; text matching one is not kept, e.g. card numbers.</summary>
    public List<string> IgnoredPatterns { get; set; } = [];

    /// <summary>New copies are not kept until resumed.</summary>
    public bool Paused { get; set; }
}

/// <summary>The auto game mode feature; until 0.13.0 the game mode module's own settings.</summary>
public sealed class GameModeSettings
{
    public bool Enabled { get; set; }

    /// <summary>Process names without <c>.exe</c>.</summary>
    public List<string> Games { get; set; } = [];

    /// <summary>The gaming plan's id. Empty means the best available one.</summary>
    public string GamePlan { get; set; } = "";

    /// <summary>The previous plan while game mode applies.</summary>
    /// <remarks>
    /// On disk, not only in memory: after an unexpected exit the computer would otherwise stay on high
    /// performance for good. The next start restores it.
    /// </remarks>
    public string RestorePlan { get; set; } = "";
}

public enum ModuleOpenMode
{
    /// <summary>As a tab in the main window.</summary>
    Tab,

    /// <summary>In its own window.</summary>
    Window,
}

public sealed class WindowPlacement
{
    public double Width { get; set; }
    public double Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool Maximized { get; set; }
}
