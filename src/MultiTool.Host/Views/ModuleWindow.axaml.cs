using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MultiTool.Abstractions;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Views;

/// <summary>A module in its own window.</summary>
/// <remarks>
/// Shows the <b>same</b> view a tab would. Nothing is rebuilt when switching, so a running
/// download or an open audio device survives it.
/// </remarks>
public partial class ModuleWindow : Window
{
    private readonly ShellViewModel? _shell;
    private readonly string _moduleId = "";

    // Parameterless constructor for the XAML previewer only.
    public ModuleWindow()
    {
        InitializeComponent();
        WindowCorners.Round(this);
        WindowResizeGrips.Attach(this, Root);
    }

    public ModuleWindow(ShellViewModel shell, ModuleManifest manifest, Control view)
    {
        InitializeComponent();
        WindowCorners.Round(this);

        _shell = shell;
        _moduleId = manifest.Id;

        // The window title doubles as the title bar text.
        Title = manifest.Name + " – WinTo";
        ModuleHost.Content = view;
    }

    /// <summary>Detaches the view so it can be attached elsewhere.</summary>
    /// <remarks>A control has exactly one parent; docking would otherwise throw "already has a visual parent".</remarks>
    public void DetachContent() => ModuleHost.Content = null;

    /// <summary>Hides "Dock as tab". The single module window (<c>--module</c>) has no main window to dock into.</summary>
    internal void HideDockButton() => DockButton.IsVisible = false;

    /// <summary>Minimize and restore report visibility to the module.</summary>
    /// <remarks>
    /// Window state, not <c>Activated</c>/<c>Deactivated</c>: those fire on every focus change, and a
    /// separate window is meant to be watched on the side.
    /// </remarks>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty || _shell is null) return;
        _shell.SetViewVisible(_moduleId, WindowState != WindowState.Minimized);
    }

    private void OnDockClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_shell is not null) _ = _shell.DockWindowAsync(_moduleId);
    }


    // ================= Custom title bar =================
    //
    // With WindowDecorations="None" Windows draws nothing around this window. Dragging,
    // double-click to maximize and the three dots on the right live here.

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Drag anywhere except on controls. Walk up from the hit element and stop only if a
        // control lies in between; the tab ScrollViewer covers almost the whole header, so
        // comparing e.Source with sender never matched.
        if (e.Source is Visual hit && HitsControl(hit, sender as Visual)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>Is there a control between the hit element and the header?</summary>
    private static bool HitsControl(Visual from, Visual? stopAt)
    {
        for (var visual = from; visual is not null && visual != stopAt; visual = visual.GetVisualParent())
        {
            if (visual is Button or ToggleButton or TextBox or ComboBox or Slider or CheckBox or RadioButton)
                return true;
        }

        return false;
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

}
