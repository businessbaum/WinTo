using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Modules.Capture.Screenshot;
using SkiaSharp;

namespace MultiTool.Modules.Photos;

/// <summary>What goes into the module's <c>settings.json</c>.</summary>
public sealed class PhotosSettings
{
    public int JpegQuality { get; set; } = 92;
}

/// <summary>A filter in the strip, with a small picture of the photo through it.</summary>
public sealed partial class FilterChoice(string name, PhotoFilter filter) : ObservableObject
{
    public string Name { get; } = name;
    public PhotoFilter Filter { get; } = filter;

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }
}

/// <summary>An aspect ratio for cropping; 0 is free, -1 the picture's own.</summary>
public sealed partial class AspectChoice(string name, double ratio) : ObservableObject
{
    public string Name { get; } = name;
    public double Ratio { get; } = ratio;

    [ObservableProperty] public partial bool IsActive { get; set; }
}

/// <summary>One photo at a time, its folder to browse, and edits replayed on save.</summary>
/// <remarks>
/// <para>
/// <b>Viewing shows the full picture, editing a preview.</b> Opened, the photo is shown at full resolution.
/// Edits run on a copy at most 2560 pixels long, so sliders answer at once; saving decodes the original again
/// and replays every edit on it (<see cref="PhotoStep"/>). Nothing is ever written over the original without
/// asking, and replacing it moves the original to the recycle bin first.
/// </para>
/// <para>
/// <b>Saved pictures carry no EXIF.</b> Skia writes pixels only: no camera, no date, no location. The tooltip
/// on saving says so.
/// </para>
/// </remarks>
public sealed partial class PhotosViewModel : ObservableObject, IDisposable
{
    private const int PreviewSide = 2560;

    private readonly IModuleContext _context;
    private readonly bool _loading;
    private readonly List<PhotoStep> _steps = [];
    private CancellationTokenSource? _load;
    private SKBitmap? _previewBase;
    private SKBitmap? _stepped;
    private int _originalWidth;
    private int _originalHeight;
    private DispatcherTimer? _lookTimer;

