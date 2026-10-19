using Avalonia.Threading;
using MultiTool.Modules.Soundboard.Audio;
using MultiTool.Modules.Soundboard.Models;

namespace MultiTool.Modules.Soundboard.Services;

/// <summary>Composition root. Wired by hand instead of a DI container: at this size a container only costs startup time and indirection.</summary>
public sealed class AppServices : IDisposable
{
    private readonly ModulePaths _paths;
    private readonly DebouncedSaver _settingsSaver;
    private readonly DebouncedSaver _librarySaver;
    private bool _disposed;

    public AppServices(ModulePaths paths)
    {
        _paths = paths;
        Settings = JsonStore.Load(paths.SettingsFile, () => new AppSettings());

        var (folders, sounds) = SoundLibrary.Load(paths.LibraryFile);
        Folders = folders;
        Sounds = sounds;

        Engine = new AudioEngine();
        Hotkeys = new GlobalHotkeyService();
        Triggers = new SerialWorkQueue("Soundboard.Trigger");
        Recorder = new HotkeyRecorder(Hotkeys);

        _settingsSaver = new DebouncedSaver(
            () => JsonStore.Serialize(Settings),
            json => JsonStore.WriteText(paths.SettingsFile, json));
        _librarySaver = new DebouncedSaver(
            () => SoundLibrary.Serialize(Folders, Sounds),
            json => JsonStore.WriteText(paths.LibraryFile, json));

        // Get the capture result out of the keyboard hook before anything happens; no work is
        // allowed inside the hook.
        Hotkeys.CaptureCompleted += hotkey =>
            Dispatcher.UIThread.Post(() => Recorder.Complete(hotkey));
    }

    /// <summary>The module's data folder and file names.</summary>
    public ModulePaths Paths => _paths;

    public AppSettings Settings { get; }

    public List<SoundEntry> Sounds { get; }

    public List<FolderEntry> Folders { get; }

    public AudioEngine Engine { get; }

    public GlobalHotkeyService Hotkeys { get; }

    /// <summary>Processes key presses outside the hook and the UI thread.</summary>
    public SerialWorkQueue Triggers { get; }

    public HotkeyRecorder Recorder { get; }

    public void SaveSettingsSoon() => _settingsSaver.Request();

    public void SaveLibrarySoon() => _librarySaver.Request();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Hotkeys.Dispose();
        _settingsSaver.Dispose();  // writes pending changes
        _librarySaver.Dispose();
        Triggers.Dispose();
        Engine.Dispose();
    }
}

/// <summary>Mediates capturing a key combination. Only one capture runs at a time; a new one cleanly cancels the previous.</summary>
public sealed class HotkeyRecorder(GlobalHotkeyService hotkeys)
{
    private Action<Hotkey?>? _pending;

    public bool IsRecording => _pending is not null;

    public void Begin(Action<Hotkey?> onResult)
    {
        Cancel();
        _pending = onResult;
        hotkeys.BeginCapture();
    }

    public void Cancel()
    {
        var pending = _pending;
        _pending = null;

        if (pending is null) return;

        hotkeys.CancelCapture();
        pending(null);
    }

    /// <summary>Called from the UI thread after the hook has returned.</summary>
    internal void Complete(Hotkey? hotkey)
    {
        var pending = _pending;
        _pending = null;
        pending?.Invoke(hotkey);
    }
}
