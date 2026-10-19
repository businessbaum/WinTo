using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiTool.Host.ViewModels;

/// <summary>A tab in the main window.</summary>
public sealed partial class ShellTab : ObservableObject
{
    public required string Key { get; init; }

    public required Control Content { get; init; }

    /// <summary>Set when the tab shows a module. Empty for home, settings and diagnostics.</summary>
    public string? ModuleId { get; init; }

    /// <summary>The home tab cannot be closed.</summary>
    public bool CanClose { get; init; } = true;

    /// <summary>Whether the tab may be popped out into its own window.</summary>
    public bool CanPopOut { get; init; } = true;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial Geometry? Icon { get; set; }

    /// <summary>Dot on the tab while the module is working.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether this tab is in front. Maintained by the shell.</summary>
    /// <remarks>
    /// A flag on the tab is cheaper than comparing against SelectedTab in every item binding.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
