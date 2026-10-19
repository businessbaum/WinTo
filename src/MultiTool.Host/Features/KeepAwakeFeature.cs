using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>Keeps the computer from going to sleep while WinTo runs. The screen may still turn off.</summary>
/// <remarks>
/// The request itself is <see cref="StayAwake"/>, set on the UI thread by <see cref="ShellViewModel.SetKeepAwake"/>;
/// the control center toggles the same state, hence the subscription.
/// </remarks>
public sealed class KeepAwakeFeature : Feature
{
    public KeepAwakeFeature(ShellViewModel shell) : base(shell)
    {
        shell.KeepAwakeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEnabled));
            Describe();
        };

        Describe();
    }

    public override string Id => "keep-awake";

    public override string Name => "Keep awake";

    public override string Description => "The computer doesn't sleep while WinTo runs. The screen still turns off as configured.";

    public override string Group => "Power";

    protected override string IconPath =>
        "M4 8h13v5a5 5 0 0 1-5 5H9a5 5 0 0 1-5-5V8Zm13 1h1a3 3 0 0 1 0 6h-1.3c.2-.6.3-1.3.3-2h1a1 1 0 0 0 0-2h-1V9ZM3 20h15v2H3v-2ZM8 2h2v4H8V2Zm4 0h2v4h-2V2Z";

    protected override bool ReadEnabled() => Settings.KeepAwake;

    protected override void StoreEnabled(bool value) => Settings.KeepAwake = value;

    /// <summary>Through the shell: it saves, sets the Windows request and tells the control center.</summary>
    protected override void WriteEnabled(bool value) => Shell.SetKeepAwake(value);

    protected override void Start() => Describe();

    protected override void Stop() => Describe();

    private void Describe() => Detail = !Settings.KeepAwake ? "Off"
        : StayAwake.IsActive ? "On" : "Rejected by Windows, probably by a policy";
}
