using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Storage.DiskUsage;

/// <summary>A drive in the bar at the top.</summary>
public sealed record DriveRow(string Root, string Label, string Detail, double Percent);

/// <summary>One segment of the path above the map.</summary>
public sealed record Crumb(string Name, DirNode Node);

/// <summary>A row of the list on the right.</summary>
public sealed record ListRow(TreemapItem Item, string Name, string SizeText, double Percent, string Category)
{
    public bool IsFolder => Item.Kind == ItemKind.Folder;
}

public sealed partial class DiskUsageViewModel : ObservableObject, IDisposable
{
    /// <summary>Rows shown in the list. The map shows everything anyway.</summary>
    private const int ListLimit = 80;

    private readonly IModuleContext _context;
    private readonly DispatcherTimer _progressTimer;
    private CancellationTokenSource? _cancellation;
    private ScanProgress? _progress;

    /// <summary>Free space of the drive, if a whole drive was read.</summary>
    private long? _free;

    public DiskUsageViewModel(IModuleContext context)
    {
        _context = context;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => ShowProgress();
    }

    /// <summary>Tells the shell what is running. Wired by the module.</summary>
    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<DriveRow> Drives { get; } = [];

    public ObservableCollection<Crumb> Breadcrumbs { get; } = [];

    public ObservableCollection<ListRow> Rows { get; } = [];

    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string HoverText { get; set; } = "";
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    /// <summary>What the map shows; a new list per folder so it re-lays out.</summary>
    [ObservableProperty] public partial IReadOnlyList<TreemapItem>? Items { get; set; }

    [ObservableProperty] public partial TreemapItem? Selected { get; set; }

    [ObservableProperty] public partial ListRow? SelectedRow { get; set; }

    [ObservableProperty] public partial string SelectionText { get; set; } = "";

    /// <summary>Whether the selection can be acted on; rest and free aren't single things.</summary>
    [ObservableProperty] public partial bool CanActOnSelection { get; set; }

    public DirNode? Root { get; private set; }

    public DirNode? Current { get; private set; }

    partial void OnSelectedChanged(TreemapItem? value)
    {
        CanActOnSelection = value is { Kind: ItemKind.Folder or ItemKind.File };
        SelectionText = value is null ? "" : Describe(value);

        // List and map show the same selection.
        var row = value is null ? null : Rows.FirstOrDefault(r => Same(r.Item, value));
        if (!ReferenceEquals(row, SelectedRow)) SelectedRow = row;
    }

    partial void OnSelectedRowChanged(ListRow? value)
    {
        if (value is not null && !Same(value.Item, Selected)) Selected = value.Item;
    }

    private static bool Same(TreemapItem a, TreemapItem? b) =>
        b is not null && a.Kind == b.Kind && ReferenceEquals(a.Folder, b.Folder)
        && ReferenceEquals(a.File, b.File) && a.Name == b.Name;

    // --------------------------------------------------------------- Drives

