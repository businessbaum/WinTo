using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>A feature that is a keyboard shortcut: one combination, chosen from a short list.</summary>
/// <remarks>
/// <para>
/// Registered through <see cref="Hotkeys"/> (<c>RegisterHotKey</c>, no hook); a combination Windows owns
/// itself (Win+V) through <see cref="WinKeyShortcuts"/>.
/// </para>
/// <para>
/// <b>A taken combination falls back to the next free one</b> of the list, as long as the user hasn't chosen
/// one: PowerToys holds Win+Shift+T, Win+Shift+C and Win+Ctrl+T by default, and a shortcut that silently does
/// nothing was the worst outcome. The detail line names the one in use and the one that was taken.
/// </para>
/// <para>
/// The switch and the chosen combination are stored per feature id in <see cref="AppSettings.Switches"/> and
/// <see cref="AppSettings.Shortcuts"/>, so a new shortcut feature needs no new settings property.
/// </para>
/// </remarks>
public abstract class HotkeyFeature : Feature
{
    private int? _registration;
    private uint? _windowsKey;
    private HotkeyChoice? _active;

    protected HotkeyFeature(ShellViewModel shell) : base(shell)
    {
        ShortcutOptions = new HotkeyOptions(this);
    }

    /// <summary>What can be chosen; the first is the default.</summary>
    public abstract IReadOnlyList<HotkeyChoice> Choices { get; }

    /// <summary>On by default unless a feature says otherwise.</summary>
    protected virtual bool OnByDefault => true;

    public HotkeyOptions ShortcutOptions { get; }

    /// <summary>Whether a combination is in use.</summary>
    protected bool Registered => _active is not null;

    /// <summary>The combination in use: the chosen one, or the first free one of the list.</summary>
    protected HotkeyChoice? Active => _active;

    public override object? Options => ShortcutOptions;

    public HotkeyChoice Choice =>
        Settings.Shortcuts.TryGetValue(Id, out var label) && Choices.FirstOrDefault(c => c.Label == label) is { } chosen
            ? chosen
            : Choices[0];

    protected override bool ReadEnabled() => Settings.Switches.TryGetValue(Id, out var on) ? on : OnByDefault;

    protected override void StoreEnabled(bool value) => Settings.Switches[Id] = value;

    protected override void Start()
    {
        var chosen = Settings.Shortcuts.ContainsKey(Id);
        var preferred = Choice;

        foreach (var choice in chosen ? [preferred] : Choices)
        {
            if (!TryRegister(choice)) continue;

            _active = choice;
            break;
        }

        Detail = _active is null ? $"{preferred.Label} is taken by another program. Choose another one."
            : _active == preferred ? _active.Label
            : $"{_active.Label} · {preferred.Label} is taken by another program";
    }

    private bool TryRegister(HotkeyChoice choice)
    {
        if (choice.TakesOverWindows)
        {
            WinKeyShortcuts.Set((int)choice.VirtualKey, (choice.Modifiers & HotkeyChoice.Shift) != 0,
                () => Dispatcher.UIThread.Post(Fire));
            _windowsKey = choice.VirtualKey;
            return true;
        }

        _registration = Hotkeys.Register(choice, () => Dispatcher.UIThread.Post(Fire));
        return _registration is not null;
    }

    protected override void Stop()
    {
        Hotkeys.Unregister(_registration);
        _registration = null;

        if (_windowsKey is { } key) WinKeyShortcuts.Remove((int)key);
        _windowsKey = null;

        _active = null;
        Detail = "Off";
    }

    /// <summary>The combination was pressed. On the UI thread.</summary>
    protected abstract void Pressed();

    private void Fire()
    {
        Pressed();
        ReportUsed();
    }

    /// <summary>From the options: register the new combination right away.</summary>
    internal void Choose(HotkeyChoice choice)
    {
        Settings.Shortcuts[Id] = choice.Label;
        Shell.SaveSettings();

        if (!IsRunning) return;

        Stop();
        Start();
    }
}

/// <summary>The combination picker below a shortcut feature.</summary>
public sealed partial class HotkeyOptions : ObservableObject
{
    private readonly HotkeyFeature _feature;
    private bool _loading;

    internal HotkeyOptions(HotkeyFeature feature)
    {
        _feature = feature;
        _loading = true;
        Selected = feature.Choice;
        _loading = false;
    }

    public IReadOnlyList<HotkeyChoice> Choices => _feature.Choices;

    [ObservableProperty]
    public partial HotkeyChoice? Selected { get; set; }

    partial void OnSelectedChanged(HotkeyChoice? value)
    {
        if (_loading || value is null) return;
        _feature.Choose(value);
    }
}
