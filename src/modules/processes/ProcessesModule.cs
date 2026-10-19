using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Processes;

/// <summary>View running programs, close them, and end them when "End task" isn't enough.</summary>
/// <remarks>
/// <para>
/// <b>Status <c>none</c>:</b> "350 processes" is already wrong when the module closes. Only
/// <see cref="ModuleBase.SetActivity"/> is used, while a kill attempt runs.
/// </para>
/// <para>
/// <c>keepsRunning</c> is <c>false</c>: the ladder takes seconds and nothing of it should outlive the tab.
/// The timer follows <see cref="ViewActivated"/> and <see cref="ViewDeactivated"/>; opening 350 processes
/// every two seconds for a hidden window would waste CPU.
/// </para>
/// </remarks>
public sealed class ProcessesModule : ModuleBase
{
    private ProcessesViewModel? _viewModel;

    protected override void OnInitialize()
    {
        _viewModel = new ProcessesViewModel(Context);
        _viewModel.ActivityChanged += OnActivityChanged;
    }

    protected override Control Build() => new ProcessesView { DataContext = _viewModel };

    public override void ViewActivated(Control view)
    {
        _viewModel?.StartTicking();

        // Ask for rights right when opening, not after the first failure. Once per instance.
        _ = _viewModel?.EnsureRightsAsync();
    }

    public override void ViewDeactivated(Control view) => _viewModel?.StopTicking();

    public override void ViewClosed(Control view)
    {
        _viewModel?.StopTicking();
        view.DataContext = null;
    }

    private void OnActivityChanged(object? sender, ModuleActivity activity) => SetActivity(activity);

    public override void Dispose()
    {
        if (_viewModel is not null) _viewModel.ActivityChanged -= OnActivityChanged;

        _viewModel?.Dispose();
        _viewModel = null;

        SetIdle();
        base.Dispose();
    }
}
