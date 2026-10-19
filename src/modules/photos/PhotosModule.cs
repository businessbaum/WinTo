using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Photos;

/// <summary>A clean photo viewer with the edits people need: light and color, crop, rotate, markup, size.</summary>
/// <remarks>
/// <para>
/// Opened mostly from Explorer ("Open in WinTo", or WinTo as the program for pictures): the argument
/// is the file. Separate from Capture on purpose: a screenshot wants arrows and pixelation right away, a
/// photo wants to be looked at first and edited on request.
/// </para>
/// <para>
/// <b>Status <c>none</c></b>: nothing worth a tile. <b>Unsaved edits</b> are reported, so closing asks.
/// </para>
/// </remarks>
public sealed class PhotosModule : ModuleBase
{
    private PhotosViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new PhotosViewModel(Context);
        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build() => new PhotosView { DataContext = _viewModel };

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>A file path: shows that picture and its folder.</summary>
    public override void OpenWith(string argument)
    {
        if (_viewModel is not null && File.Exists(argument)) _ = _viewModel.OpenAsync(argument);
    }

    public override bool HasUnsavedWork => _viewModel?.HasUnsavedWork == true;

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null)
        {
            _viewModel.ActivityChanged -= OnActivityChanged;
            _viewModel.Dispose();
            _viewModel = null;
        }

        SetIdle();
        base.Dispose();
    }
}
