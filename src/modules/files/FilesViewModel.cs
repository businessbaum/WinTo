using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Modules.Files.Convert;
using MultiTool.Modules.Files.Metadata;

namespace MultiTool.Modules.Files;

/// <summary>The two sections and which one is in front.</summary>
/// <remarks>Both views stay alive while the tool is open, so a running batch survives switching.</remarks>
public sealed partial class FilesViewModel : ObservableObject
{
    private ModuleActivity _convertActivity = ModuleActivity.Idle;
    private ModuleActivity _metadataActivity = ModuleActivity.Idle;

    public FilesViewModel(ImageConvertViewModel convert, MetadataCleanerViewModel metadata)
    {
        Convert = convert;
        Metadata = metadata;

        Convert.ActivityChanged += (_, activity) =>
        {
            _convertActivity = activity;
            Report();
        };

        Metadata.ActivityChanged += (_, activity) =>
        {
            _metadataActivity = activity;
            Report();
        };
    }

    public ImageConvertViewModel Convert { get; }

    public MetadataCleanerViewModel Metadata { get; }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    /// <summary>0 convert, 1 metadata.</summary>
    [ObservableProperty]
    public partial int Section { get; set; }

    public bool IsConvert => Section == 0;

    public bool IsMetadata => Section == 1;

    partial void OnSectionChanged(int value)
    {
        OnPropertyChanged(nameof(IsConvert));
        OnPropertyChanged(nameof(IsMetadata));
    }

    [RelayCommand]
    private void ShowConvert() => Section = 0;

    [RelayCommand]
    private void ShowMetadata() => Section = 1;

    private void Report() => ActivityChanged?.Invoke(this,
        _convertActivity.IsBusy ? _convertActivity
        : _metadataActivity.IsBusy ? _metadataActivity
        : ModuleActivity.Idle);
}
