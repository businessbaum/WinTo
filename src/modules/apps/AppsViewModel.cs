using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Modules.Apps.Packages;
using MultiTool.Modules.Apps.Uninstaller;

namespace MultiTool.Modules.Apps;

/// <summary>The two sections and which one is in front.</summary>
/// <remarks>Both views stay alive while the tool is open, so an installation keeps running while switching.</remarks>
public sealed partial class AppsViewModel : ObservableObject
{
    private bool _uninstallerRead;

    public AppsViewModel(PackagesViewModel packages, UninstallerViewModel uninstaller)
    {
        Packages = packages;
        Uninstaller = uninstaller;

        Packages.ActivityChanged += (_, activity) => ActivityChanged?.Invoke(this, activity);
    }

    public PackagesViewModel Packages { get; }

    public UninstallerViewModel Uninstaller { get; }

    /// <summary>The packages section's work: installations, updates, the list.</summary>
    public event EventHandler<ModuleActivity>? ActivityChanged;

    /// <summary>0 install and update, 1 uninstall.</summary>
    [ObservableProperty]
    public partial int Section { get; set; }

    public bool IsPackages => Section == 0;

    public bool IsUninstall => Section == 1;

    partial void OnSectionChanged(int value)
    {
        OnPropertyChanged(nameof(IsPackages));
        OnPropertyChanged(nameof(IsUninstall));

        // Three registry branches with hundreds of subkeys: read when first wanted, not on every open.
        if (value == 1 && !_uninstallerRead)
        {
            _uninstallerRead = true;
            _ = Uninstaller.RefreshAsync();
        }
    }

    [RelayCommand]
    private void ShowPackages() => Section = 0;

    [RelayCommand]
    private void ShowUninstall() => Section = 1;
}
