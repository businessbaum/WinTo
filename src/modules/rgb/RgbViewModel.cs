using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Modules.Rgb.Sources;
using MultiTool.UI;

namespace MultiTool.Modules.Rgb;

/// <summary>The RGB page: which devices, from which sources, show what.</summary>
/// <remarks>
/// <para>
/// <b>All devices or one</b>: the editor works on <see cref="RgbSettings.All"/> while nothing is selected, and on a
/// device's own lighting once one is; changing a device sets it apart until "Follow all devices".
/// </para>
/// <para>
/// Sources are asked in parallel when the module opens and on refresh; OpenRGB also says when it found more
/// devices, which asks it again a moment later.
/// </para>
/// </remarks>
public sealed partial class RgbViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private readonly RgbSettings _settings;
    private readonly RgbEngine _engine;
    private readonly List<IRgbSource> _sources;
    private readonly Dictionary<IRgbSource, List<RgbDevice>> _found = [];
    private readonly Dictionary<IRgbSource, DispatcherTimer> _again = [];
    private readonly CancellationTokenSource _life = new();
    private bool _loading = true;
    private bool _disposed;

    internal RgbViewModel(IModuleContext context)
    {
        _context = context;
        _settings = context.Settings.Load<RgbSettings>();
        _engine = new RgbEngine(context.Log);
        _engine.Failed += (device, reason) => Dispatcher.UIThread.Post(() => DeviceFailed(device, reason));

        _sources =
        [
            new DynamicLightingSource(),
            new OpenRgbSource(context),
            new SteelSeriesSource(),
            new RazerSource(),
        ];

        foreach (var source in _sources)
        {
            Sources.Add(new SourceItem(source.Name));
            source.Changed += () => Dispatcher.UIThread.Post(() => DiscoverSoon(source));
        }

        Swatches = [.. Presets.Select(p => new Swatch(p.Name, p.Hex))];
        LoadEditor();
        _loading = false;

        _ = RefreshAsync();
    }

    private static readonly (string Name, string Hex)[] Presets =
    [
        ("Red", "#FF1E1E"), ("Orange", "#FF6A00"), ("Amber", "#FFB000"), ("Yellow", "#FFE600"),
        ("Lime", "#8CFF1E"), ("Green", "#00E05A"), ("Cyan", "#00E1FF"), ("Blue", "#1E5BFF"),
        ("Violet", "#7C5CFF"), ("Purple", "#B01EFF"), ("Magenta", "#FF1ED2"), ("Pink", "#FF6FA8"),
        ("White", "#FFFFFF"), ("Warm white", "#FFC98A"),
    ];

    public ObservableCollection<SourceItem> Sources { get; } = [];

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    public IReadOnlyList<Swatch> Swatches { get; }

    [ObservableProperty]
    public partial bool Searching { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public bool HasDevices => Devices.Count > 0;

    /// <summary>The list's empty state, once every source answered.</summary>
    public bool ShowEmpty => !Searching && Devices.Count == 0;

    partial void OnSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    // --------------------------------------------------------------- What is edited

    /// <summary><c>null</c>: all devices.</summary>
    [ObservableProperty]
    public partial DeviceItem? Selected { get; set; }

    public bool IsAll => Selected is null;

    public string TargetTitle => Selected?.Name ?? "All devices";

    public string TargetDetail => Selected is { } device
        ? device.HasOwn ? $"{device.Detail} · its own lighting" : $"{device.Detail} · follows all devices"
        : Devices.Count switch
        {
            0 => "No device found yet. The sources below say why.",
            _ => $"{Devices.Count(d => d.IsIncluded)} of {Devices.Count} devices controlled",
        };

    public bool CanFollowAll => Selected?.HasOwn == true;

    [ObservableProperty]
    public partial int EffectIndex { get; set; }

    /// <summary>0 to 360.</summary>
    [ObservableProperty]
    public partial double Hue { get; set; }

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    public partial double Saturation { get; set; }

    [ObservableProperty]
    public partial double Brightness { get; set; }

    [ObservableProperty]
    public partial double Speed { get; set; }

    [ObservableProperty]
    public partial string Hex { get; set; } = "";

    /// <summary>A fresh object on every change: the preview redraws when its value changes.</summary>
    [ObservableProperty]
    public partial Lighting Preview { get; set; } = new();

    public bool IsStatic => EffectIndex == (int)RgbEffect.Static;
    public bool IsBreathing => EffectIndex == (int)RgbEffect.Breathing;
    public bool IsSpectrum => EffectIndex == (int)RgbEffect.Spectrum;
    public bool IsWave => EffectIndex == (int)RgbEffect.Wave;
    public bool IsOff => EffectIndex == (int)RgbEffect.Off;

    /// <summary>Static and Breathing take the color; Spectrum and Wave only its saturation.</summary>
    public bool ShowsColor => EffectIndex is (int)RgbEffect.Static or (int)RgbEffect.Breathing;

    public bool ShowsSaturation => EffectIndex is not (int)RgbEffect.Off;

    public bool ShowsSpeed => EffectIndex is (int)RgbEffect.Breathing or (int)RgbEffect.Spectrum or (int)RgbEffect.Wave;

    public bool ShowsBrightness => EffectIndex is not (int)RgbEffect.Off;

    public IBrush ColorBrush => new SolidColorBrush(ToAvalonia(Current)).ToImmutable();

    /// <summary>The saturation track: white to the full hue.</summary>
    public IBrush SaturationTrack => Gradient(Colors.White, ToAvalonia(RgbColor.FromHsv(Hue, 1, 1)));

    /// <summary>The brightness track: black to the color.</summary>
    public IBrush BrightnessTrack => Gradient(Colors.Black, ToAvalonia(RgbColor.FromHsv(Hue, Saturation / 100, 1)));

    public static IBrush HueTrack { get; } = new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(255, 0, 0), 0),
            new GradientStop(Color.FromRgb(255, 255, 0), 1 / 6d),
            new GradientStop(Color.FromRgb(0, 255, 0), 2 / 6d),
            new GradientStop(Color.FromRgb(0, 255, 255), 3 / 6d),
            new GradientStop(Color.FromRgb(0, 0, 255), 4 / 6d),
            new GradientStop(Color.FromRgb(255, 0, 255), 5 / 6d),
            new GradientStop(Color.FromRgb(255, 0, 0), 1),
        },
    }.ToImmutable();

    private RgbColor Current => RgbColor.FromHsv(Hue, Saturation / 100, 1);

    /// <summary>The lighting being edited: a device's own, or the one for all.</summary>
    private Lighting Edited => Selected is { } device && _settings.Devices.TryGetValue(device.Key, out var own) ? own : _settings.All;

    partial void OnSelectedChanged(DeviceItem? oldValue, DeviceItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;

        LoadEditor();
        NotifyTarget();
    }

    private void NotifyTarget()
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(TargetTitle));
        OnPropertyChanged(nameof(TargetDetail));
        OnPropertyChanged(nameof(CanFollowAll));
    }

    /// <summary>Puts the edited lighting into the controls without writing anything back.</summary>
    private void LoadEditor()
    {
        var was = _loading;
        _loading = true;

        var lighting = Edited;
        var (hue, saturation, _) = RgbColor.Parse(lighting.Color, new RgbColor(124, 92, 255)).ToHsv();
        EffectIndex = (int)lighting.Effect;
        Hue = hue;
        Saturation = saturation * 100;
        Brightness = lighting.Brightness;
        Speed = lighting.Speed;
        Hex = RgbColor.FromHsv(hue, saturation, 1).Hex;
        Preview = lighting.Clone();
        NotifyEditor();

        _loading = was;
    }

    partial void OnEffectIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsStatic));
        OnPropertyChanged(nameof(IsBreathing));
        OnPropertyChanged(nameof(IsSpectrum));
        OnPropertyChanged(nameof(IsWave));
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(ShowsColor));
        OnPropertyChanged(nameof(ShowsSaturation));
        OnPropertyChanged(nameof(ShowsSpeed));
        OnPropertyChanged(nameof(ShowsBrightness));
        Store();
    }

    partial void OnHueChanged(double value) => ColorChanged();

    partial void OnSaturationChanged(double value) => ColorChanged();

    partial void OnBrightnessChanged(double value) => Store();

    partial void OnSpeedChanged(double value) => Store();

    partial void OnHexChanged(string value)
    {
        if (_loading || !RgbColor.TryParse(value, out var color)) return;

        // Typed in: the sliders follow, without writing the hex box back while it is being typed in.
        _loading = true;
        var (hue, saturation, _) = color.ToHsv();
        Hue = hue;
        Saturation = saturation * 100;
        _loading = false;
        NotifyEditor();
        Store();
    }

    private void ColorChanged()
    {
        if (_loading) return;

        _loading = true;
        Hex = Current.Hex;
        _loading = false;
        NotifyEditor();
        Store();
    }

    private void NotifyEditor()
    {
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(SaturationTrack));
        OnPropertyChanged(nameof(BrightnessTrack));
    }

    /// <summary>Writes the controls into the edited lighting and sends it to the devices.</summary>
    private void Store()
    {
        if (_loading) return;

        var lighting = new Lighting
        {
            Effect = (RgbEffect)Math.Clamp(EffectIndex, 0, 4),
            Color = Current.Hex,
            Brightness = (int)Math.Round(Brightness),
            Speed = (int)Math.Clamp(Math.Round(Speed), 1, 100),
        };

        if (Selected is { } device)
        {
            _settings.Devices[device.Key] = lighting;
            device.HasOwn = true;
            NotifyTarget();
        }
        else
        {
            _settings.All = lighting;
        }

        Preview = lighting.Clone();
        Apply();
        _context.Settings.Save(_settings);
    }

    [RelayCommand]
    private void ChooseEffect(string index)
    {
        if (int.TryParse(index, out var value)) EffectIndex = Math.Clamp(value, 0, 4);
    }

    [RelayCommand]
    private void ChooseSwatch(Swatch? swatch)
    {
        if (swatch is null || !RgbColor.TryParse(swatch.Hex, out var color)) return;

        // A swatch on Spectrum, Wave or Off means "this color": back to a still one.
        if (!ShowsColor) EffectIndex = (int)RgbEffect.Static;
        Hex = color.Hex;
    }

    [RelayCommand]
    private void SelectAll() => Selected = null;

    [RelayCommand]
    private void Select(DeviceItem? device) => Selected = device;

    /// <summary>The device drops its own lighting and shows the one for all again.</summary>
    [RelayCommand]
    private void FollowAll()
    {
        if (Selected is not { } device) return;

        _settings.Devices.Remove(device.Key);
        device.HasOwn = false;
        _context.Settings.Save(_settings);
        LoadEditor();
        NotifyTarget();
        Apply();
    }

    internal void SetIncluded(DeviceItem device, bool included)
    {
        _settings.Excluded.Remove(device.Key);
        if (!included) _settings.Excluded.Add(device.Key);
        _context.Settings.Save(_settings);
        OnPropertyChanged(nameof(TargetDetail));
        Apply();
    }

    /// <summary>Every included device with its lighting, to the engine; the list's color dots follow.</summary>
    private void Apply()
    {
        if (_disposed) return;

        var targets = new List<(RgbDevice, Lighting)>();
        foreach (var item in Devices)
        {
            var lighting = _settings.Devices.GetValueOrDefault(item.Key) ?? _settings.All;
            item.Show(lighting);
            if (item.IsIncluded && item.Problem is null) targets.Add((item.Device, lighting));
        }

        _engine.Set(targets);
    }

    // --------------------------------------------------------------- Sources

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_disposed) return;

        Searching = true;
        try
        {
            await Task.WhenAll(_sources.Select(DiscoverAsync));
        }
        finally
        {
            Searching = false;
        }
    }

    /// <summary>OpenRGB found more devices or closed: ask it again once it has settled.</summary>
    private void DiscoverSoon(IRgbSource source)
    {
        if (_disposed) return;

        if (!_again.TryGetValue(source, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = DiscoverAsync(source);
            };
            _again[source] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private async Task DiscoverAsync(IRgbSource source)
    {
        var item = Sources.First(s => s.Name == source.Name);
        item.State = "Looking…";
        item.Health = SourceHealth.Ready;

        // Its old devices leave the engine before the source closes them. A selected one is found again by its key.
        var selectedKey = Selected?.Key;
        if (_found.Remove(source, out var old))
        {
            foreach (var device in Devices.Where(d => old.Contains(d.Device)).ToList()) Devices.Remove(device);
            if (Selected is { } selected && !Devices.Contains(selected)) Selected = null;
            Apply();
        }

        SourceResult result;
        try
        {
            result = await Task.Run(() => source.DiscoverAsync(_life.Token));
        }
        catch (Exception ex)
        {
            result = new SourceResult([], SourceHealth.Problem, ex.Message);
        }

        if (_disposed) return;

        _found[source] = [.. result.Devices];
        item.State = result.State;
        item.Health = result.Health;
        UpdateActions(source, item, result);

        foreach (var device in result.Devices)
        {
            Devices.Add(new DeviceItem(device, this)
            {
                IsIncluded = !_settings.Excluded.Contains(device.Key),
                HasOwn = _settings.Devices.ContainsKey(device.Key),
            });
        }

        Sort();
        if (Selected is null && selectedKey is not null) Selected = Devices.FirstOrDefault(d => d.Key == selectedKey);
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(ShowEmpty));
        NotifyTarget();
        Apply();
    }

    private void Sort()
    {
        var sorted = Devices.OrderBy(d => d.Device.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var at = Devices.IndexOf(sorted[i]);
            if (at != i) Devices.Move(at, i);
        }
    }

    private void UpdateActions(IRgbSource source, SourceItem item, SourceResult result)
    {
        item.CanOpenSettings = source is DynamicLightingSource;

        if (source is OpenRgbSource)
        {
            var installed = OpenRgbSource.FindExe(_context) is not null;
            item.CanStart = result.Health == SourceHealth.Missing && installed;
            item.CanInstall = result.Health == SourceHealth.Missing && !installed;
        }
    }

    private void DeviceFailed(RgbDevice device, string reason)
    {
        if (Devices.FirstOrDefault(d => d.Device == device) is { } item) item.Problem = $"Not answering: {reason}";
        Apply();
    }

    [RelayCommand]
    private Task StartOpenRgbAsync() => StartOpenRgbAsync(elevated: false);

    [RelayCommand]
    private Task StartOpenRgbAdminAsync() => StartOpenRgbAsync(elevated: true);

    private async Task StartOpenRgbAsync(bool elevated)
    {
        var source = _sources.OfType<OpenRgbSource>().First();
        var item = Sources.First(s => s.Name == source.Name);
        if (OpenRgbSource.FindExe(_context) is not { } exe) return;

        try
        {
            await OpenRgbSource.StartAsync(exe, elevated);
        }
        catch (Exception ex)
        {
            // Declining Windows' question ends here too.
            item.State = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "Not started" : ex.Message;
            return;
        }

        item.CanStart = false;
        item.State = "Starting…";

        // The server is up within seconds; detection goes on and OpenRGB reports more devices as it finds them.
        for (var i = 0; i < 30 && !_disposed; i++)
        {
            await Task.Delay(1000);
            using var probe = new System.Net.Sockets.TcpClient();
            try
            {
                await probe.ConnectAsync("127.0.0.1", OpenRgbSource.Port).WaitAsync(TimeSpan.FromMilliseconds(500));
                break;
            }
            catch (Exception)
            {
                // Not yet.
            }
        }

        if (!_disposed) await DiscoverAsync(source);
    }

    /// <summary>Installs OpenRGB with winget; its installer may ask Windows for rights.</summary>
    [RelayCommand]
    private async Task InstallOpenRgbAsync()
    {
        var source = _sources.OfType<OpenRgbSource>().First();
        var item = Sources.First(s => s.Name == source.Name);
        if (_context.Tools.Find("winget") is not { } winget)
        {
            item.State = "winget not found";
            return;
        }

        item.CanInstall = false;
        item.State = "Installing…";

        // Timed out on silence, not on duration: a stalled download says nothing, a slow one keeps printing.
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        silence.CancelAfter(TimeSpan.FromMinutes(2));

        var code = await ExternalProcess.StreamAsync(winget,
            ["install", "--id", OpenRgbSource.WingetId, "--exact", "--silent", "--accept-package-agreements",
             "--accept-source-agreements", "--disable-interactivity"],
            line =>
            {
                silence.CancelAfter(TimeSpan.FromMinutes(2));
                if (line.Trim().Length > 0 && !line.Contains('█') && !line.Contains('▒'))
                    Dispatcher.UIThread.Post(() => item.State = Trim(line.Trim()));
            },
            cancellationToken: silence.Token);

        if (_disposed) return;

        if (code == 0 && OpenRgbSource.FindExe(_context) is not null)
        {
            item.CanStart = true;
            item.State = "Installed. Start it to see its devices.";
        }
        else
        {
            item.CanInstall = true;
            item.State = code == -1 ? "Installation stopped answering" : $"winget ended with {code}";
        }
    }

    [RelayCommand]
    private void OpenLightingSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    private static string Trim(string text) => text.Length > 70 ? text[..70] + "…" : text;

    internal static Color ToAvalonia(RgbColor color) => Color.FromRgb(color.R, color.G, color.B);

    private static IBrush Gradient(Color from, Color to) => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    }.ToImmutable();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _life.Cancel();
        foreach (var timer in _again.Values) timer.Stop();

        // The engine first: nothing writes to a device while its source closes it.
        _engine.Dispose();
        foreach (var source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"{source.Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>One source in the list: its state and what can be done about it.</summary>
public sealed partial class SourceItem(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string State { get; set; } = "Looking…";

    [ObservableProperty]
    public partial SourceHealth Health { get; set; }

    [ObservableProperty]
    public partial bool CanStart { get; set; }

    [ObservableProperty]
    public partial bool CanInstall { get; set; }

    [ObservableProperty]
    public partial bool CanOpenSettings { get; set; }

    public bool IsReady => Health == SourceHealth.Ready;

    public bool IsProblem => Health == SourceHealth.Problem;

    partial void OnHealthChanged(SourceHealth value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsProblem));
    }
}

