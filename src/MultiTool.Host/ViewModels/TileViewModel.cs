using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MultiTool.Abstractions;
using MultiTool.UI;

namespace MultiTool.Host.ViewModels;

/// <summary>A tile on the home page.</summary>
/// <remarks>
/// Holds <b>no</b> reference to an <see cref="IModule"/>: a tile exists for every installed module,
/// a module only once someone opens it. Everything shown comes from the manifest or a probe.
/// </remarks>
public sealed partial class TileViewModel(ModuleManifest manifest) : ObservableObject
{
    public ModuleManifest Manifest { get; } = manifest;

    public string Id => Manifest.Id;

    public string Name => Manifest.Name;

    public string Summary => Manifest.Summary;

    public string Category => Manifest.Category;

    public Geometry Icon { get; } = IconCache.Get(manifest.Icon);

    /// <summary>The module's accent color, otherwise the app's.</summary>
    public IBrush AccentBrush { get; } = new SolidColorBrush(ParseAccent(manifest.Accent));

    /// <summary>The accent at low opacity: the icon's background.</summary>
    public IBrush AccentSoftBrush { get; } = new SolidColorBrush(ParseAccent(manifest.Accent), 0.16);

    public bool NeedsElevation => Manifest.Elevation != ElevationRequirement.Never;

    /// <summary>Searches name, summary and keywords, case-insensitive.</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = Contains(Name, term)
                || Contains(Summary, term)
                || Contains(Category, term)
                || Manifest.Keywords.Any(k => Contains(k, term));

            if (!hit) return false;
        }

        return true;

        static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }

    // --- runtime state ---

    [ObservableProperty]
    public partial ModuleStatus? Status { get; set; }

    /// <summary>Is the module's code loaded right now?</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    /// <summary>What the module is doing. Visible even with its view closed.</summary>
    [ObservableProperty]
    public partial ModuleActivity Activity { get; set; } = ModuleActivity.Idle;

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>A cached state older than this is no longer trusted and not shown.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>Line under the summary: running work beats the stored status.</summary>
    public string StatusText => Activity.IsBusy
        ? Activity.Text ?? "working…"
        : IsFresh ? Status?.Text ?? "" : "";

    private bool IsFresh => Status?.AsOf is not { } asOf || DateTimeOffset.Now - asOf < StaleAfter;

    public StatusLevel StatusLevel => Activity.IsBusy
        ? Abstractions.StatusLevel.Busy
        : Status?.Level ?? Abstractions.StatusLevel.Neutral;

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    // Separate flags instead of a converter: Avalonia binds a style class directly to a bool
    // (Classes.good="{Binding IsStatusGood}").
    public bool IsStatusGood => StatusLevel == Abstractions.StatusLevel.Good;

    public bool IsStatusWarn => StatusLevel == Abstractions.StatusLevel.Warn;

    public bool IsStatusBad => StatusLevel == Abstractions.StatusLevel.Bad;

    public bool IsStatusBusy => StatusLevel == Abstractions.StatusLevel.Busy;

    /// <summary>Whether the tile shows the spinning border.</summary>
    /// <remarks>
    /// Follows <see cref="ModuleActivity.IsBusy"/>, not the status dot: the dot says how things are,
    /// the border says something is happening right now.
    /// </remarks>
    public bool IsWorking => Activity.IsBusy;

    public string Tooltip
    {
        get
        {
            var parts = new List<string> { Manifest.Summary };
            if (Status?.Detail is { Length: > 0 } detail) parts.Add(detail);
            if (NeedsElevation) parts.Add("Needs administrator rights.");
            return string.Join(Environment.NewLine + Environment.NewLine, parts.Where(p => p.Length > 0));
        }
    }

    partial void OnStatusChanged(ModuleStatus? value) => RaiseDerived();

    partial void OnActivityChanged(ModuleActivity value) => RaiseDerived();

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusLevel));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsStatusGood));
        OnPropertyChanged(nameof(IsStatusWarn));
        OnPropertyChanged(nameof(IsStatusBad));
        OnPropertyChanged(nameof(IsStatusBusy));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(Tooltip));
    }

    private static readonly Color DefaultAccent = Color.Parse("#7C5CFF");

    private static Color ParseAccent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultAccent;
        try
        {
            return Color.Parse(value);
        }
        catch (Exception)
        {
            // An unreadable color in the manifest costs the accent, not the tile.
            return DefaultAccent;
        }
    }
}
