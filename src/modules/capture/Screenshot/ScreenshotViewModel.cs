using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>What goes into the module's <c>settings.json</c>.</summary>
public sealed class ScreenshotSettings
{
    public string TargetDirectory { get; set; } = "";
    public bool AutoSave { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public bool AutoRecognize { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public string Hotkey { get; set; } = "";

    /// <summary>What the hotkey captures: <c>false</c> the display under the pointer, <c>true</c> a region.</summary>
    public bool HotkeyTakesRegion { get; set; }

    public int DelaySeconds { get; set; }
}

/// <summary>A saved screenshot in the list below.</summary>
public sealed record RecentShot(string Path, string Name, string Detail);

public sealed partial class ScreenshotViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private readonly GlobalHotkey _hotkey = new();
    private readonly bool _loading;

    /// <summary>The last captured image, raw, for "Save as" and OCR.</summary>
    private CapturedImage? _last;

    /// <summary>Where the last captured image was saved, if at all.</summary>
    private string? _lastPath;

    public ScreenshotViewModel(IModuleContext context)
    {
        _loading = true;
        _context = context;

        var settings = context.Settings.Load<ScreenshotSettings>();

        TargetDirectory = ScreenshotFolder.Resolve(settings.TargetDirectory);

        AutoSave = settings.AutoSave;
        CopyToClipboard = settings.CopyToClipboard;
        AutoRecognize = settings.AutoRecognize;
        HotkeyEnabled = settings.HotkeyEnabled;
        SelectedHotkey = GlobalHotkey.ById(settings.Hotkey);
        HotkeyTakesRegion = settings.HotkeyTakesRegion;
        SelectedDelay = Delays.Contains(settings.DelaySeconds) ? settings.DelaySeconds : 0;

        _hotkey.Pressed += OnHotkeyPressed;
        _loading = false;
    }

    /// <summary>Tells the shell what is running. Wired by the module.</summary>
    public event EventHandler<ModuleActivity>? ActivityChanged;

    /// <summary>Set by the view once it is attached to a window. Only for text; images go through
    /// <see cref="ClipboardImage"/>.</summary>
    public IClipboard? Clipboard { get; set; }

    /// <summary>The window hosting the view; it briefly leaves the picture while capturing.</summary>
    public Window? HostWindow { get; set; }

    // --------------------------------------------------------------- Sources

    public ObservableCollection<Monitor> MonitorList { get; } = [];

    public ObservableCollection<CaptureWindow> WindowList { get; } = [];

    [ObservableProperty] public partial Monitor? SelectedMonitor { get; set; }
    [ObservableProperty] public partial CaptureWindow? SelectedWindow { get; set; }

    public IReadOnlyList<int> Delays { get; } = [0, 3, 5, 10];

    [ObservableProperty] public partial int SelectedDelay { get; set; }

    // --------------------------------------------------------------- Settings

    [ObservableProperty] public partial string TargetDirectory { get; set; } = "";
    [ObservableProperty] public partial bool AutoSave { get; set; }
    [ObservableProperty] public partial bool CopyToClipboard { get; set; }
    [ObservableProperty] public partial bool AutoRecognize { get; set; }
    [ObservableProperty] public partial bool HotkeyEnabled { get; set; }
    [ObservableProperty] public partial bool HotkeyTakesRegion { get; set; }
    [ObservableProperty] public partial HotkeyChoice SelectedHotkey { get; set; } = GlobalHotkey.Default;

    public IReadOnlyList<HotkeyChoice> Hotkeys => GlobalHotkey.Choices;

    /// <summary>Under the hotkey picker: whether it works, or why not.</summary>
    [ObservableProperty] public partial string HotkeyText { get; set; } = "";

    [ObservableProperty] public partial bool HotkeyProblem { get; set; }

    partial void OnAutoSaveChanged(bool value) => Save();
    partial void OnCopyToClipboardChanged(bool value) => Save();
    partial void OnAutoRecognizeChanged(bool value) => Save();
    partial void OnHotkeyTakesRegionChanged(bool value)
    {
        OnPropertyChanged(nameof(HotkeyTakesScreen));
        Save();
    }

    /// <summary>
    /// The counterpart of <see cref="HotkeyTakesRegion"/> for the second radio button; a binding on
    /// <c>!HotkeyTakesRegion</c> only works one way.
    /// </summary>
    public bool HotkeyTakesScreen
    {
        get => !HotkeyTakesRegion;
        set { if (value) HotkeyTakesRegion = false; }
    }
    partial void OnSelectedDelayChanged(int value) => Save();

    partial void OnHotkeyEnabledChanged(bool value)
    {
        if (_loading) return;
        Save();
        ApplyHotkey();
    }

    partial void OnSelectedHotkeyChanged(HotkeyChoice value)
    {
        if (_loading) return;
        Save();
        ApplyHotkey();
    }

    // --------------------------------------------------------------- Result

    [ObservableProperty] public partial bool HasPreview { get; set; }
    [ObservableProperty] public partial string OcrText { get; set; } = "";
    [ObservableProperty] public partial string OcrInfo { get; set; } = "";
    [ObservableProperty] public partial bool HasOcrText { get; set; }
    [ObservableProperty] public partial bool IsRecognizing { get; set; }
    [ObservableProperty] public partial bool IsCapturing { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    public ObservableCollection<RecentShot> Recent { get; } = [];

    [ObservableProperty] public partial bool HasRecent { get; set; }

    partial void OnOcrTextChanged(string value) => HasOcrText = value.Length > 0;

    /// <summary>The text panel below the picture: only once text recognition ran, so the picture gets the room.</summary>
    public bool ShowTextPanel => HasPreview && (IsRecognizing || OcrInfo.Length > 0);

    partial void OnHasPreviewChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTextPanel));
        OnPropertyChanged(nameof(OptionsVisible));
    }

    /// <summary>The capture options on the left. Closed when an existing screenshot opens: it is there to be
    /// looked at and edited, not to take a new one.</summary>
    [ObservableProperty] public partial bool ShowOptions { get; set; } = true;

    /// <summary>With nothing to show, the options are all there is.</summary>
    public bool OptionsVisible => ShowOptions || !HasPreview;

    partial void OnShowOptionsChanged(bool value) => OnPropertyChanged(nameof(OptionsVisible));

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    partial void OnIsRecognizingChanged(bool value) => OnPropertyChanged(nameof(ShowTextPanel));

    partial void OnOcrInfoChanged(string value) => OnPropertyChanged(nameof(ShowTextPanel));

    [RelayCommand]
    private void CloseText()
    {
        OcrText = "";
        OcrInfo = "";
    }

    // --------------------------------------------------------------- Lifecycle

    /// <summary>On module load: register the hotkey. Displays and windows come with the view.</summary>
    public void Start() => ApplyHotkey();

    /// <summary>Reloads displays and windows. Cheap, a few system calls.</summary>
    [RelayCommand]
    public void RefreshSources()
    {
        var monitor = SelectedMonitor?.Bounds;
        MonitorList.Clear();
        foreach (var m in Monitors.All()) MonitorList.Add(m);
        SelectedMonitor = MonitorList.FirstOrDefault(m => m.Bounds == monitor) ?? MonitorList.FirstOrDefault();

        var window = SelectedWindow?.Handle;
        WindowList.Clear();
        foreach (var w in ScreenCapture.Windows(Environment.ProcessId)) WindowList.Add(w);
        SelectedWindow = WindowList.FirstOrDefault(w => w.Handle == window) ?? WindowList.FirstOrDefault();

        RefreshRecent();
    }

    private void ApplyHotkey()
    {
        _hotkey.Stop();

        if (!HotkeyEnabled)
        {
            HotkeyText = "Off.";
            HotkeyProblem = false;
            return;
        }

        var choice = SelectedHotkey;
        if (_hotkey.Start(choice))
        {
            HotkeyText = HotkeyTakesRegion
                ? $"{choice.Label} captures a region while the module is open."
                : $"{choice.Label} captures the display under the mouse pointer while the module is open.";
            HotkeyProblem = false;
            return;
        }

        HotkeyText = $"{choice.Label} is taken by another program. Choose another combination.";
        HotkeyProblem = true;
        _context.Log.Warn($"Hotkey {choice.Label} could not be registered.");
    }

    /// <summary>Comes from the hotkey thread.</summary>
    private void OnHotkeyPressed() => Dispatcher.UIThread.Post(() =>
    {
        if (IsCapturing) return;

        if (HotkeyTakesRegion)
        {
            _ = CaptureRegionAsync();
            return;
        }

        // No delay: whoever presses the key mid-game wants exactly that moment.
        _ = CaptureAsync(() =>
        {
            var monitor = Monitors.UnderCursor() ?? throw new InvalidOperationException("No display found.");
            var b = monitor.Bounds;
            return Task.FromResult<CapturedImage?>(ScreenCapture.Screen(b.X, b.Y, b.Width, b.Height));
        }, hideHost: HostWindow?.IsActive == true, delay: 0);
    });

    // --------------------------------------------------------------- Capturing

    [RelayCommand]
    private Task CaptureRegionAsync() => CaptureAsync(async () =>
    {
        var (x, y, width, height) = ScreenCapture.VirtualScreen();
        var desktop = ScreenCapture.Screen(x, y, width, height);
        var rect = await RegionPicker.PickAsync(Monitors.All(), desktop, (x, y));

        return rect is { } r ? desktop.Crop(r.X - x, r.Y - y, r.Width, r.Height) : null;
    }, hideHost: true, delay: SelectedDelay);

    /// <summary>Win+Shift+S: a region straight away, without hiding or showing any window.</summary>
    /// <remarks>
    /// Busy while it runs and idle afterwards, also when cancelled: a module the shell loaded only for this
    /// is released on idle, which is why the clipboard is written right away (<see cref="ClipboardImage"/>).
    /// The result is announced through the shell as a card with a thumbnail; a click opens the image here.
    /// </remarks>
    public async Task QuickRegionAsync()
    {
        if (IsCapturing) return;

        ActivityChanged?.Invoke(this, new ModuleActivity(true, "Capturing…"));
        try
        {
            await CaptureAsync(async () =>
            {
                var (x, y, width, height) = ScreenCapture.VirtualScreen();
                var desktop = ScreenCapture.Screen(x, y, width, height);
                var rect = await RegionPicker.PickAsync(Monitors.All(), desktop, (x, y));

                return rect is { } r ? desktop.Crop(r.X - x, r.Y - y, r.Width, r.Height) : null;
            }, hideHost: false, delay: 0, announce: true);
        }
        finally
        {
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    /// <summary>From the app's "Copy text from screen" shortcut: drag over text, it lands in the clipboard.</summary>
    /// <remarks>Without a view, like <see cref="QuickRegionAsync"/>; busy while it runs so the shell releases it after.</remarks>
    public async Task QuickTextAsync()
    {
        if (IsCapturing) return;

        IsCapturing = true;
        ActivityChanged?.Invoke(this, new ModuleActivity(true, "Reading text…"));
        try
        {
            var (x, y, width, height) = ScreenCapture.VirtualScreen();
            var desktop = ScreenCapture.Screen(x, y, width, height);
            var rect = await RegionPicker.PickAsync(Monitors.All(), desktop, (x, y), "Drag over the text · Esc cancels");
            if (rect is not { } r || desktop.Crop(r.X - x, r.Y - y, r.Width, r.Height) is not { } part) return;

            var result = await WindowsOcr.RecognizeAsync(part.Pixels, part.Width, part.Height, _context.Shutdown);

            if (result.Problem is { } problem)
            {
                _context.Shell.Announce(new Announcement("Text recognition failed", problem) { Level = StatusLevel.Warn });
                return;
            }

            if (result.Text.Length == 0)
            {
                _context.Shell.Announce(new Announcement("No text recognized", "Try a larger region.") { Level = StatusLevel.Warn });
                return;
            }

            var lines = result.Text.Split(Environment.NewLine).Length;
            var copied = ClipboardImage.SetText(result.Text) is null;
            _context.Shell.Announce(new Announcement(
                copied ? "Text in the clipboard" : "Text recognized, clipboard busy",
                lines == 1 ? Shorten(result.Text) : $"{lines} lines")
            {
                Level = copied ? StatusLevel.Good : StatusLevel.Warn,
            });
        }
        catch (Exception ex)
        {
            _context.Log.Error("Quick text recognition failed.", ex);
        }
        finally
        {
            IsCapturing = false;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    /// <summary>From the app's "Pick a color" shortcut: click a pixel, its hex value lands in the clipboard.</summary>
    /// <remarks>The card's thumbnail is the color itself, a small PNG in the data folder, overwritten each time.</remarks>
    public async Task QuickColorAsync()
    {
        if (IsCapturing) return;

        IsCapturing = true;
        ActivityChanged?.Invoke(this, new ModuleActivity(true, "Picking a color…"));
        try
        {
            var (x, y, width, height) = ScreenCapture.VirtualScreen();
            var desktop = ScreenCapture.Screen(x, y, width, height);
            if (await ColorPicker.PickAsync(Monitors.All(), desktop, (x, y)) is not { } color) return;

            var hex = LoupeSurface.Hex(color);
            var copied = ClipboardImage.SetText(hex) is null;

            _context.Shell.Announce(new Announcement(copied ? $"{hex} in the clipboard" : hex, $"RGB {color.R}, {color.G}, {color.B}")
            {
                ImagePath = WriteSwatch(color),
                Level = copied ? StatusLevel.Good : StatusLevel.Warn,
            });
        }
        catch (Exception ex)
        {
            _context.Log.Error("Color picking failed.", ex);
        }
        finally
        {
            IsCapturing = false;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    private string? WriteSwatch(Avalonia.Media.Color color)
    {
        try
        {
            const int side = 64;
            var pixels = new byte[side * side * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 255;
            }

            using var bitmap = Images.ToBitmap(new CapturedImage(pixels, side, side));
            var path = Path.Combine(_context.DataDirectory, "color.png");
            bitmap.Save(path, new PngBitmapEncoderOptions());
            return path;
        }
        catch (Exception)
        {
            // Without the swatch the card still shows the value.
            return null;
        }
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "…";

    [RelayCommand]
    private Task CaptureMonitorAsync()
    {
        if (SelectedMonitor is not { } monitor) return Task.CompletedTask;

        var b = monitor.Bounds;
        return CaptureAsync(
            () => Task.FromResult<CapturedImage?>(ScreenCapture.Screen(b.X, b.Y, b.Width, b.Height)),
            hideHost: true, delay: SelectedDelay);
    }

    /// <summary>A window, even a covered one; WinTo doesn't have to leave the picture.</summary>
    [RelayCommand]
    private Task CaptureWindowAsync()
    {
        if (SelectedWindow is not { } window) return Task.CompletedTask;

        return CaptureAsync(
            () => Task.FromResult<CapturedImage?>(ScreenCapture.Window(window.Handle)),
            hideHost: false, delay: SelectedDelay);
    }

    /// <summary>The shared sequence: hide window, wait, capture, show window, process.</summary>
    /// <remarks>
    /// <para>
    /// <b>Hide, not minimize:</b> the minimize animation can take a quarter second and the capture would show
    /// the shrinking window. <c>Hide</c> is instant; 200 ms are enough for the compositor to draw without it.
    /// </para>
    /// <para>
    /// <b>Restoring is in <c>finally</c>:</b> an error in between would leave the whole app invisible, with no
    /// taskbar entry.
    /// </para>
    /// </remarks>
    private async Task CaptureAsync(Func<Task<CapturedImage?>> take, bool hideHost, int delay, bool announce = false)
    {
        if (IsCapturing) return;
        IsCapturing = true;
        Message = null;

        var window = hideHost && HostWindow is { IsVisible: true } host ? host : null;
        CapturedImage? image = null;

        try
        {
            if (window is not null) window.Hide();

            var wait = TimeSpan.FromSeconds(delay) + (window is not null ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            image = await take();
        }
        catch (Exception ex)
        {
            _context.Log.Error("Capture failed.", ex);
            Show(ex.Message, error: true);
        }
        finally
        {
            if (window is not null)
            {
                window.Show();
                window.Activate();
            }

            IsCapturing = false;
        }

        if (image is not null) await HandleAsync(image, announce);
    }

    private async Task HandleAsync(CapturedImage image, bool announce = false)
    {
        ShowImage(image);

        // Encoded once, for the file and for the clipboard's PNG format.
        var png = await EncodeAsync(image);

        var parts = new List<string>();
        string? saved = null;

        if (AutoSave && png is not null)
        {
            saved = await SaveToAsync(NewFilePath(), png);
            if (saved is not null)
            {
                _lastPath = saved;
                parts.Add($"Saved: {Path.GetFileName(saved)}");
            }
        }

        var copied = CopyToClipboard && CopyImage(image, png);
        if (copied) parts.Add("in the clipboard");

        if (parts.Count > 0) Show(string.Join(" · ", parts) + ".");

        if (announce)
        {
            var size = $"{image.Width} × {image.Height}";
            _context.Shell.Announce(new Announcement(
                copied ? "Screenshot in the clipboard" : saved is not null ? "Screenshot saved" : "Screenshot taken",
                saved is not null ? $"{size} · {Path.GetFileName(Path.GetDirectoryName(saved))}" : size)
            {
                ImagePath = saved,
                OpenModuleId = _context.Manifest.Id,
                OpenArgument = saved,
                Level = copied || saved is not null ? StatusLevel.Good : StatusLevel.Warn,
            });
        }

        RefreshRecent();

        if (AutoRecognize) await RecognizeAsync();
    }

    /// <summary>Shows an image, ready to draw on: every capture opens in the editor.</summary>
    private void ShowImage(CapturedImage image)
    {
        _last = image;

        // Set the new editor first, then release the old one, or the view might draw a discarded image.
        var old = Editor;
        Editor = new ImageEditor(image);
        old?.Dispose();

        HasPreview = true;
        OcrText = "";
        OcrInfo = "";
    }

    /// <summary>What copy, save, pin and text recognition use: the image with every edit, as pixels and PNG.</summary>
    private async Task<(CapturedImage Image, byte[]? Png)?> CurrentAsync()
    {
        if (_last is not { } original) return null;

        if (Editor is { HasChanges: true } editor)
        {
            try
            {
                var png = editor.RenderPng();
                var image = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(png);
                    using var bitmap = new Bitmap(stream);
                    return Images.FromBitmap(bitmap);
                });

                return (image, png);
            }
            catch (Exception ex)
            {
                _context.Log.Error("Rendering the edit failed.", ex);
                Show($"Edit not applied: {ex.Message}", error: true);
                return null;
            }
        }

        return (original, await EncodeAsync(original));
    }

    // --------------------------------------------------------------- Reuse

    [RelayCommand]
    private async Task CopyImageAsync()
    {
        if (await CurrentAsync() is not var (image, png)) return;
        if (CopyImage(image, png)) Show("Image in the clipboard.");
    }

    /// <summary>Onto the clipboard right away, see <see cref="ClipboardImage"/>. UI thread.</summary>
    private bool CopyImage(CapturedImage image, byte[]? png)
    {
        try
        {
            if (ClipboardImage.Set(image, png) is not { } problem) return true;

            _context.Log.Warn($"Clipboard: {problem}");
            Show(problem, error: true);
            return false;
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Clipboard: {ex.Message}");
            return false;
        }
    }

    /// <summary>An image as PNG, off the UI thread.</summary>
    /// <remarks>
    /// Default compression instead of fastest: a screenshot gets shared and archived, and half a second more
    /// doesn't show while capturing.
    /// </remarks>
    private async Task<byte[]?> EncodeAsync(CapturedImage image)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var bitmap = Images.ToBitmap(image);
                using var stream = new MemoryStream();
                bitmap.Save(stream, new PngBitmapEncoderOptions());
                return stream.ToArray();
            });
        }
        catch (Exception ex)
        {
            _context.Log.Error("Encoding failed.", ex);
            return null;
        }
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (_last is null) return;

        var path = await _context.Shell.PickSaveFileAsync("Save screenshot",
            Path.GetFileName(NewFilePath()), new FilePattern("PNG image", "png"));
        if (path is null || await CurrentAsync() is not (_, { } png)) return;

        if (await SaveToAsync(path, png) is { } saved)
        {
            _lastPath = saved;
            Show($"Saved: {Path.GetFileName(saved)}.");
            RefreshRecent();
        }
    }

    private async Task<string?> SaveToAsync(string path, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, png);
            return path;
        }
        catch (Exception ex)
        {
            _context.Log.Error("Saving failed.", ex);
            Show($"Saving failed: {ex.Message}", error: true);
            return null;
        }
    }

    private string NewFilePath() => ScreenshotFolder.NewFile(TargetDirectory);

    [RelayCommand]
    private async Task RecognizeAsync()
    {
        if (IsRecognizing) return;

        IsRecognizing = true;
        OcrInfo = "Recognizing text…";
        ActivityChanged?.Invoke(this, new ModuleActivity(true, "Recognizing text…"));

        try
        {
            // The edited image: a crop recognizes only what is left.
            if (await CurrentAsync() is not var (image, _)) return;

            var result = await WindowsOcr.RecognizeAsync(image.Pixels, image.Width, image.Height, _context.Shutdown);

            OcrText = result.Text;
            var lines = result.Text.Length == 0 ? 0 : result.Text.Split(Environment.NewLine).Length;

            OcrInfo = result.Problem
                ?? (lines == 0 ? "No text recognized."
                    : $"{(lines == 1 ? "1 line" : $"{lines} lines")} · {result.Language ?? "system language"}");
        }
        catch (OperationCanceledException)
        {
            OcrInfo = "";
        }
        catch (Exception ex)
        {
            _context.Log.Error("Text recognition failed.", ex);
            OcrInfo = $"Text recognition failed: {ex.Message}";
        }
        finally
        {
            IsRecognizing = false;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    [RelayCommand]
    private async Task CopyTextAsync()
    {
        if (Clipboard is null || OcrText.Length == 0) return;
        await Clipboard.SetTextAsync(OcrText);
        Show("Text in the clipboard.");
    }

    // --------------------------------------------------------------- Edit, save and pin

    private readonly List<PinWindow> _pins = [];

    /// <summary>The picture with its tools. Every image shown opens here; there is no separate edit mode.</summary>
    [ObservableProperty] public partial ImageEditor? Editor { get; set; }

    /// <summary>Saves the edited image: over its own file for a capture from this tool, otherwise "Save as".</summary>
    /// <remarks>A file opened from elsewhere is never overwritten without a dialog.</remarks>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_lastPath is not { } path || !IsOwnCapture(path))
        {
            await SaveAsAsync();
            return;
        }

        if (await CurrentAsync() is not (_, { } png)) return;

        if (await SaveToAsync(path, png) is { } saved)
        {
            Show($"Saved: {Path.GetFileName(saved)}.");
            RefreshRecent();
        }
    }

    private bool IsOwnCapture(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(TargetDirectory).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(path).StartsWith("Screenshot ", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task PinAsync()
    {
        if (await CurrentAsync() is not var (image, _)) return;

        var pin = PinWindow.Open(image);
        _pins.Add(pin);
        pin.Closed += (_, _) => _pins.Remove(pin);
    }

    [RelayCommand]
    private void RevealLast()
    {
        if (_lastPath is not null && File.Exists(_lastPath)) _context.Shell.RevealInExplorer(_lastPath);
        else OpenFolder();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(TargetDirectory);
        _context.Shell.RevealInExplorer(TargetDirectory);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (await _context.Shell.PickFolderAsync("Target folder for screenshots") is not { } folder) return;

        TargetDirectory = folder;
        Save();
        RefreshRecent();
    }

    /// <summary>A saved image back into the preview, for copying or OCR.</summary>
    [RelayCommand]
    private Task OpenRecentAsync(RecentShot? shot) =>
        shot is null ? Task.CompletedTask : OpenFileAsync(shot.Path);

    /// <summary>From the list or dropped: loads an image without saving it again.</summary>
    public async Task OpenFileAsync(string path)
    {
        ShowOptions = false;

        try
        {
            var image = await Task.Run(() =>
            {
                using var bitmap = new Bitmap(path);
                return Images.FromBitmap(bitmap);
            });

            ShowImage(image);
            _lastPath = path;
            Show($"Loaded: {Path.GetFileName(path)}.");

            if (AutoRecognize) await RecognizeAsync();
        }
        catch (Exception ex)
        {
            Show($"Image could not be loaded: {ex.Message}", error: true);
        }
    }

    private void RefreshRecent()
    {
        Recent.Clear();

        try
        {
            if (Directory.Exists(TargetDirectory))
            {
                foreach (var file in new DirectoryInfo(TargetDirectory)
                             .EnumerateFiles("*.png")
                             .OrderByDescending(f => f.LastWriteTimeUtc)
                             .Take(12))
                {
                    Recent.Add(new RecentShot(file.FullName, file.Name,
                        $"{Humanize.Bytes(file.Length)} · {Humanize.Ago(file.LastWriteTime)}"));
                }
            }
        }
        catch (Exception)
        {
            // An unreadable target folder costs the list, nothing else.
        }

        HasRecent = Recent.Count > 0;
    }

    // --------------------------------------------------------------- Helpers

    private void Save()
    {
        if (_loading) return;

        _context.Settings.Save(new ScreenshotSettings
        {
            TargetDirectory = TargetDirectory,
            AutoSave = AutoSave,
            CopyToClipboard = CopyToClipboard,
            AutoRecognize = AutoRecognize,
            HotkeyEnabled = HotkeyEnabled,
            Hotkey = SelectedHotkey.Id,
            HotkeyTakesRegion = HotkeyTakesRegion,
            DelaySeconds = SelectedDelay,
        });

        if (!_loading && HotkeyEnabled && _hotkey.IsRegistered)
        {
            // The text below the picker also depends on "region or display".
            HotkeyText = HotkeyTakesRegion
                ? $"{SelectedHotkey.Label} captures a region while the module is open."
                : $"{SelectedHotkey.Label} captures the display under the mouse pointer while the module is open.";
        }
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    public void Dispose()
    {
        _hotkey.Pressed -= OnHotkeyPressed;
        _hotkey.Dispose();

        foreach (var pin in _pins.ToList()) pin.Close();

        Editor?.Dispose();
        Editor = null;
        _last = null;
    }
}
