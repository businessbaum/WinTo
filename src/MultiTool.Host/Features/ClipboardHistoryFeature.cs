using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Host.Features.Clipboard;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;
using MultiTool.Host.Views;
using MultiTool.UI;

namespace MultiTool.Host.Features;

/// <summary>Keeps what was copied; a shortcut opens the list at the mouse pointer, Enter pastes.</summary>
/// <remarks>
/// <para>
/// <b>Replaces the Shelf tool (0.13.0).</b> Parking things needed a window; a history fills itself.
/// Text, images and file lists are kept; pinned entries stay until removed.
/// </para>
/// <para>
/// <b>Pasting</b> puts the entry on the clipboard, gives the focus back to the window that had it before the
/// panel opened, and presses Ctrl+V there once every modifier is released. Without such a window the entry
/// is only copied.
/// </para>
/// <para>
/// <b>From Maccy (0.13.0)</b>: formatting kept with a text (Shift+Enter pastes plain), Ctrl+1 to Ctrl+9, search
/// modes, order, history size, kinds to keep, ignored programs and patterns, a pause, where the panel opens.
/// All in <see cref="ClipboardSettings"/>, set in the options below the switch.
/// </para>
/// </remarks>
public sealed partial class ClipboardHistoryFeature : HotkeyFeature
{
    internal const string ClipboardPath =
        "M9 2a2 2 0 0 0-2 2H6a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2h-1a2 2 0 0 0-2-2H9Zm0 2h6v2H9V4ZM6 6h1a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2h1v13H6V6Zm3 5a1 1 0 1 0 0 2h6a1 1 0 1 0 0-2H9Zm0 4a1 1 0 1 0 0 2h4a1 1 0 1 0 0-2H9Z";

    private readonly ClipboardHistory _history;
    private ClipboardListener? _listener;
    private ClipboardPanel? _panel;

