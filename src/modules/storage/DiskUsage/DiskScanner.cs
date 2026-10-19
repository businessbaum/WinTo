using System.IO.Enumeration;

namespace MultiTool.Modules.Storage.DiskUsage;

/// <summary>A file large enough to appear on its own.</summary>
public sealed record FileLeaf(string Name, long Size);

/// <summary>A folder with everything below it.</summary>
/// <remarks>
/// <b>Not every file is kept.</b> A system drive easily has a million files; one object each would be
/// hundreds of MB for a view that shows a dozen per folder at most. Each folder keeps its
/// <see cref="KeepLargest"/> largest files individually and the rest as a sum.
/// </remarks>
public sealed class DirNode(string name, DirNode? parent)
{
    public const int KeepLargest = 10;

    public string Name { get; } = name;

    public DirNode? Parent { get; private set; } = parent;

    /// <summary>All files below, in bytes.</summary>
    public long Size { get; internal set; }

    /// <summary>Number of all files below.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Subfolders, largest first.</summary>
    public List<DirNode> Children { get; } = [];

    /// <summary>The largest files directly in this folder, largest first.</summary>
    public List<FileLeaf> LargestFiles { get; } = [];

    /// <summary>Files directly in this folder that weren't kept individually.</summary>
    public long OtherSize { get; internal set; }

    public int OtherCount { get; internal set; }

    /// <summary>Could the folder not be read? Its size is then a lower bound.</summary>
    public bool Denied { get; internal set; }

    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);

    /// <summary>Removes a file or folder from the tree and subtracts its size from all parents, after deleting, without rescanning.</summary>
    public void Remove(DirNode? child, FileLeaf? file)
    {
        long size = 0, files = 0;

        if (child is not null && Children.Remove(child))
        {
            size = child.Size;
            files = child.FileCount;
            child.Parent = null;
        }
        else if (file is not null && LargestFiles.Remove(file))
        {
            size = file.Size;
            files = 1;
        }

        for (var node = this; node is not null; node = node.Parent)
        {
            node.Size -= size;
            node.FileCount -= files;
        }
    }

    /// <summary>Replaces a subfolder with a freshly read version and adjusts the sums.</summary>
    public void Replace(DirNode old, DirNode fresh)
    {
        var index = Children.IndexOf(old);
        if (index < 0) return;

        Children[index] = fresh;
        fresh.Parent = this;
        old.Parent = null;

        var size = fresh.Size - old.Size;
        var files = fresh.FileCount - old.FileCount;

        for (var node = this; node is not null; node = node.Parent)
        {
            node.Size += size;
            node.FileCount += files;
        }

        Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
    }
}

/// <summary>What the run is doing; read by the UI, written by several threads.</summary>
public sealed class ScanProgress
{
    private long _files;
    private long _bytes;
    private long _cloud;
    private long _denied;

    /// <summary>Folders that couldn't be read; a few dozen under C:\ without admin rights.</summary>
    public long Denied => Interlocked.Read(ref _denied);

    internal void AddDenied() => Interlocked.Increment(ref _denied);

    public long Files => Interlocked.Read(ref _files);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Cloud-only files, which take no space.</summary>
    public long CloudOnly => Interlocked.Read(ref _cloud);

    public string? Current { get; set; }

    internal void Add(long files, long bytes, long cloud)
    {
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _cloud, cloud);
    }
}

/// <summary>Reads a folder tree with sizes.</summary>
/// <remarks>
/// <para>
/// <b>Via <see cref="FileSystemEnumerable{TResult}"/>, not <c>FileInfo</c>:</b> the size is already in the
/// directory entry Windows returns while enumerating; a <c>FileInfo</c> per file would ask again, seconds
/// versus minutes for a million files.
/// </para>
/// <para>
/// <b>Links are not entered, cloud folders are.</b> A junction points somewhere already counted, or
/// above, which would loop (<c>C:\Users\&lt;name&gt;</c> has several). The reparse attribute alone isn't
/// enough: the OneDrive folder and all its subfolders carry it too (<c>0x431</c>, <c>0x410</c>). A link has
/// a target, a cloud folder doesn't; that's the distinction.
/// </para>
/// <para>
/// <b>Cloud-only files don't count.</b> A OneDrive placeholder reports its full size but occupies
/// nothing; counted, OneDrive would look like the biggest consumer and deleting would free nothing.
/// </para>
/// <para>
/// <b>Hard links count more than once.</b> Windows keeps many files in <c>WinSxS</c> under two names and
/// this run sees both, so that folder looks larger than it is on disk.
/// </para>
/// </remarks>
internal static class DiskScanner
{
    /// <summary>Cloud only, fetched on read.</summary>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,

