using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;
using MultiTool.UI;

namespace MultiTool.Host.Features;

/// <summary>Something WinTo does without a window: a shortcut, a watcher, a notice.</summary>
/// <remarks>
/// <para>
/// <b>Features are part of the app, tools are modules.</b> A feature runs from the start, so its code lives in
/// the host and must stay small: a hook, a timer, a watcher. Anything heavy it needs is a module loaded on
/// use (the Win+Shift+S shortcut loads the capture tool only when pressed), which keeps "no module code at
/// startup" true.
/// </para>
/// <para>
/// Switched on and off on the Features page; the state is stored in <see cref="AppSettings"/>. Started by
/// <see cref="FeatureHost.StartAll"/> after the first frame, stopped on quit.
/// </para>
/// </remarks>
public abstract partial class Feature(ShellViewModel shell) : ObservableObject
{
    protected ShellViewModel Shell { get; } = shell;

    protected AppSettings Settings => Shell.Settings;

    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>One sentence: what it does for the user.</summary>
    public abstract string Description { get; }

    /// <summary>Heading on the Features page.</summary>
    public abstract string Group { get; }

    protected abstract string IconPath { get; }

    public Geometry Icon => IconCache.Get(IconPath);

    /// <summary>A short state under the name: the shortcut, "3 games", "Vencord not installed".</summary>
    [ObservableProperty]
    public partial string Detail { get; protected set; } = "";

    /// <summary>Whether the switch is on.</summary>
    public bool IsEnabled
    {
        get => ReadEnabled();
        set
        {
            if (value == ReadEnabled()) return;

            WriteEnabled(value);
            Apply();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether it currently runs. Off while switched off, or before <see cref="FeatureHost.StartAll"/>.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Its shortcut was pressed. The tour listens to tick off "try it".</summary>
    public event Action? Used;

    protected void ReportUsed() => Used?.Invoke();

    /// <summary>Options shown below the switch; a view model with a DataTemplate on the Features page.</summary>
    public virtual object? Options => null;

    public bool HasOptions => Options is not null;

    [ObservableProperty]
    public partial bool ShowOptions { get; set; }

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    partial void OnShowOptionsChanged(bool value)
    {
        if (value) OptionsOpened();
    }

    /// <summary>The options were expanded: a chance to read what they show, e.g. running programs.</summary>
    protected virtual void OptionsOpened() { }

    protected abstract bool ReadEnabled();

    /// <summary>Stores the switch. The default writes the settings file.</summary>
    protected virtual void WriteEnabled(bool value)
    {
        StoreEnabled(value);
        Shell.SaveSettings();
    }

    protected abstract void StoreEnabled(bool value);

    /// <summary>Starts or stops to match the switch. A feature that fails stays off and says why.</summary>
    public void Apply()
    {
        if (IsEnabled == IsRunning) return;

        try
        {
            if (IsEnabled) Start();
            else Stop();

            IsRunning = IsEnabled;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Detail = $"Couldn't start: {ex.Message}";
            new ModuleLog("features").Error($"{Id} failed to {(IsEnabled ? "start" : "stop")}.", ex);
        }
    }

    /// <summary>On quit. Releases hooks, timers and watchers.</summary>
    public void Shutdown()
    {
        if (!IsRunning) return;

        try
        {
            Stop();
        }
        catch (Exception)
        {
            // Quitting anyway.
        }

        IsRunning = false;
    }

    protected abstract void Start();

    protected abstract void Stop();
}