/// <summary>One device in the list.</summary>
public sealed partial class DeviceItem : ObservableObject
{
    private readonly RgbViewModel _owner;
    private bool _ready;

    internal DeviceItem(RgbDevice device, RgbViewModel owner)
    {
        Device = device;
        _owner = owner;
        Icon = IconCache.Get(RgbIcons.For(device.Kind));
        Detail = $"{(device.LedCount == 1 ? "1 LED" : $"{device.LedCount} LEDs")} · {device.Source}";
        if (device.LedCount == 1 && device.Name.EndsWith("devices", StringComparison.Ordinal)) Detail = device.Source;
        _ready = true;
    }

    public RgbDevice Device { get; }

    public string Key => Device.Key;

    public string Name => Device.Name;

    public string Detail { get; }

    public Geometry Icon { get; }

    public string? Note => Device.Note;

    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    [ObservableProperty]
    public partial bool HasOwn { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>The dot next to the name: the color, a rainbow for Spectrum and Wave, dark for Off.</summary>
    [ObservableProperty]
    public partial IBrush Swatch { get; set; } = Brushes.Transparent;

    partial void OnIsIncludedChanged(bool value)
    {
        if (_ready) _owner.SetIncluded(this, value);
    }

    internal void Show(Lighting lighting)
    {
        Swatch = lighting.Effect switch
        {
            RgbEffect.Spectrum or RgbEffect.Wave => RgbViewModel.HueTrack,
            RgbEffect.Off => new SolidColorBrush(Color.FromRgb(40, 42, 50)).ToImmutable(),
            _ => new SolidColorBrush(RgbViewModel.ToAvalonia(RgbColor.Parse(lighting.Color, RgbColor.Black))).ToImmutable(),
        };
        OnPropertyChanged(nameof(Note));
    }
}

/// <summary>A preset color.</summary>
public sealed class Swatch(string name, string hex)
{
    public string Name { get; } = name;

    public string Hex { get; } = hex;

    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}