    public PhotosViewModel(IModuleContext context)
    {
        _loading = true;
        _context = context;
        JpegQuality = Math.Clamp(context.Settings.Load<PhotosSettings>().JpegQuality, 40, 100);

        Filters =
        [
            new FilterChoice("Original", PhotoFilter.None) { IsActive = true },
            new FilterChoice("Vivid", PhotoFilter.Vivid),
            new FilterChoice("Warm", PhotoFilter.Warm),
            new FilterChoice("Cool", PhotoFilter.Cool),
            new FilterChoice("B&W", PhotoFilter.BlackWhite),
            new FilterChoice("Noir", PhotoFilter.Noir),
            new FilterChoice("Sepia", PhotoFilter.Sepia),
            new FilterChoice("Fade", PhotoFilter.Fade),
        ];

        Aspects =
        [
            new AspectChoice("Free", 0) { IsActive = true },
            new AspectChoice("Original", -1),
            new AspectChoice("Square", 1),
            new AspectChoice("4:3", 4.0 / 3),
            new AspectChoice("3:2", 3.0 / 2),
            new AspectChoice("16:9", 16.0 / 9),
            new AspectChoice("4:5", 4.0 / 5),
            new AspectChoice("9:16", 9.0 / 16),
        ];

        _loading = false;
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    // --------------------------------------------------------------- The photo

    [ObservableProperty] public partial string? Path { get; set; }
    [ObservableProperty] public partial Bitmap? Display { get; set; }
    [ObservableProperty] public partial bool HasPhoto { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "Photos";
    [ObservableProperty] public partial string Counter { get; set; } = "";
    [ObservableProperty] public partial double Zoom { get; set; }

    public ObservableCollection<string> Folder { get; } = [];

    public bool IsEmpty => !HasPhoto && !IsLoading && Problem is null;

    partial void OnHasPhotoChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Cropping));
    }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    public string ZoomText => Zoom <= 0 ? "Fit"
        : $"{Zoom * (Display is { } d && _originalWidth > 0 ? d.PixelSize.Width / (double)_originalWidth : 1) * 100:0} %";

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    partial void OnDisplayChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        OnPropertyChanged(nameof(ZoomText));
        // Released after the canvas has the new one.
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue)) Dispatcher.UIThread.Post(oldValue.Dispose, DispatcherPriority.Background);
    }

    // --------------------------------------------------------------- Info

    [ObservableProperty] public partial bool ShowInfo { get; set; }
    [ObservableProperty] public partial string InfoName { get; set; } = "";
    [ObservableProperty] public partial string InfoFolder { get; set; } = "";
    [ObservableProperty] public partial string InfoSize { get; set; } = "";
    [ObservableProperty] public partial string InfoFile { get; set; } = "";
    [ObservableProperty] public partial string InfoDates { get; set; } = "";
    [ObservableProperty] public partial string? InfoCamera { get; set; }
    [ObservableProperty] public partial string? InfoExposure { get; set; }
    [ObservableProperty] public partial bool InfoHasLocation { get; set; }

    [RelayCommand]
    private void ToggleInfo()
    {
        ShowInfo = !ShowInfo;
    }

    // --------------------------------------------------------------- Opening and browsing

    public async Task OpenAsync(string path)
    {
        if (!await ConfirmDiscardAsync()) return;

        Path = System.IO.Path.GetFullPath(path);
        ListFolder();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var files = await _context.Shell.PickFilesAsync("Open a picture", multiple: false,
            new FilePattern("Pictures", PhotoDecoder.Extensions));
        if (files.Count > 0) await OpenAsync(files[0]);
    }

    [RelayCommand]
    private Task NextAsync() => StepAsync(1);

    [RelayCommand]
    private Task PreviousAsync() => StepAsync(-1);

    private async Task StepAsync(int delta)
    {
        if (Path is null || Folder.Count < 2) return;
        if (!await ConfirmDiscardAsync()) return;

        var index = IndexOf(Path);
        Path = Folder[((index < 0 ? 0 : index) + delta + Folder.Count) % Folder.Count];
        await LoadAsync();
    }

    private int IndexOf(string path)
    {
        for (var i = 0; i < Folder.Count; i++)
            if (string.Equals(Folder[i], path, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }

    /// <summary>The pictures next to this one, in Explorer's order ("2.jpg" before "10.jpg").</summary>
    private void ListFolder()
    {
        Folder.Clear();
        if (Path is null || System.IO.Path.GetDirectoryName(Path) is not { } folder) return;

        try
        {
            var files = Directory.EnumerateFiles(folder).Where(PhotoDecoder.IsSupported).ToList();
            files.Sort((a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
            foreach (var file in files) Folder.Add(file);
        }
        catch (Exception)
        {
            // An unreadable folder costs the browsing, not the picture.
        }

        if (IndexOf(Path) < 0) Folder.Insert(0, Path);
    }

    private async Task LoadAsync()
    {
        if (Path is not { } path) return;

        _load?.Cancel();
        _load?.Dispose();
        var load = _load = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);

        ResetEdits();
        IsLoading = true;
        Problem = null;
        Title = System.IO.Path.GetFileName(path);
        Counter = Folder.Count > 1 ? $"{IndexOf(path) + 1} / {Folder.Count}" : "";

        var ffmpeg = _context.Tools.Find("ffmpeg");
        try
        {
            var (photo, problem) = await Task.Run(() => PhotoDecoder.Decode(path, ffmpeg), load.Token);
            if (load.IsCancellationRequested)
            {
                photo?.Bitmap.Dispose();
                return;
            }

            if (photo is null)
            {
                HasPhoto = false;
                Display = null;
                Problem = problem;
                return;
            }

            var (display, preview) = await Task.Run(() =>
            {
                var shown = PhotoBitmaps.ToAvalonia(photo.Bitmap);
                var small = PhotoBitmaps.Shrink(photo.Bitmap, PreviewSide);
                return (shown, small);
            }, load.Token);

            if (load.IsCancellationRequested)
            {
                display.Dispose();
                preview.Dispose();
                photo.Bitmap.Dispose();
                return;
            }

            _originalWidth = photo.Bitmap.Width;
            _originalHeight = photo.Bitmap.Height;
            _previewBase?.Dispose();
            _previewBase = preview;
            photo.Bitmap.Dispose();

            Zoom = 0;
            Display = display;
            HasPhoto = true;
            FillInfo(path, photo);
            ResetSize();
            _ = MakeFilterThumbnailsAsync();

            // Still on an editing tab: the new photo becomes the preview right away.
            if (IsEditing) Refresh();
            if (IsMarkup) StartMarkup();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _context.Log.Error($"Loading {path} failed.", ex);
            Problem = $"{System.IO.Path.GetFileName(path)} could not be read: {ex.Message}";
            HasPhoto = false;
        }
        finally
        {
            if (_load == load) IsLoading = false;
        }
    }

    private void FillInfo(string path, DecodedPhoto photo)
    {
        var file = new FileInfo(path);
        InfoName = file.Name;
        InfoFolder = file.DirectoryName ?? "";
        InfoSize = $"{_originalWidth} × {_originalHeight} · {_originalWidth * (long)_originalHeight / 1_000_000.0:0.#} MP";
        InfoFile = $"{file.Extension.TrimStart('.').ToUpperInvariant()} · {Humanize.Bytes(file.Length)} · read by {photo.Decoder}";
        InfoDates = photo.Info.Taken is { } taken
            ? $"Taken {taken:g} · modified {file.LastWriteTime:g}"
            : $"Modified {file.LastWriteTime:g}";
        InfoCamera = photo.Info.Camera;
        InfoExposure = photo.Info.Exposure;
        InfoHasLocation = photo.Info.HasLocation;
    }

    // --------------------------------------------------------------- Editing

    /// <summary>The ribbon tab, always visible like in Word: 0 home, 1 adjust, 2 crop and rotate, 3 markup, 4 size.</summary>
    [ObservableProperty] public partial int Section { get; set; }

    /// <summary>A tab other than Home is open: the photo shows as the quick preview that edits work on.</summary>
    /// <remarks>Follows <see cref="Section"/>; Home shows the full picture again once nothing is edited.</remarks>
    [ObservableProperty] public partial bool IsEditing { get; private set; }

    [ObservableProperty] public partial bool HasEdits { get; set; }
    [ObservableProperty] public partial bool CanUndo { get; set; }

    public bool IsHome => Section == 0;
    public bool IsAdjust => Section == 1;
    public bool IsCrop => Section == 2;
    public bool IsMarkup => Section == 3;
    public bool IsSize => Section == 4;

    /// <summary>The crop frame shows only while the crop tab is open.</summary>
    public bool Cropping => IsCrop && HasPhoto;

    /// <summary>The markup surface replaces the photo while the markup tab is open.</summary>
    public bool ShowMarkup => IsMarkup && Markup is not null;

    public bool ShowCanvas => !ShowMarkup;

    /// <summary>E: between Home and Adjust.</summary>
    [RelayCommand]
    private void ToggleEdit() => Section = Section == 0 ? 1 : 0;

    [RelayCommand]
    private void ShowSection(string index)
    {
        if (int.TryParse(index, out var value)) Section = value;
    }

    partial void OnSectionChanged(int oldValue, int newValue)
    {
        // Leaving markup keeps what was drawn as a step.
        if (oldValue == 3 && newValue != 3) FinishMarkup();

        IsEditing = newValue != 0;
        if (newValue == 3) StartMarkup();
        NotifySections();
    }

    partial void OnIsEditingChanged(bool value) => Refresh();

    private void NotifySections()
    {
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsAdjust));
        OnPropertyChanged(nameof(IsCrop));
        OnPropertyChanged(nameof(IsMarkup));
        OnPropertyChanged(nameof(IsSize));
        OnPropertyChanged(nameof(Cropping));
        OnPropertyChanged(nameof(ShowMarkup));
        OnPropertyChanged(nameof(ShowCanvas));
    }

    private void AddStep(PhotoStep step)
    {
        _steps.Add(step);
        _stepped?.Dispose();
        _stepped = null;
        Refresh();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_steps.Count > 0)
        {
            _steps.RemoveAt(_steps.Count - 1);
            _stepped?.Dispose();
            _stepped = null;
        }
        else if (!Look.IsNeutral)
        {
            SetLook(PhotoLook.Neutral);
        }

        Refresh();
    }

    [RelayCommand]
    private void ResetAll()
    {
        ResetEdits();
        Refresh();
    }

    private void ResetEdits()
    {
        _steps.Clear();
        _stepped?.Dispose();
        _stepped = null;
        Markup?.Dispose();
        Markup = null;
        SetLook(PhotoLook.Neutral);
        HasEdits = false;
        CanUndo = false;
    }

    /// <summary>Redraws the preview with every edit. Steps are cached; the look is applied on top.</summary>
    private void Refresh()
    {
        HasEdits = _steps.Count > 0 || !Look.IsNeutral;
        CanUndo = HasEdits;
        if (_previewBase is null) return;

        if (!HasEdits && !IsEditing)
        {
            // Nothing changed: back to the full picture, sharp at 100 %.
            if (Path is not null && Display is { } current && current.PixelSize.Width != _originalWidth) _ = LoadAsync();
            return;
        }

        try
        {
            if (_stepped is null)
            {
                var bitmap = _previewBase.Copy();
                foreach (var step in _steps) bitmap = step.Apply(bitmap);
                _stepped = bitmap;
            }

            using var looked = Look.Apply(_stepped.Copy());
            Display = PhotoBitmaps.ToAvalonia(looked);
            NotifySize();
        }
        catch (Exception ex)
        {
            _context.Log.Error("Preview failed.", ex);
            _context.Shell.Notify($"Edit not shown: {ex.Message}", StatusLevel.Warn);
        }
    }

    // --------------------------------------------------------------- Adjust

    public ObservableCollection<FilterChoice> Filters { get; }

    [ObservableProperty] public partial int Brightness { get; set; }
    [ObservableProperty] public partial int Contrast { get; set; }
    [ObservableProperty] public partial int Saturation { get; set; }
    [ObservableProperty] public partial int Warmth { get; set; }

    private PhotoFilter _filter;
    private bool _settingLook;

    private PhotoLook Look => new(Brightness, Contrast, Saturation, Warmth, _filter);

    partial void OnBrightnessChanged(int value) => LookChanged();
    partial void OnContrastChanged(int value) => LookChanged();
    partial void OnSaturationChanged(int value) => LookChanged();
    partial void OnWarmthChanged(int value) => LookChanged();

    /// <summary>Slider steps are gathered for 40 ms: drawing the preview takes about that long.</summary>
    private void LookChanged()
    {
        if (_loading || _settingLook) return;

        _lookTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
        {
            _lookTimer!.Stop();
            Refresh();
        });

        _lookTimer.Stop();
        _lookTimer.Start();
    }

    private void SetLook(PhotoLook look)
    {
        _settingLook = true;
        Brightness = look.Brightness;
        Contrast = look.Contrast;
        Saturation = look.Saturation;
        Warmth = look.Warmth;
        _filter = look.Filter;
        foreach (var choice in Filters) choice.IsActive = choice.Filter == look.Filter;
        _settingLook = false;
    }

    [RelayCommand]
    private void ChooseFilter(FilterChoice choice)
    {
        _filter = choice.Filter;
        foreach (var item in Filters) item.IsActive = item == choice;
        Refresh();
    }

    [RelayCommand]
    private void ResetLook()
    {
        SetLook(PhotoLook.Neutral);
        Refresh();
    }

    /// <summary>Each filter's look on a 96-pixel copy of the photo.</summary>
    private async Task MakeFilterThumbnailsAsync()
    {
        if (_previewBase is not { } source) return;

        using var small = PhotoBitmaps.Shrink(source, 96);
        foreach (var choice in Filters)
        {
            var filter = choice.Filter;
            var bitmap = await Task.Run(() =>
            {
                using var looked = (PhotoLook.Neutral with { Filter = filter }).Apply(small.Copy());
                return PhotoBitmaps.ToAvalonia(looked);
            });

            var old = choice.Thumbnail;
            choice.Thumbnail = bitmap;
            old?.Dispose();
        }
    }

    // --------------------------------------------------------------- Crop and rotate

    public ObservableCollection<AspectChoice> Aspects { get; }

    [ObservableProperty] public partial double CropAspect { get; set; }
    [ObservableProperty] public partial Rect CropRect { get; set; }

    [RelayCommand]
    private void ChooseAspect(AspectChoice choice)
    {
        foreach (var item in Aspects) item.IsActive = item == choice;
        CropAspect = choice.Ratio < 0 && Display is { } d ? d.PixelSize.Width / (double)d.PixelSize.Height : choice.Ratio;
    }

    [RelayCommand]
    private void ApplyCrop()
    {
        if (Display is not { } display || CropRect.Width < 2 || CropRect.Height < 2) return;

        var w = (double)display.PixelSize.Width;
        var h = (double)display.PixelSize.Height;
        if (CropRect.Width >= w - 1 && CropRect.Height >= h - 1) return;

        AddStep(new CropStep(new SKRect((float)(CropRect.X / w), (float)(CropRect.Y / h), (float)(CropRect.Right / w), (float)(CropRect.Bottom / h))));

        // A fresh frame for the next crop.
        OnPropertyChanged(nameof(Cropping));
    }

    [RelayCommand]
    private void RotateLeft() => AddStep(new RotateStep(270));

    [RelayCommand]
    private void RotateRight() => AddStep(new RotateStep(90));

    [RelayCommand]
    private void FlipHorizontal() => AddStep(new FlipStep(true));

    [RelayCommand]
    private void FlipVertical() => AddStep(new FlipStep(false));

    // --------------------------------------------------------------- Markup

    /// <summary>The drawing tools of the Capture tool, on the current preview.</summary>
    [ObservableProperty] public partial ImageEditor? Markup { get; set; }

    partial void OnMarkupChanged(ImageEditor? value)
    {
        OnPropertyChanged(nameof(ShowMarkup));
        OnPropertyChanged(nameof(ShowCanvas));
    }

    private void StartMarkup()
    {
        if (Markup is not null || _previewBase is null) return;

        try
        {
            var bitmap = _stepped?.Copy() ?? _previewBase.Copy();
            using var looked = Look.Apply(bitmap);
            Markup = new ImageEditor(MarkupStep.ToCaptured(looked));
            Markup.Tool = EditTool.Pen;
        }
        catch (Exception ex)
        {
            _context.Log.Error("Markup could not start.", ex);
        }
    }

    /// <summary>Keeps what was drawn as a step; nothing drawn, nothing kept.</summary>
    private void FinishMarkup()
    {
        if (Markup is not { } editor) return;

        Markup = null;
        var steps = editor.Steps.Where(s => s.Tool is not EditTool.Crop).ToList();
        var width = (int)editor.View.Width;
        editor.Dispose();

        if (steps.Count > 0) AddStep(new MarkupStep(steps, Math.Max(1, _stepped?.Width ?? _previewBase?.Width ?? width)));
    }

    [RelayCommand]
    private void ApplyMarkup()
    {
        FinishMarkup();
        StartMarkup();
    }

    // --------------------------------------------------------------- Size and quality

    [ObservableProperty] public partial int ResizeWidth { get; set; }
    [ObservableProperty] public partial int ResizeHeight { get; set; }
    [ObservableProperty] public partial bool KeepRatio { get; set; } = true;
    [ObservableProperty] public partial int JpegQuality { get; set; } = 92;

    private bool _sizing;

    /// <summary>Width × height the saved picture gets: after crop and rotation, before resizing.</summary>
    public string ResultSize
    {
        get
        {
            var (w, h) = SteppedSize();
            return ResizeWidth > 0 && ResizeHeight > 0 && (ResizeWidth != w || ResizeHeight != h)
                ? $"{w} × {h} → {ResizeWidth} × {ResizeHeight}"
                : $"{w} × {h}";
        }
    }

    /// <summary>The width box: empty keeps the size, which the placeholder shows.</summary>
    public decimal? WidthBox
    {
        get => ResizeWidth > 0 ? ResizeWidth : null;
        set => ResizeWidth = value is > 0 ? (int)value.Value : 0;
    }

    public decimal? HeightBox
    {
        get => ResizeHeight > 0 ? ResizeHeight : null;
        set => ResizeHeight = value is > 0 ? (int)value.Value : 0;
    }

    public string WidthPlaceholder => SteppedSize().Width is > 0 and var w ? w.ToString(CultureInfo.InvariantCulture) : "";

    public string HeightPlaceholder => SteppedSize().Height is > 0 and var h ? h.ToString(CultureInfo.InvariantCulture) : "";

    private void NotifySize()
    {
        OnPropertyChanged(nameof(ResultSize));
        OnPropertyChanged(nameof(WidthBox));
        OnPropertyChanged(nameof(HeightBox));
        OnPropertyChanged(nameof(WidthPlaceholder));
        OnPropertyChanged(nameof(HeightPlaceholder));
    }

    /// <summary>The full-resolution size after the steps, from the preview's proportions.</summary>
    private (int Width, int Height) SteppedSize()
    {
        if (_previewBase is null || _originalWidth == 0) return (0, 0);

        var scale = _originalWidth / (double)_previewBase.Width;
        var current = _stepped ?? _previewBase;
        return ((int)Math.Round(current.Width * scale), (int)Math.Round(current.Height * scale));
    }

    private void ResetSize()
    {
        _sizing = true;
        ResizeWidth = 0;
        ResizeHeight = 0;
        _sizing = false;
        NotifySize();
    }

    partial void OnResizeWidthChanged(int value)
    {
        if (_sizing || !KeepRatio) { NotifySize(); return; }

        var (w, h) = SteppedSize();
        _sizing = true;
        ResizeHeight = w > 0 && value > 0 ? (int)Math.Round(value * (double)h / w) : 0;
        _sizing = false;
        NotifySize();
    }

    partial void OnResizeHeightChanged(int value)
    {
        if (_sizing || !KeepRatio) { NotifySize(); return; }

        var (w, h) = SteppedSize();
        _sizing = true;
        ResizeWidth = h > 0 && value > 0 ? (int)Math.Round(value * (double)w / h) : 0;
        _sizing = false;
        NotifySize();
    }

    partial void OnJpegQualityChanged(int value)
    {
        if (_loading) return;
        _context.Settings.Save(new PhotosSettings { JpegQuality = value });
    }

    /// <summary>"50", "25" in percent, or "w1920" for a width.</summary>
    [RelayCommand]
    private void ResizePreset(string preset)
    {
        var (w, h) = SteppedSize();
        if (w == 0) return;

        _sizing = true;
        if (preset.StartsWith('w') && int.TryParse(preset[1..], out var width))
        {
            ResizeWidth = Math.Min(width, w);
            ResizeHeight = (int)Math.Round(ResizeWidth * (double)h / w);
        }
        else if (int.TryParse(preset, out var percent))
        {
            ResizeWidth = Math.Max(1, w * percent / 100);
            ResizeHeight = Math.Max(1, h * percent / 100);
        }
        _sizing = false;
        NotifySize();
    }

    // --------------------------------------------------------------- Rendering for real

    /// <summary>The original decoded again with every edit, at full resolution.</summary>
    private async Task<SKBitmap?> RenderFullAsync()
    {
        if (Path is not { } path) return null;

        var ffmpeg = _context.Tools.Find("ffmpeg");
        var (photo, problem) = await Task.Run(() => PhotoDecoder.Decode(path, ffmpeg));
        if (photo is null)
        {
            _context.Shell.Notify(problem ?? "The picture could not be read again.", StatusLevel.Bad);
            return null;
        }

        var bitmap = photo.Bitmap;
        foreach (var step in _steps)
        {
            var input = bitmap;
            bitmap = step.NeedsUiThread
                ? await Dispatcher.UIThread.InvokeAsync(() => step.Apply(input))
                : await Task.Run(() => step.Apply(input));
        }

        var look = Look;
        var (width, height) = (ResizeWidth, ResizeHeight);
        return await Task.Run(() =>
        {
            var result = look.Apply(bitmap);
            return width > 0 && height > 0 ? PhotoBitmaps.Resize(result, width, height) : result;
        });
    }

    private static byte[] Encode(SKBitmap bitmap, string extension, int quality)
    {
        var format = extension.ToLowerInvariant() switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg,
        };

        // JPEG has no transparency: transparent parts become white, not black.
        using var flattened = format == SKEncodedImageFormat.Jpeg && bitmap.AlphaType != SKAlphaType.Opaque ? Flatten(bitmap) : null;
        using var image = SKImage.FromBitmap(flattened ?? bitmap);
        using var data = image.Encode(format, format == SKEncodedImageFormat.Png ? 100 : quality)
            ?? throw new InvalidOperationException($"{extension} could not be written.");
        return data.ToArray();
    }

    private static SKBitmap Flatten(SKBitmap bitmap)
    {
        var result = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(bitmap, 0, 0);
        return result;
    }

    /// <summary>Formats Skia writes; anything else is saved as JPEG.</summary>
    private static string WritableExtension(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp"
            ? System.IO.Path.GetExtension(path)
            : ".jpg";

    private async Task<bool> WriteAsync(string target)
    {
        Busy("Saving…");
        try
        {
            using var bitmap = await RenderFullAsync();
            if (bitmap is null) return false;

            var quality = JpegQuality;
            var bytes = await Task.Run(() => Encode(bitmap, System.IO.Path.GetExtension(target), quality));
            await File.WriteAllBytesAsync(target, bytes);
            return true;
        }
        catch (Exception ex)
        {
            _context.Log.Error($"Saving {target} failed.", ex);
            _context.Shell.Notify($"Not saved: {ex.Message}", StatusLevel.Bad);
            return false;
        }
        finally
        {
            Idle();
        }
    }

    /// <summary>"photo (edited).jpg" next to the original; the original stays.</summary>
    [RelayCommand]
    private async Task SaveCopyAsync()
    {
        if (Path is not { } path) return;

        var folder = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = WritableExtension(path);

        var target = System.IO.Path.Combine(folder, $"{name} (edited){extension}");
        for (var n = 2; File.Exists(target); n++) target = System.IO.Path.Combine(folder, $"{name} (edited {n}){extension}");

        if (!await WriteAsync(target)) return;

        _context.Shell.Notify($"Saved: {System.IO.Path.GetFileName(target)}.", StatusLevel.Good);
        ListFolder();
        Counter = Folder.Count > 1 ? $"{IndexOf(path) + 1} / {Folder.Count}" : "";
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (Path is not { } path) return;

        var suggested = System.IO.Path.GetFileNameWithoutExtension(path) + WritableExtension(path);
        var target = await _context.Shell.PickSaveFileAsync("Save picture as", suggested,
            new FilePattern("JPEG", "jpg"), new FilePattern("PNG", "png"), new FilePattern("WebP", "webp"));
        if (target is null) return;

        if (string.Equals(System.IO.Path.GetFullPath(target), path, StringComparison.OrdinalIgnoreCase))
        {
            await ReplaceOriginalAsync();
            return;
        }

        if (await WriteAsync(target)) _context.Shell.Notify($"Saved: {System.IO.Path.GetFileName(target)}.", StatusLevel.Good);
    }

    /// <summary>The edited picture takes the original's place; the original goes to the recycle bin.</summary>
    [RelayCommand]
    private async Task ReplaceOriginalAsync()
    {
        if (Path is not { } path || !HasEdits && ResizeWidth == 0) return;

        var extension = WritableExtension(path);
        var target = System.IO.Path.ChangeExtension(path, extension);
        var note = string.Equals(target, path, StringComparison.OrdinalIgnoreCase)
            ? "The original goes to the recycle bin."
            : $"The original goes to the recycle bin; the edit is saved as {System.IO.Path.GetFileName(target)}.";

        if (!await _context.Shell.ConfirmAsync("Replace the original", note, "Replace")) return;

        // Rendered from the original before it moves.
        Busy("Saving…");
        byte[] bytes;
        try
        {
            using var bitmap = await RenderFullAsync();
            if (bitmap is null) return;
            var quality = JpegQuality;
            bytes = await Task.Run(() => Encode(bitmap, extension, quality));
        }
        catch (Exception ex)
        {
            _context.Shell.Notify($"Not saved: {ex.Message}", StatusLevel.Bad);
            return;
        }
        finally
        {
            Idle();
        }

        if (RecycleBin.Delete([path]) is { } problem)
        {
            _context.Shell.Notify($"Not replaced: {problem}", StatusLevel.Bad);
            return;
        }

        await File.WriteAllBytesAsync(target, bytes);
        _context.Shell.Notify($"Replaced: {System.IO.Path.GetFileName(target)}. The original is in the recycle bin.", StatusLevel.Good);

        ResetEdits();
        Path = target;
        ListFolder();
        await LoadAsync();
    }

    // --------------------------------------------------------------- Other actions

    [RelayCommand]
    private async Task CopyAsync()
    {
        Busy("Copying…");
        try
        {
            using var bitmap = await RenderFullAsync();
            if (bitmap is null) return;

            var (image, png) = await Task.Run(() => (MarkupStep.ToCaptured(bitmap), Encode(bitmap, ".png", 100)));
            var problem = ClipboardImage.Set(image, png);
            _context.Shell.Notify(problem ?? "Picture in the clipboard.", problem is null ? StatusLevel.Good : StatusLevel.Warn);
        }
        finally
        {
            Idle();
        }
    }

    [RelayCommand]
    private async Task SetWallpaperAsync()
    {
        var target = System.IO.Path.Combine(_context.DataDirectory, "wallpaper.jpg");
        Directory.CreateDirectory(_context.DataDirectory);
        if (!await WriteAsync(target)) return;

        var ok = SystemParametersInfoW(0x0014 /* SPI_SETDESKWALLPAPER */, 0, target, 0x01 | 0x02);
        _context.Shell.Notify(ok ? "Set as wallpaper." : "Windows refused the wallpaper.", ok ? StatusLevel.Good : StatusLevel.Warn);
    }

    [RelayCommand]
    private void Reveal()
    {
        if (Path is { } path) _context.Shell.RevealInExplorer(path);
    }

    /// <summary>To the recycle bin, then the next picture. Undoable from the recycle bin.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Path is not { } path) return;

        if (RecycleBin.Delete([path]) is { } problem)
        {
            _context.Shell.Notify($"Not deleted: {problem}", StatusLevel.Bad);
            return;
        }

        _context.Shell.Notify($"Moved to the recycle bin: {System.IO.Path.GetFileName(path)}.");

        var index = IndexOf(path);
        Folder.Remove(Folder.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) ?? path);
        ResetEdits();

        if (Folder.Count == 0)
        {
            Path = null;
            HasPhoto = false;
            Display = null;
            Title = "Photos";
            Counter = "";
            return;
        }

        Path = Folder[Math.Clamp(index, 0, Folder.Count - 1)];
        await LoadAsync();
    }

    [RelayCommand]
    private void ZoomFit() => Zoom = 0;

    [RelayCommand]
    private void ZoomActual() => Zoom = Display is { } d && d.PixelSize.Width > 0 ? _originalWidth / (double)d.PixelSize.Width : 1;

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(16, (Zoom <= 0 ? 0.5 : Zoom) * 1.25);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(0.02, (Zoom <= 0 ? 0.5 : Zoom) / 1.25);

    // --------------------------------------------------------------- Helpers

    /// <summary>Edits that would be lost: asks before another picture replaces this one.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasEdits && Markup is not { HasChanges: true }) return true;
        return await _context.Shell.ConfirmAsync("Discard edits", $"The edits to {Title} are not saved.", "Discard", danger: true);
    }

    public bool HasUnsavedWork => HasEdits || Markup is { HasChanges: true };

    private void Busy(string text) => ActivityChanged?.Invoke(this, new ModuleActivity(true, text));

    private void Idle() => ActivityChanged?.Invoke(this, ModuleActivity.Idle);

    public void Dispose()
    {
        _load?.Cancel();
        _load?.Dispose();
        _lookTimer?.Stop();
        Markup?.Dispose();
        _previewBase?.Dispose();
        _stepped?.Dispose();
        Display?.Dispose();
        foreach (var choice in Filters) choice.Thumbnail?.Dispose();
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, string value, uint winIni);
}