    public ClipboardHistoryFeature(ShellViewModel shell) : base(shell)
    {
        _history = new ClipboardHistory(() => Settings.Clipboard);
        ClipOptions = new ClipboardOptions(this, ShortcutOptions, Settings.Clipboard);
        _history.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDetail);
    }

    public override string Id => "clipboard-history";
    public override string Name => "Clipboard history";
    public override string Description =>
        "Keeps copied text, images and files. Win+V opens the list at the mouse pointer instead of Windows' own; Enter pastes.";
    public override string Group => "Clipboard";
    protected override string IconPath => ClipboardPath;

    public override IReadOnlyList<HotkeyChoice> Choices { get; } =
    [
        HotkeyChoice.FromWindows("Win+V", 0x56),
        new("Win+Alt+V", HotkeyChoice.Win | HotkeyChoice.Alt, 0x56),
        new("Ctrl+Alt+H", HotkeyChoice.Control | HotkeyChoice.Alt, 0x48),
        new("Ctrl+Shift+F11", HotkeyChoice.Control | HotkeyChoice.Shift, 0x7A),
    ];

    public ClipboardOptions ClipOptions { get; }

    public override object? Options => ClipOptions;

    internal ClipboardHistory History => _history;

    /// <summary>Stores a changed option and refreshes what shows it.</summary>
    internal void OptionsChanged()
    {
        Shell.SaveSettings();
        UpdateDetail();
    }

    protected override void Start()
    {
        base.Start();
        _history.Load();
        _listener = new ClipboardListener(_history.Add);
        UpdateDetail();
    }

    protected override void Stop()
    {
        _listener?.Dispose();
        _listener = null;
        _panel?.Close();
        base.Stop();
    }

    /// <remarks>Loads the history even while switched off, so it can be cleared then too.</remarks>
    protected override void OptionsOpened()
    {
        _history.Load();
        ClipOptions.Refresh();
    }

    private void UpdateDetail()
    {
        ClipOptions.Refresh();
        if (_listener is null) return;

        // A taken shortcut keeps its message; the count matters less than a dead shortcut.
        if (!Registered) return;

        var count = _history.Count;
        var shortcut = Active?.Label ?? Choice.Label;
        var paused = Settings.Clipboard.Paused ? " · paused" : "";
        Detail = (count == 0 ? shortcut : $"{shortcut} · {count} {(count == 1 ? "entry" : "entries")}") + paused;
    }

    protected override void Pressed()
    {
        // The same shortcut closes it again.
        if (_panel is not null)
        {
            _panel.Close();
            return;
        }

        var target = GetForegroundWindow();
        var model = new ClipboardPanelViewModel(_history, Settings.Clipboard, OptionsChanged);
        var panel = new ClipboardPanel { DataContext = model, Placement = Settings.Clipboard.Placement, Target = target };

        model.PasteRequested += (record, plain) =>
        {
            panel.Close();
            _ = PasteAsync(record, plain, target);
        };

        panel.Closed += (_, _) =>
        {
            model.Dispose();
            if (_panel == panel) _panel = null;
        };

        _panel = panel;
        panel.Show();
    }

    private async Task PasteAsync(ClipRecord record, bool plain, nint target)
    {
        var problem = await Task.Run(() => record.Kind switch
        {
            ClipKind.Text when plain => ClipboardNative.WriteText(record.Text ?? ""),
            ClipKind.Text => _history.ReadRich(record) is var (html, rtf)
                ? ClipboardNative.WriteText(record.Text ?? "", html, rtf)
                : null,
            ClipKind.Files => ClipboardNative.WriteFiles(record.Files ?? []),
            _ => _history.ImagePath(record) is { } path && File.Exists(path)
                ? ClipboardNative.WriteImage(File.ReadAllBytes(path))
                : "The image file is gone.",
        });

        if (problem is not null)
        {
            Toast.Show(NoticeKind.App, "Not pasted", problem, IconCache.Get(ClipboardPath));
            return;
        }

        if (target == 0 || !IsWindow(target)) return;

        SetForegroundWindow(target);
        await PressPasteAsync();
    }

    /// <summary>Ctrl+V into the window that now has the focus.</summary>
    /// <remarks>
    /// Waits until Shift, Ctrl, Alt and Win are up (at most a second): a still-held Shift from the panel
    /// would turn it into Ctrl+Shift+V, which some programs read as "paste without formatting".
    /// </remarks>
    private static async Task PressPasteAsync()
    {
        await Task.Delay(60);

        for (var waited = 0; waited < 1000 && AnyModifierDown(); waited += 25) await Task.Delay(25);

        SendCtrlV();
    }

    private static bool AnyModifierDown() =>
        Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static unsafe void SendCtrlV()
    {
        const ushort control = 0x11;
        const ushort v = 0x56;
        const uint keyUp = 0x2;

        var inputs = stackalloc Input[4];
        inputs[0] = new Input { Type = 1, VirtualKey = control };
        inputs[1] = new Input { Type = 1, VirtualKey = v };
        inputs[2] = new Input { Type = 1, VirtualKey = v, Flags = keyUp };
        inputs[3] = new Input { Type = 1, VirtualKey = control, Flags = keyUp };
        _ = SendInput(4, inputs, sizeof(Input));
    }

    /// <summary>From the options: removes every unpinned entry after asking.</summary>
    internal async Task ClearAsync()
    {
        var count = _history.Entries.Count(e => !e.Pinned);
        if (count == 0) return;

        if (!await Shell.Api.ConfirmAsync("Clear clipboard history",
                $"{count} {(count == 1 ? "entry" : "entries")} will be removed. Pinned entries stay.", "Clear", danger: true))
            return;

        _history.ClearUnpinned();
    }

    internal void OpenFolder()
    {
        Directory.CreateDirectory(_history.Folder);
        Shell.Api.RevealInExplorer(_history.Folder);
    }

    /// <summary>INPUT with KEYBDINPUT: 40 bytes on x64 (4 type, 4 padding, 32 union).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static unsafe partial uint SendInput(uint count, Input* inputs, int size);
}

/// <summary>Below the clipboard history's switch: shortcut, what is kept, search, order, ignoring, clearing.</summary>
public sealed partial class ClipboardOptions : ObservableObject
{
    private static readonly int[] Sizes = [50, 100, 200, 500, 1000];

    private readonly ClipboardHistoryFeature _feature;
    private readonly ClipboardSettings _settings;
    private readonly bool _loading = true;

    public ClipboardOptions(ClipboardHistoryFeature feature, HotkeyOptions shortcut, ClipboardSettings settings)
    {
        _feature = feature;
        _settings = settings;
        Shortcut = shortcut;

        var size = Array.IndexOf(Sizes, settings.MaxEntries);
        SizeIndex = size >= 0 ? size : 2;
        KeepText = settings.KeepText;
        KeepImages = settings.KeepImages;
        KeepFiles = settings.KeepFiles;
        KeepFormatting = settings.KeepFormatting;
        SearchIndex = (int)settings.Search;
        OrderIndex = (int)settings.Order;
        PlacementIndex = (int)settings.Placement;
        IgnoredApps = string.Join(", ", settings.IgnoredApps);
        IgnoredPatterns = string.Join("\n", settings.IgnoredPatterns);
        Paused = settings.Paused;
        PatternProblem = Check(settings.IgnoredPatterns);

        _loading = false;
    }

