using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Storage.Duplicates;

/// <summary>What goes into the module's <c>settings.json</c>.</summary>
public sealed class FinderSettings
{
    public List<string> Folders { get; set; } = [];
    public long MinimumSizeKb { get; set; } = 1;
    public string Extensions { get; set; } = "";
    public bool IncludeHidden { get; set; }
}

public sealed partial class FileRow : ObservableObject
{
    private readonly DuplicateFinderViewModel _owner;

    public FileRow(DuplicateFile file, DuplicateFinderViewModel owner)
    {
        _owner = owner;
        File = file;
    }

    public DuplicateFile File { get; }

    public string Name => File.Name;

    public string Folder => File.Folder;

    public string ModifiedText => File.Modified.ToString("g");

    [ObservableProperty] public partial bool Selected { get; set; }

    partial void OnSelectedChanged(bool value) => _owner.RecountSelection();
}

public sealed partial class GroupRow : ObservableObject
{
    public GroupRow(DuplicateGroup group)
    {
        Size = group.Size;
        Wasted = group.Wasted;
        Files = [];
    }

    public long Size { get; }

    public long Wasted { get; private set; }

    public ObservableCollection<FileRow> Files { get; }

    public string HeaderText => $"{Files.Count} files of {Humanize.Bytes(Size)}";

    public string WastedText => $"{Humanize.Bytes(Wasted)} wasted";

    /// <summary>Count and savings of the group change after deleting.</summary>
    public void Recount()
    {
        Wasted = Size * Math.Max(0, Files.Count - 1);
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(WastedText));
    }
}

public sealed partial class DuplicateFinderViewModel : ObservableObject
{
    private readonly IModuleContext _context;
    private CancellationTokenSource? _cancellation;

