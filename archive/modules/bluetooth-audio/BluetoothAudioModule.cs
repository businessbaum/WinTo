using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.BluetoothAudio;

/// <summary>Connect and disconnect Bluetooth headphones, with battery level, including AirPods.</summary>
/// <remarks>
/// <para>
/// <b>Deliberately not supported:</b> AirPods noise control, transparency and ear detection. They use
/// Apple's own protocol (AAP) over an L2CAP channel (PSM 0x1001) that the Windows stack doesn't expose
/// to user mode; even commercial tools need a kernel driver for it.
/// </para>
/// <para>
/// What remains is the everyday part: the AirPods are on the iPhone, one click brings them back
/// (<c>KSPROPSETID_BtAudio</c>, <c>ONESHOT_RECONNECT</c>), the history shows when they switched, and the
/// battery level shows while connected.
/// </para>
/// </remarks>
public sealed class BluetoothAudioModule : ModuleBase
{
    private BluetoothAudioViewModel? _viewModel;

    protected override void OnInitialize() => _viewModel = new BluetoothAudioViewModel(Context);

    protected override Control Build() => new BluetoothAudioView { DataContext = _viewModel };

    public override void ViewActivated(Control view) => _viewModel?.ViewActivated();

    public override void ViewDeactivated(Control view) => _viewModel?.ViewDeactivated();

    public override void ViewClosed(Control view) => view.DataContext = null;

    public override void Dispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        base.Dispose();
    }
}
