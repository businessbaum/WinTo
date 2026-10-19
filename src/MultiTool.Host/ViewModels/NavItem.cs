using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiTool.Host.ViewModels;

/// <summary>A fixed page tab in the header: Dashboard, Features, Tools, Settings.</summary>
/// <remarks>Open tools are listed below these, from <see cref="ShellViewModel.OpenTabs"/>.</remarks>
public sealed partial class NavItem(string key, string title, Geometry icon) : ObservableObject
{
    /// <summary>The key of the page's tab.</summary>
    public string Key { get; } = key;

    public string Title { get; } = title;

    public Geometry Icon { get; } = icon;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
