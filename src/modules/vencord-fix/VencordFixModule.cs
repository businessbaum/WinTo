using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.VencordFix;

/// <summary>Restores the Vencord patch that every Discord update removes.</summary>
/// <remarks>
/// Finds installations, determines their state, builds the stub, patches, unpatches and launches.
/// Taking over desktop/start menu/taskbar shortcuts and an autostart entry is left out on purpose:
/// that needs a single EXE permanently standing in for Discord, not a tab in a toolbox. This module
/// repairs on demand and launches on request.
/// </remarks>
public sealed class VencordFixModule : ModuleBase
{
    private VencordFixViewModel? _viewModel;

    protected override void OnInitialize() => _viewModel = new VencordFixViewModel(Context);

    protected override Control Build()
    {
        var view = new VencordFixView { DataContext = _viewModel };

        // Not in the constructor: enumerating app folders and reading every app.asar is disk work,
        // not part of the tile click.
        _ = _viewModel?.RefreshAsync();

        return view;
    }

    public override void ViewClosed(Control view) => view.DataContext = null;

    /// <summary>"auto" from the app's Keep Vencord feature: repair after a Discord update, without a view.</summary>
    /// <remarks>Busy while it runs and idle afterwards, so the shell releases the quietly loaded module again.</remarks>
    public override void OpenWith(string argument)
    {
        if (argument != "auto" || _viewModel is not { } viewModel) return;

        _ = RepairAsync(viewModel);
    }

    private async Task RepairAsync(VencordFixViewModel viewModel)
    {
        SetActivity(new ModuleActivity(true, "Restoring Vencord…"));
        try
        {
            await viewModel.AutoRepairAsync();
        }
        catch (Exception ex)
        {
            Context.Log.Error("Automatic repair failed.", ex);
        }
        finally
        {
            SetIdle();
        }
    }

    public override void Dispose()
    {
        _viewModel = null;
        base.Dispose();
    }
}