        // Skip nothing: hidden and system files take space too. Read below decides what counts.
        AttributesToSkip = 0,
    };

    private readonly record struct Entry(string Name, bool IsDirectory, FileAttributes Attributes, long Length);

    public static DirNode Scan(string root, ScanProgress progress, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(root);

        // A drive keeps its backslash ("C:\"), everything else loses it.
        var name = Path.GetPathRoot(full) is { } drive && string.Equals(drive, full, StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.TrimEndingDirectorySeparator(full);

        var node = new DirNode(name, null);
        Read(node, full, progress, cancellationToken, parallelDepth: 1);
        return node;
    }

    /// <summary>Reads a folder again, for "Rescan" and after deleting.</summary>
    public static DirNode Rescan(DirNode old, ScanProgress progress, CancellationToken cancellationToken)
    {
        var fresh = new DirNode(old.Name, null);
        Read(fresh, old.FullPath, progress, cancellationToken, parallelDepth: 1);
        return fresh;
    }

    private static void Read(DirNode node, string path, ScanProgress progress, CancellationToken cancellationToken, int parallelDepth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Current = path;

        var subdirectories = new List<(DirNode Node, string Path)>();
        long files = 0, bytes = 0, cloud = 0;

        try
        {
            var entries = new FileSystemEnumerable<Entry>(path,
                static (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Attributes, e.Length),
                Options);

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    var childPath = Path.Combine(path, entry.Name);
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(childPath)) continue;

                    subdirectories.Add((new DirNode(entry.Name, node), childPath));
                    continue;
                }

                if ((entry.Attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0)
                {
                    cloud++;
                    continue;
                }

                files++;
                bytes += entry.Length;
                Keep(node, new FileLeaf(entry.Name, entry.Length));
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.Denied = true;
            progress.AddDenied();
        }
        catch (IOException)
        {
            // A drive disappearing right now, a network path without connection.
            node.Denied = true;
            progress.AddDenied();
        }

        node.Size = bytes;
        node.FileCount = files;
        progress.Add(files, bytes, cloud);

        if (parallelDepth > 0 && subdirectories.Count > 1)
        {
            // Parallel only at the top level, with four readers. Measured on a user profile (987,000
            // files): a second parallel level gave 9.4 → 8.1 s warm, while cold it was 36.6 s; the time
            // goes to the disk, not the CPU. More readers gain little on an SSD and hurt a spinning disk.
            Parallel.ForEach(subdirectories,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), CancellationToken = cancellationToken },
                child => Read(child.Node, child.Path, progress, cancellationToken, parallelDepth - 1));
        }
        else
        {
            foreach (var child in subdirectories) Read(child.Node, child.Path, progress, cancellationToken, 0);
        }

        foreach (var (child, _) in subdirectories)
        {
            node.Children.Add(child);
            node.Size += child.Size;
            node.FileCount += child.FileCount;
        }

        node.Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        node.Children.TrimExcess();
    }

    /// <summary>Is this reparse point a link: junction, symbolic link, mount point?</summary>
    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception)
        {
            // When in doubt, don't enter: a skipped link costs a number, an entered one maybe a loop.
            return true;
        }
    }

    /// <summary>Keeps the largest files individually, the rest as a sum.</summary>
    private static void Keep(DirNode node, FileLeaf file)
    {
        var list = node.LargestFiles;

        if (list.Count < DirNode.KeepLargest)
        {
            Insert(list, file);
            return;
        }

        var smallest = list[^1];
        if (file.Size <= smallest.Size)
        {
            node.OtherSize += file.Size;
            node.OtherCount++;
            return;
        }

        list.RemoveAt(list.Count - 1);
        node.OtherSize += smallest.Size;
        node.OtherCount++;
        Insert(list, file);
    }

    private static void Insert(List<FileLeaf> list, FileLeaf file)
    {
        var index = list.FindIndex(f => f.Size < file.Size);
        if (index < 0) list.Add(file);
        else list.Insert(index, file);
    }
}
