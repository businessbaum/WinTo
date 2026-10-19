using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Modules.Soundboard.Models;

namespace MultiTool.Modules.Soundboard.ViewModels;

public partial class SoundItemViewModel : ObservableObject
{
    private readonly SoundboardViewModel _owner;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isRecordingHotkey;
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private string _durationText = "–:–";
    [ObservableProperty] private int _depth;

    /// <summary>Indent by nesting depth in the folder tree.</summary>
    public Avalonia.Thickness Indent => new(Depth * 22, 0, 0, 0);

    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(Indent));

    public SoundItemViewModel(SoundEntry entry, SoundboardViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        UpdateDuration();
        RefreshMissing();
    }

    public SoundEntry Entry { get; }

    public Guid Id => Entry.Id;

    public string Name
    {
        get => Entry.Name;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed == Entry.Name) return;
            Entry.Name = trimmed;
            OnPropertyChanged();
            _owner.PersistLibrary();
        }
    }

    public string FilePath => Entry.FilePath;

    public string FileName => Path.GetFileName(Entry.FilePath);

    public string HotkeyText => IsRecordingHotkey
        ? "Press a key…"
        : Entry.Hotkey is { IsEmpty: false } hotkey ? hotkey.DisplayName : "Hotkey…";

    public bool HasHotkey => Entry.Hotkey is { IsEmpty: false };

    // double instead of float: Slider.Value is double, so the binding needs no conversion.
    public double Volume
    {
        get => Entry.Volume;
        set
        {
            var clamped = (float)Math.Clamp(value, 0d, 1.5d);
            if (Math.Abs(clamped - Entry.Volume) < 0.001f) return;
            Entry.Volume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            _owner.OnItemVolumeChanged(this);
        }
    }

    public string VolumeText => $"{Entry.Volume * 100:0}%";

    public bool Loop
    {
        get => Entry.Loop;
        set
        {
            if (value == Entry.Loop) return;
            Entry.Loop = value;
            OnPropertyChanged();
            _owner.PersistLibrary();
        }
    }

    [RelayCommand]
    private void Toggle() => _owner.Toggle(this);

    [RelayCommand]
    private void RecordHotkey() => _owner.BeginHotkeyCapture(this);

    [RelayCommand]
    private void ClearHotkey()
    {
        if (Entry.Hotkey is null) return;
        Entry.Hotkey = null;
        NotifyHotkeyChanged();
        _owner.PersistLibrary();
        _owner.RefreshHotkeyBindings();
    }

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    [RelayCommand]
    private void ShowInFolder() => _owner.ShowInFolder(this);

    public void SetHotkey(Hotkey? hotkey)
    {
        Entry.Hotkey = hotkey;
        NotifyHotkeyChanged();
    }

    public void NotifyHotkeyChanged()
    {
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(HasHotkey));
    }

    public void RefreshMissing()
    {
        try { IsMissing = !File.Exists(Entry.FilePath); }
        catch (Exception) { IsMissing = true; }
    }

    public void SetDuration(TimeSpan duration)
    {
        Entry.DurationSeconds = duration.TotalSeconds;
        UpdateDuration();
    }

    private void UpdateDuration()
    {
        var seconds = Entry.DurationSeconds;
        DurationText = seconds <= 0
            ? "–:–"
            : TimeSpan.FromSeconds(seconds) is { TotalHours: >= 1 } long_
                ? long_.ToString(@"h\:mm\:ss")
                : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
    }

    partial void OnIsRecordingHotkeyChanged(bool value) => OnPropertyChanged(nameof(HotkeyText));
}