    /// <summary>Fixed and removable drives. Not network drives: a sleeping server would stall everything here.</summary>
    public async Task LoadDrivesAsync()
    {
        var drives = await Task.Run(() =>
        {
            var result = new List<DriveRow>();

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;

                    var total = drive.TotalSize;
                    var used = total - drive.TotalFreeSpace;
                    var name = drive.VolumeLabel is { Length: > 0 } label
                        ? $"{drive.Name.TrimEnd('\\')}  {label}"
                        : drive.Name.TrimEnd('\\');

                    result.Add(new DriveRow(drive.RootDirectory.FullName, name,
                        $"{Humanize.Bytes(used)} of {Humanize.Bytes(total)} used",
                        total > 0 ? used * 100.0 / total : 0));
                }
                catch (Exception)
                {
                    // A drive disappearing between enumerating and querying.
                }
            }

            return result;
        });

        Drives.Clear();
        foreach (var drive in drives) Drives.Add(drive);
    }

    [RelayCommand]
    private Task ScanDriveAsync(DriveRow? drive) =>
        drive is null ? Task.CompletedTask : ScanAsync(drive.Root);

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (await _context.Shell.PickFolderAsync("Folder to analyze") is { } folder) await ScanAsync(folder);
    }

    /// <summary>A folder from another module.</summary>
    public void ScanFromElsewhere(string path)
    {
        if (!IsScanning && Directory.Exists(path)) _ = ScanAsync(path);
    }

    // --------------------------------------------------------------- Reading

    private async Task ScanAsync(string path)
    {
        if (IsScanning) return;

        Cancel();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        var token = _cancellation.Token;

        _progress = new ScanProgress();
        IsScanning = true;
        Message = null;
        _progressTimer.Start();
        ShowProgress();

        try
        {
            var progress = _progress;
            var root = await Task.Run(() => DiskScanner.Scan(path, progress, token), token);

            _free = IsDriveRoot(root.FullPath) ? FreeSpace(root.FullPath) : null;
            Root = root;
            NavigateTo(root);

            HasResult = true;
            IsEmpty = false;

            if (progress.Denied > 0)
                Show($"{progress.Denied} folders without read access. Their size is missing from the total.", error: false);
        }
        catch (OperationCanceledException)
        {
            Show("Cancelled.");
        }
        catch (Exception ex)
        {
            _context.Log.Error("Disk usage could not be read.", ex);
            Show(ex.Message, error: true);
        }
        finally
        {
            _progressTimer.Stop();
            IsScanning = false;
            ProgressText = "";
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void ShowProgress()
    {
        if (_progress is not { } p) return;

        var current = p.Current is { } path ? $" · {Shorten(path)}" : "";
        ProgressText = $"{p.Files:N0} files · {Humanize.Bytes(p.Bytes)}{current}";

        ActivityChanged?.Invoke(this, new ModuleActivity(true, $"Reading {p.Files:N0} files"));
    }

    private static string Shorten(string path) => path.Length <= 70 ? path : "…" + path[^69..];

    /// <summary>Rescans the current folder, after changes made outside this module.</summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        if (IsScanning || Current is not { } current) return;

        Cancel();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        var token = _cancellation.Token;

        _progress = new ScanProgress();
        IsScanning = true;
        _progressTimer.Start();

        try
        {
            var progress = _progress;
            var fresh = await Task.Run(() => DiskScanner.Rescan(current, progress, token), token);

            if (current.Parent is { } parent)
            {
                parent.Replace(current, fresh);
            }
            else
            {
                Root = fresh;
                _free = IsDriveRoot(fresh.FullPath) ? FreeSpace(fresh.FullPath) : null;
            }

            NavigateTo(fresh);
            Show("Rescanned.");
        }
        catch (OperationCanceledException)
        {
            Show("Cancelled.");
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
        finally
        {
            _progressTimer.Stop();
            IsScanning = false;
            ProgressText = "";
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    // --------------------------------------------------------------- Navigating

    /// <summary>Double click in the map or open from the list.</summary>
    public void Open(TreemapItem item)
    {
        if (item.Kind == ItemKind.Folder) NavigateTo(item.Folder);
    }

    [RelayCommand]
    private void OpenRow(ListRow? row)
    {
        if (row is not null) Open(row.Item);
    }

    [RelayCommand]
    private void Navigate(Crumb? crumb)
    {
        if (crumb is not null) NavigateTo(crumb.Node);
    }

    [RelayCommand]
    private void Up()
    {
        if (Current?.Parent is { } parent) NavigateTo(parent);
    }

    private void NavigateTo(DirNode node)
    {
        Current = node;

        var items = TreemapItem.Of(node);

        // At the top of a drive the free space belongs in the picture; otherwise a half-empty drive
        // looks full.
        if (ReferenceEquals(node, Root) && _free is { } free && free > 0)
        {
            items.Add(new TreemapItem("Free", free, ItemKind.Free, node));
            items.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        }

        Items = items;
        Selected = null;

        Rows.Clear();
        var total = Math.Max(1, node.Size);
        foreach (var item in items.Where(i => i.Kind != ItemKind.Free).Take(ListLimit))
        {
            Rows.Add(new ListRow(item, item.Name, Humanize.Bytes(item.Size),
                item.Size * 100.0 / total, item.Category));
        }

        Breadcrumbs.Clear();
        var chain = new List<DirNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        foreach (var n in chain) Breadcrumbs.Add(new Crumb(n.Name, n));

        SummaryText = $"{Humanize.Bytes(node.Size)} · {node.FileCount:N0} files"
            + (node.Children.Count > 0 ? $" · {node.Children.Count:N0} folders" : "");
    }

    // --------------------------------------------------------------- Display

    public void Hover(TreemapItem? item) => HoverText = item is null ? "" : Describe(item);

    private string Describe(TreemapItem item)
    {
        var share = Current is { Size: > 0 } current && item.Kind != ItemKind.Free
            ? $" · {Humanize.Percent(item.Size / (double)current.Size)}"
            : "";

        var path = item.Path is { Length: > 0 } p ? $" · {p}" : "";
        return $"{item.Name} · {Humanize.Bytes(item.Size)}{share}{path}";
    }

    // --------------------------------------------------------------- Actions

    [RelayCommand]
    private void Reveal()
    {
        var path = Selected is { Path.Length: > 0 } item ? item.Path : Current?.FullPath;
        if (path is not null) _context.Shell.RevealInExplorer(path);
    }

    /// <summary>Asks for duplicates in a folder; the Storage tool switches to its duplicates section.</summary>
    public event Action<string>? DuplicatesRequested;

    /// <summary>Hands the selected folder (or the current one) to the duplicate search.</summary>
    [RelayCommand]
    private void FindDuplicates()
    {
        var folder = Selected is { Kind: ItemKind.Folder } item ? item.Folder.FullPath : Current?.FullPath;
        if (folder is not null) DuplicatesRequested?.Invoke(folder);
    }

    [RelayCommand]
    private async Task RecycleAsync()
    {
        if (Selected is not { Kind: ItemKind.Folder or ItemKind.File } item) return;

        var path = item.Path;

        if (item.Kind == ItemKind.Folder && ProtectedPaths.IsProtected(path))
        {
            Show($"\"{item.Name}\" is a protected location and is not deleted.", error: true);
            return;
        }

        var confirmed = await _context.Shell.ConfirmAsync(
            "Move to recycle bin",
            $"\"{item.Name}\" ({Humanize.Bytes(item.Size)}) goes to the recycle bin and can be restored from there.",
            "Move to recycle bin", danger: true);

        if (!confirmed) return;

        var problem = await Task.Run(() => RecycleBin.Delete([path]));
        var gone = item.Kind == ItemKind.Folder ? !Directory.Exists(path) : !File.Exists(path);

        if (!gone)
        {
            Show(problem ?? "Not moved to the recycle bin.", error: true);
            return;
        }

        // Remove from the tree instead of rescanning; the sums above follow.
        if (item.Kind == ItemKind.Folder) item.Folder.Parent?.Remove(item.Folder, null);
        else item.Folder.Remove(null, item.File);

        Show($"\"{item.Name}\" is in the recycle bin. {Humanize.Bytes(item.Size)} free once it is emptied.");

        if (Current is { } current) NavigateTo(current);
    }

    // --------------------------------------------------------------- Helpers

    private static bool IsDriveRoot(string path) =>
        Path.GetPathRoot(path) is { } root && string.Equals(
            Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path),
            StringComparison.OrdinalIgnoreCase);

    private static long? FreeSpace(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    public void Dispose()
    {
        _progressTimer.Stop();
        Cancel();

        // The tree can be dozens of MB; a binding still attached would keep it alive.
        Items = null;
        Rows.Clear();
        Breadcrumbs.Clear();
        Root = null;
        Current = null;
    }
}
