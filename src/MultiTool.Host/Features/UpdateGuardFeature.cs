using Avalonia.Threading;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;
using MultiTool.Host.Views;
using MultiTool.UI;

namespace MultiTool.Host.Features;

/// <summary>Windows installs updates but doesn't restart on its own while someone is signed in.</summary>
/// <remarks>
/// <para>
/// <b>Off by default; the switch is WinTo's, not the policy.</b> Switching on sets
/// <c>NoAutoRebootWithLoggedOnUsers</c> (<see cref="MachinePolicy.NoAutoRestart"/>) if it isn't set yet, with
/// one administrator prompt; switching off removes it only if WinTo set it. A policy set by another tool
/// or by Group Policy is left as it is.
/// </para>
/// <para>
/// <b>A pending restart is said once</b>, as a card, instead of Windows choosing the moment. Checked every
/// half hour from <c>WindowsUpdate\Auto Update\RebootRequired</c>, readable without rights.
/// </para>
/// </remarks>
public sealed class UpdateGuardFeature(ShellViewModel shell) : Feature(shell)
{
    private const string ShieldPath =
        "M12 2 4 5v6.5c0 4.7 3.2 9 8 10.5 4.8-1.5 8-5.8 8-10.5V5l-8-3Zm-1 13.4-3.7-3.7 1.4-1.4 2.3 2.3 4.3-4.3 1.4 1.4-5.7 5.7Z";

    /// <summary>Marker in <see cref="AppSettings.TweakBackups"/>: WinTo set the policy and may remove it.</summary>
    private const string WroteMarker = "update-guard-policy";

    private DispatcherTimer? _timer;
    private bool _announced;
    private bool _switching;

    public override string Id => "update-guard";
    public override string Name => "No forced restarts";
    public override string Description =>
        "Windows installs updates but doesn't restart while someone is signed in. A pending restart shows as a card.";
    public override string Group => "Windows";
    protected override string IconPath => ShieldPath;

    protected override bool ReadEnabled() => Settings.Switches.TryGetValue(Id, out var on) && on;

    protected override void StoreEnabled(bool value) => Settings.Switches[Id] = value;

    /// <remarks>Asynchronous: the switch shows the old state until the prompt is answered, then the new one.</remarks>
    protected override void WriteEnabled(bool value)
    {
        if (!_switching) _ = SwitchAsync(value);
    }

    private async Task SwitchAsync(bool value)
    {
        _switching = true;
        string? problem = null;

        if (value && !MachinePolicy.IsSet(MachinePolicy.NoAutoRestart))
        {
            Detail = "Waiting for the administrator prompt…";
            problem = await MachinePolicy.SetAsync(MachinePolicy.NoAutoRestart, true);
            if (problem is null) Settings.TweakBackups[WroteMarker] = [];
        }
        else if (!value && Settings.TweakBackups.ContainsKey(WroteMarker) && MachinePolicy.IsSet(MachinePolicy.NoAutoRestart))
        {
            Detail = "Waiting for the administrator prompt…";
            problem = await MachinePolicy.SetAsync(MachinePolicy.NoAutoRestart, false);
            if (problem is null) Settings.TweakBackups.Remove(WroteMarker);
        }

        if (problem is null) StoreEnabled(value);
        Shell.SaveSettings();

        _switching = false;
        Detail = problem ?? (value ? StateText() : "");
        OnPropertyChanged(nameof(IsEnabled));
        Apply();
    }

    protected override void Start()
    {
        Detail = StateText();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();

        // Not in the first seconds after start: the window and the features come first.
        DispatcherTimer.RunOnce(Check, TimeSpan.FromMinutes(2));
    }

    protected override void Stop()
    {
        _timer?.Stop();
        _timer = null;
        if (!_switching) Detail = "";
    }

    private static string StateText() =>
        !MachinePolicy.IsSet(MachinePolicy.NoAutoRestart) ? "The policy is not set. Switching off and on again sets it."
        : MachinePolicy.RestartPending() ? "On · a restart is pending for updates."
        : "On";

    private void Check()
    {
        if (_timer is null) return;

        var pending = MachinePolicy.RestartPending();
        if (!_switching) Detail = StateText();

        if (!pending)
        {
            _announced = false;
            return;
        }

        if (_announced) return;
        _announced = true;

        Toast.Show(NoticeKind.System, "Restart pending for updates", "Windows waits until a restart is chosen.", IconCache.Get(ShieldPath));
    }
}
