using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Rgb;

/// <summary>RGB lighting: one color or effect for every device it can reach, or one per device.</summary>
/// <remarks>
/// <para>
/// <b>Sources</b>: Windows Dynamic Lighting (no software of the maker), OpenRGB (motherboards, RAM, graphics cards,
/// strips and most peripherals), SteelSeries GG and Razer Synapse (their devices through the maker's software).
/// Corsair iCUE and Logitech G HUB hand their newer devices to Dynamic Lighting; OpenRGB covers older ones.
/// </para>
/// <para>
/// <b>Keeps running</b>: effects are computed here, and Dynamic Lighting, GG and Synapse take their lights back
/// when the module lets go. Status <c>none</c>: what was set is in the settings, not a state worth a tile.
/// </para>
/// </remarks>
public sealed class RgbModule : ModuleBase
{
    private RgbViewModel? _viewModel;

    protected override void OnInitialize() => _viewModel = new RgbViewModel(Context);

    protected override Control Build() => new RgbView { DataContext = _viewModel };

    public override void ViewClosed(Control view) => view.DataContext = null;

    public override void Dispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        base.Dispose();
    }
}