    public DuplicateFinderViewModel(IModuleContext context)
    {
        _context = context;

        var settings = context.Settings.Load<FinderSettings>();
        foreach (var folder in settings.Folders) Folders.Add(folder);
        MinimumSizeKb = settings.MinimumSizeKb;
        Extensions = settings.Extensions;
        IncludeHidden = settings.IncludeHidden;

        // Avalonia can't convert int to bool in a binding ("{Binding !Folders.Count}" silently does
        // nothing), hence explicit properties tied to the collection.
        Folders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFolders));
        Groups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGroups));
    }

    public bool HasFolders => Folders.Count > 0;

    public bool HasGroups => Groups.Count > 0;

    /// <summary>Tells the shell what is running. Wired by the module.</summary>
    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<string> Folders { get; } = [];

    public ObservableCollection<GroupRow> Groups { get; } = [];

    [ObservableProperty] public partial long MinimumSizeKb { get; set; }
    [ObservableProperty] public partial string Extensions { get; set; } = "";
    [ObservableProperty] public partial bool IncludeHidden { get; set; }

    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string PhaseText { get; set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool ProgressIsKnown { get; set; }

    [ObservableProperty] public partial string SummaryText { get; set; } = "Not searched yet.";
    [ObservableProperty] public partial string SelectionText { get; set; } = "";
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }

    partial void OnMinimumSizeKbChanged(long value) => Save();
    partial void OnExtensionsChanged(string value) => Save();
    partial void OnIncludeHiddenChanged(bool value) => Save();

    // ------------------------------------------------------------------ Folders

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        foreach (var folder in await _context.Shell.PickFoldersAsync("Folders to search"))
            if (!Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                Folders.Add(folder);

        Save();
    }

    [RelayCommand]
    private void RemoveFolder(string? folder)
    {
        if (folder is null) return;
        Folders.Remove(folder);
        Save();
    }

    private void Save() => _context.Settings.Save(new FinderSettings
    {
        Folders = [.. Folders],
        MinimumSizeKb = MinimumSizeKb,
        Extensions = Extensions,
        IncludeHidden = IncludeHidden,
    });

    // ------------------------------------------------------------------ Search

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;

        if (Folders.Count == 0)
        {
            Message = "Choose a folder first.";
            return;
        }

        Message = null;
        Groups.Clear();
        IsScanning = true;
        ProgressIsKnown = false;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);

        var options = new ScanOptions
        {
            MinimumSize = Math.Max(0, MinimumSizeKb) * 1024,
            Extensions = ParseExtensions(Extensions),
            IncludeHidden = IncludeHidden,
        };

        // Progress<T> reports through the synchronization context, i.e. back to the UI thread the
        // run started from; that's why it is created here and not in the scanner.
        var progress = new Progress<ScanProgress>(OnProgress);

        try
        {
            var groups = await DuplicateScanner.ScanAsync(
                [.. Folders], options, progress, _cancellation.Token);

            Show(groups);
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Cancelled.";
        }
        catch (Exception ex)
        {
            _context.Log.Error("Duplicate search failed.", ex);
            Message = ex.Message;
            SummaryText = "Cancelled.";
        }
        finally
        {
            IsScanning = false;
            PhaseText = "";
            _cancellation?.Dispose();
            _cancellation = null;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    private void OnProgress(ScanProgress progress)
    {
        PhaseText = progress.Fraction is { } fraction
            ? $"{progress.Phase} — {Humanize.Percent(fraction)}"
            : $"{progress.Phase} — {progress.FilesSeen} files";

        ProgressIsKnown = progress.Fraction is not null;
        ProgressValue = progress.Fraction ?? 0;

        ActivityChanged?.Invoke(this, new ModuleActivity(true, PhaseText, progress.Fraction));
    }

    private void Show(IReadOnlyList<DuplicateGroup> groups)
    {
        Groups.Clear();

        foreach (var group in groups)
        {
            var row = new GroupRow(group);

            foreach (var file in group.Files) row.Files.Add(new FileRow(file, this));

            // Everything but the first file is preselected. A suggestion, not a decision: only the user
            // knows which copy is the right one.
            for (var i = 1; i < row.Files.Count; i++) row.Files[i].Selected = true;

            row.Recount();
            Groups.Add(row);
        }

        var wasted = groups.Sum(g => g.Wasted);
        var files = groups.Sum(g => g.Files.Count);

        SummaryText = groups.Count == 0
            ? "No duplicates found."
            : $"{groups.Count} groups · {files} files · {Humanize.Bytes(wasted)} reclaimable";

        // No tile status: the result isn't stored anywhere, so "11 GB duplicated" outlived the groups
        // behind it. What is running is reported via ActivityChanged, which is always true.

        RecountSelection();
    }

    private static List<string> ParseExtensions(string text) =>
        [.. text
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('*', '.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()];

    // ------------------------------------------------------------------ Selection

    /// <summary>Counts the checks across all groups. Raised by every row.</summary>
    internal void RecountSelection()
    {
        var selected = Groups.SelectMany(g => g.Files).Where(f => f.Selected).ToList();
        var bytes = selected.Sum(f => f.File.Size);

        HasSelection = selected.Count > 0;
        SelectionText = selected.Count == 0
            ? "Nothing selected"
            : $"{selected.Count} files · {Humanize.Bytes(bytes)}";
    }

    [RelayCommand]
    private void KeepFirst() => Select((_, index) => index > 0);

    [RelayCommand]
    private void KeepNewest() => SelectPerGroup(files =>
    {
        var keep = files.MaxBy(f => f.File.Modified);
        return f => !ReferenceEquals(f, keep);
    });

    [RelayCommand]
    private void KeepShortestPath() => SelectPerGroup(files =>
    {
        // The shortest path is usually the original: copies end up in subfolders, "New folder" or
        // "Copy of …", never one level up.
        var keep = files.MinBy(f => f.File.Path.Length);
        return f => !ReferenceEquals(f, keep);
    });

    [RelayCommand]
    private void SelectNone() => Select((_, _) => false);

    private void Select(Func<FileRow, int, bool> predicate)
    {
        foreach (var group in Groups)
            for (var i = 0; i < group.Files.Count; i++)
                group.Files[i].Selected = predicate(group.Files[i], i);
    }

    private void SelectPerGroup(Func<IReadOnlyList<FileRow>, Func<FileRow, bool>> chooser)
    {
        foreach (var group in Groups)
        {
            var predicate = chooser(group.Files);
            foreach (var file in group.Files) file.Selected = predicate(file);
        }
    }

    // ------------------------------------------------------------------ Deleting

    /// <summary>Is the file still the one found by the search?</summary>
    /// <remarks>
    /// Hours may pass between search and delete. A changed file is no longer a duplicate, and a file
    /// deleted elsewhere can't be the copy that remains. Size and modification time catch both without
    /// reading anything.
    /// </remarks>
    private static bool StillSame(DuplicateFile file)
    {
        try
        {
            var info = new FileInfo(file.Path);
            return info.Exists && info.Length == file.Size && info.LastWriteTime == file.Modified;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var chosen = Groups.SelectMany(g => g.Files).Where(f => f.Selected).ToList();
        if (chosen.Count == 0) return;

        // Check each group before asking: delete only unchanged files, and only if the copy that stays
        // is still unchanged. Otherwise a late delete could remove the last copy.
        var selected = new List<FileRow>();
        var skipped = 0;

        foreach (var group in Groups)
        {
            var picked = group.Files.Where(f => f.Selected).ToList();
            if (picked.Count == 0) continue;

            var keptSurvives = picked.Count == group.Files.Count
                || group.Files.Any(f => !f.Selected && StillSame(f.File));

            foreach (var row in picked)
            {
                if (keptSurvives && StillSame(row.File)) selected.Add(row);
                else skipped++;
            }
        }

        if (selected.Count == 0)
        {
            Message = "Changed since the search. Search again.";
            return;
        }

        // A group with every file checked would vanish completely. Possibly intended, almost always a
        // mistake, so ask instead of preventing it.
        var wipedGroups = Groups.Count(g => g.Files.All(f => f.Selected));
        var extra = wipedGroups == 0 ? ""
            : $"\n\nIn {wipedGroups} groups every copy is checked. Nothing remains there.";

        var bytes = selected.Sum(f => f.File.Size);

        var confirmed = await _context.Shell.ConfirmAsync(
            "Move to recycle bin",
            $"{selected.Count} files ({Humanize.Bytes(bytes)}) are moved to the recycle bin "
            + $"and can be restored from there.{extra}",
            "Move to recycle bin", danger: true);

        if (!confirmed) return;

        var problem = await Task.Run(() => RecycleBin.Delete([.. selected.Select(f => f.File.Path)]));

        // Only remove from the list what is really gone. The recycle bin can reject single files
        // without the whole operation failing, and SHFileOperation continues past a locked file.
        var removed = 0;
        var sent = selected.ToHashSet();

        foreach (var group in Groups.ToList())
        {
            foreach (var row in group.Files.ToList())
            {
                if (!sent.Contains(row) || File.Exists(row.File.Path)) continue;
                group.Files.Remove(row);
                removed++;
            }

            group.Recount();

            // A group with a single file is no longer a duplicate group.
            if (group.Files.Count < 2) Groups.Remove(group);
        }

        Message = (problem is null
            ? $"{removed} files moved to the recycle bin."
            : removed == 0
                ? problem
                : $"{removed} moved, the rest not: {problem}")
            + (skipped > 0 ? $" {skipped} skipped, changed since the search." : "");
        RecountSelection();

        var wasted = Groups.Sum(g => g.Wasted);
        SummaryText = Groups.Count == 0
            ? "No duplicates left."
            : $"{Groups.Count} groups · {Humanize.Bytes(wasted)} still reclaimable";
    }

    [RelayCommand]
    private void Reveal(FileRow? row)
    {
        if (row is not null) _context.Shell.RevealInExplorer(row.File.Path);
    }

    /// <summary>
    /// Another module sends a folder (disk usage: "Find duplicates here"). It replaces the list and
    /// the search starts right away.
    /// </summary>
    /// <remarks>
    /// Replaces rather than adds: coming from disk usage means exactly this folder. A running search
    /// is left alone.
    /// </remarks>
    public void UseFolder(string folder)
    {
        if (IsScanning || !Directory.Exists(folder)) return;

        Folders.Clear();
        Folders.Add(folder);
        Save();

        _ = ScanAsync();
    }
}
