using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Host.Services;

namespace MultiTool.Host.ViewModels;

/// <summary>The Tools page: every tool as a tile, most relevant first.</summary>
/// <remarks>
/// Sorted by the manifest's <c>order</c>, which since 0.13.0 is relevance across all tools rather than a
/// position within a category: what people reach for most comes first. Pinned tools go on top.
/// </remarks>
public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly List<TileViewModel> _all;

    public ToolsViewModel(ShellViewModel shell)
    {
        _shell = shell;

        _all = shell.Catalog.Modules
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Name, StringComparer.CurrentCulture)
            .Select(m => new TileViewModel(m) { IsPinned = shell.Settings.Favorites.Contains(m.Id) })
            .ToList();

        Problems = [.. shell.Catalog.Problems];
        Rebuild();
    }

    /// <summary>The app sits in the tray. The home page has no timers; kept for the shell's lifecycle calls.</summary>
    public void Suspend() { }

    public void Resume() { }

    /// <summary>All tiles, unfiltered. The shell updates status and activity through these.</summary>
    public IReadOnlyList<TileViewModel> AllTiles => _all;

    /// <summary>Manifests that could not be read. Shown at the top, never hidden.</summary>
    public IReadOnlyList<CatalogProblem> Problems { get; }

    public bool HasProblems => Problems.Count > 0;

    public ObservableCollection<TileGroup> Groups { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    public bool NothingFound => Groups.Count == 0;

    partial void OnSearchTextChanged(string value) => Rebuild();

    [RelayCommand]
    private void Open(TileViewModel tile) => _shell.OpenModule(tile.Id);

    [RelayCommand]
    private void OpenInTab(TileViewModel tile) => _shell.OpenModule(tile.Id, ModuleOpenMode.Tab);

    [RelayCommand]
    private void OpenInWindow(TileViewModel tile) => _shell.OpenModule(tile.Id, ModuleOpenMode.Window);

    [RelayCommand]
    private void TogglePin(TileViewModel tile)
    {
        tile.IsPinned = !tile.IsPinned;

        var favorites = _shell.Settings.Favorites;
        if (tile.IsPinned) { if (!favorites.Contains(tile.Id)) favorites.Add(tile.Id); }
        else favorites.Remove(tile.Id);

        _shell.SaveSettings();
        Rebuild();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    /// <summary>Re-evaluate probes in the background; the window stays responsive.</summary>
    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        if (IsScanning) return;
        IsScanning = true;

        try
        {
            var manifests = _all.Select(t => t.Manifest).ToList();

            // Task.Run: a probe may touch a folder on a sleeping network drive.
            var results = await Task.Run(() =>
            {
                var scanner = StatusScanner.Snapshot();
                return manifests.ToDictionary(
                    m => m.Id,
                    m => scanner.Evaluate(m),
                    StringComparer.OrdinalIgnoreCase);
            });

            foreach (var tile in _all)
                if (results.TryGetValue(tile.Id, out var status) && status is not null)
                    tile.Status = status;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Rebuild()
    {
        var query = SearchText.Trim();
        var matching = _all.Where(t => t.Matches(query)).ToList();

        Groups.Clear();

        // Pinned first, but not while searching: a search wants results in relevance order.
        var pinned = query.Length == 0 ? matching.Where(t => t.IsPinned).ToList() : [];
        if (pinned.Count > 0) Groups.Add(new TileGroup("Pinned", pinned));

        var rest = matching.Except(pinned).ToList();
        if (rest.Count > 0) Groups.Add(new TileGroup(pinned.Count > 0 ? "All tools" : "", rest));

        OnPropertyChanged(nameof(NothingFound));
    }

    /// <summary>Updates which modules are loaded and what they are doing.</summary>
    public void SyncRuntimeState(ModuleManager modules)
    {
        Dispatcher.UIThread.VerifyAccess();

        foreach (var tile in _all)
        {
            var session = modules.Find(tile.Id);
            tile.IsLoaded = session is not null;
            tile.Activity = session?.Module.Activity ?? Abstractions.ModuleActivity.Idle;
        }
    }
}

/// <param name="Name">"Pinned", "All tools", or empty for one list without a heading.</param>
public sealed record TileGroup(string Name, IReadOnlyList<TileViewModel> Tiles)
{
    /// <summary>Section label above the tiles. TextBlock has no text transform.</summary>
    public string Label => Name.ToUpperInvariant();

    public bool HasLabel => Name.Length > 0;
}