    public HotkeyOptions Shortcut { get; }

    public IReadOnlyList<string> SizeChoices { get; } = [.. Sizes.Select(s => $"{s} entries")];

    /// <summary>In the order of <see cref="ClipSearch"/>.</summary>
    public IReadOnlyList<string> SearchChoices { get; } = ["Exact", "Fuzzy", "Regular expression"];

    /// <summary>In the order of <see cref="ClipOrder"/>.</summary>
    public IReadOnlyList<string> OrderChoices { get; } = ["Last copied", "First copied", "Most copied"];

    /// <summary>In the order of <see cref="ClipPlacement"/>.</summary>
    public IReadOnlyList<string> PlacementChoices { get; } = ["Mouse pointer", "Text cursor", "Screen center"];

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial int SizeIndex { get; set; }

    [ObservableProperty]
    public partial bool KeepText { get; set; }

    [ObservableProperty]
    public partial bool KeepImages { get; set; }

    [ObservableProperty]
    public partial bool KeepFiles { get; set; }

    [ObservableProperty]
    public partial bool KeepFormatting { get; set; }

    [ObservableProperty]
    public partial int SearchIndex { get; set; }

    [ObservableProperty]
    public partial int OrderIndex { get; set; }

    [ObservableProperty]
    public partial int PlacementIndex { get; set; }

    /// <summary>Process names, comma separated, as the panel shows them ("from chrome").</summary>
    [ObservableProperty]
    public partial string IgnoredApps { get; set; } = "";

    /// <summary>One regular expression per line.</summary>
    [ObservableProperty]
    public partial string IgnoredPatterns { get; set; } = "";

    [ObservableProperty]
    public partial string? PatternProblem { get; set; }

    [ObservableProperty]
    public partial bool Paused { get; set; }

    partial void OnSizeIndexChanged(int value) => Change(s => s.MaxEntries = Sizes[Math.Clamp(value, 0, Sizes.Length - 1)], trim: true);

    partial void OnKeepTextChanged(bool value) => Change(s => s.KeepText = value);

    partial void OnKeepImagesChanged(bool value) => Change(s => s.KeepImages = value);

    partial void OnKeepFilesChanged(bool value) => Change(s => s.KeepFiles = value);

    partial void OnKeepFormattingChanged(bool value) => Change(s => s.KeepFormatting = value);

    partial void OnSearchIndexChanged(int value) => Change(s => s.Search = (ClipSearch)Math.Clamp(value, 0, 2));

    partial void OnOrderIndexChanged(int value) => Change(s => s.Order = (ClipOrder)Math.Clamp(value, 0, 2));

    partial void OnPlacementIndexChanged(int value) => Change(s => s.Placement = (ClipPlacement)Math.Clamp(value, 0, 2));

    partial void OnPausedChanged(bool value) => Change(s => s.Paused = value);

    partial void OnIgnoredAppsChanged(string value) => Change(s => s.IgnoredApps =
        [.. value.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a)]);

    partial void OnIgnoredPatternsChanged(string value)
    {
        var patterns = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        PatternProblem = Check(patterns);
        Change(s => s.IgnoredPatterns = patterns);
    }

    /// <summary>The first pattern that doesn't parse, said; a broken one never matches.</summary>
    private static string? Check(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern);
            }
            catch (ArgumentException)
            {
                return $"Not a valid pattern, ignored: {pattern}";
            }
        }

        return null;
    }

    private void Change(Action<ClipboardSettings> change, bool trim = false)
    {
        if (_loading) return;

        change(_settings);
        _feature.OptionsChanged();
        if (trim) _feature.History.Trim();
    }

    /// <summary>The panel paused or resumed; the switch here follows.</summary>
    internal void SyncPaused()
    {
        if (Paused != _settings.Paused) Paused = _settings.Paused;
    }

    internal void Refresh()
    {
        SyncPaused();

        var entries = _feature.History.Entries;
        var pinned = entries.Count(e => e.Pinned);
        var images = entries.Count(e => e.Kind == ClipKind.Image);

        Summary = entries.Count == 0
            ? "Empty."
            : $"{entries.Count} of at most {_feature.History.MaxEntries} entries · {images} images · {pinned} pinned. " +
              "Copies marked private by password managers are not kept.";
    }

    [RelayCommand]
    private Task ClearAsync() => _feature.ClearAsync();

    [RelayCommand]
    private void OpenFolder() => _feature.OpenFolder();
}
