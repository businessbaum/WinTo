using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.ClipboardTray;

/// <summary>The entry point, the only type the host knows.</summary>
/// <remarks>
/// The shelf lives on the module, not the view: <c>CreateView</c> runs on every open, and the
/// collection would be lost with every tab close. That's why the manifest says <c>keepsRunning</c>;
/// the close prompt, asked while something is on the shelf, is the one place the loss is announced.
/// </remarks>
public sealed class ClipboardTrayModule : ModuleBase
{
    private ClipboardTrayViewModel? _viewModel;

    protected override Control Build()
    {
        _viewModel ??= new ClipboardTrayViewModel(Context);
        return new ClipboardTrayView { DataContext = _viewModel };
    }

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>Whatever is on the shelf is discarded on close.</summary>
    public override bool HasUnsavedWork => _viewModel?.Entries.Count > 0;

    public override void Dispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;

        base.Dispose();
    }
}
