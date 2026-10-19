using MultiTool.Host.Features;

namespace MultiTool.Host.ViewModels;

/// <summary>A heading on the Features page with its features.</summary>
public sealed record FeatureGroup(string Name, IReadOnlyList<Feature> Features)
{
    public string Label => Name.ToUpperInvariant();
}

/// <summary>The Features page: every feature with its switch and options, grouped.</summary>
public sealed class FeaturesViewModel(ShellViewModel shell)
{
    public IReadOnlyList<FeatureGroup> Groups { get; } =
    [
        .. shell.Features.All
            .GroupBy(f => f.Group)
            .Select(g => new FeatureGroup(g.Key, [.. g])),
    ];
}
