using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.TextTools;

/// <summary>Conversions otherwise done on some third-party website.</summary>
/// <remarks>
/// <para>
/// <b>Status <c>none</c>.</b> A tool turning input into output has no state that is still true days
/// later.
/// </para>
/// <para>
/// <c>keepsRunning</c> is <c>false</c>: nothing runs longer than a click, except hashing a large file,
/// which follows <c>Context.Shutdown</c>.
/// </para>
/// </remarks>
public sealed class TextToolsModule : ModuleBase
{
    private TextToolsViewModel? _viewModel;

    protected override void OnInitialize() => _viewModel = new TextToolsViewModel(Context);

    protected override Control Build() => new TextToolsView { DataContext = _viewModel };

    public override void ViewClosed(Control view) => view.DataContext = null;

    public override void Dispose()
    {
        _viewModel = null;
        base.Dispose();
    }
}
