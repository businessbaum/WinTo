using Avalonia.Controls;
using MultiTool.Abstractions;
using MultiTool.Modules.Files.Convert;
using MultiTool.Modules.Files.Metadata;

namespace MultiTool.Modules.Files;

/// <summary>Convert and shrink images, remove metadata from files.</summary>
/// <remarks>
/// Since 0.13.0 the former image converter and metadata cleaner as two sections, each with its own settings
/// and data folder through <see cref="SectionContext"/>. Status <c>none</c>: both turn input into output and
/// keep no state worth a tile.
/// </remarks>
public sealed class FilesModule : ModuleBase
{
    private FilesViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new FilesViewModel(
            new ImageConvertViewModel(new SectionContext(Context, "image-convert")),
            new MetadataCleanerViewModel(new SectionContext(Context, "metadata-cleaner")));

        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build() => new FilesView { DataContext = _viewModel };

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>"metadata" opens the metadata section.</summary>
    /// <remarks>
    /// From the Explorer menu (0.13.0): "convert:&lt;path&gt;" and "metadata:&lt;path&gt;" open the section with
    /// that file added.
    /// </remarks>
    public override void OpenWith(string argument)
    {
        if (_viewModel is not { } viewModel) return;

        const string convert = "convert:";
        const string metadata = "metadata:";

        if (string.Equals(argument, "metadata", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.ShowMetadataCommand.Execute(null);
        }
        else if (argument.StartsWith(convert, StringComparison.OrdinalIgnoreCase))
        {
            viewModel.ShowConvertCommand.Execute(null);
            viewModel.Convert.Add([argument[convert.Length..]]);
        }
        else if (argument.StartsWith(metadata, StringComparison.OrdinalIgnoreCase))
        {
            viewModel.ShowMetadataCommand.Execute(null);
            _ = viewModel.Metadata.AddAsync([argument[metadata.Length..]]);
        }
    }

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null) _viewModel.ActivityChanged -= OnActivityChanged;
        _viewModel = null;

        SetIdle();
        base.Dispose();
    }
}
