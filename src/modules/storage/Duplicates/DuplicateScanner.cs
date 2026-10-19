using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MultiTool.Modules.Storage.Duplicates;

/// <param name="Path">Full path.</param>
/// <param name="Size">Size in bytes. Equal for all files in a group.</param>
public sealed record DuplicateFile(string Path, long Size, DateTime Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>Files with identical content.</summary>
public sealed record DuplicateGroup(IReadOnlyList<DuplicateFile> Files)
{
    public long Size => Files[0].Size;

    /// <summary>What would be freed if one file of this group remained.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

/// <param name="Phase">What is running, shown as is in the footer.</param>
/// <param name="Fraction">0…1, or <c>null</c> while the total is unknown.</param>
public sealed record ScanProgress(string Phase, double? Fraction, int FilesSeen);

public sealed class ScanOptions
{
    public long MinimumSize { get; init; } = 1024;

    /// <summary>Empty means all. Otherwise extensions without dot, lowercase.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    public bool IncludeHidden { get; init; }
}

/// <summary>Finds files with identical content in three passes.</summary>
/// <remarks>
/// <para>
/// Hashing every file reads every byte; on 200 GB of video that is half an hour of disk work for an
/// answer that is usually "no duplicates". So three passes, each more expensive than the last and
/// each applied only to what the previous one left:
/// </para>
/// <list type="number">
///   <item><b>Size.</b> Free, it's in the directory entry; removes over 90 % in practice.</item>
///   <item><b>The first 64 KB.</b> One read per file; files that differ at all almost always differ
///   in their header.</item>
///   <item><b>The full content.</b> Only for what is left; confirms rather than assumes.</item>
/// </list>
/// <para>
/// SHA-256, not something faster: collisions in non-cryptographic checksums can be constructed, and
/// this module's answer is a delete suggestion. SHA-256 runs in hardware and outpaces the disk.
/// </para>
/// </remarks>
internal static partial class DuplicateScanner
{
    private const int HeadBytes = 64 * 1024;

    /// <summary>Cloud only, fetched on read (OneDrive "online-only").</summary>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Fetched on open, the older form of the same.</summary>
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    public static async Task<List<DuplicateGroup>> ScanAsync(
        IReadOnlyList<string> folders,
        ScanOptions options,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken)
    {
        var files = await Task.Run(() => Collect(folders, options, progress, cancellationToken), cancellationToken);

        progress.Report(new ScanProgress("Grouping by size", null, files.Count));

        // First pass. Groups of size 1 drop out here and are never read.
        var bySize = files
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        var byHead = await HashPassAsync(
            bySize, progress, "Comparing beginnings", HeadBytes, cancellationToken);

        var byFull = await HashPassAsync(
            [.. byHead.Select(x => x.File)], progress, "Comparing contents", null, cancellationToken);

        // Largest savings first: the order someone reads the list in when they need space.
        return [.. byFull
            .GroupBy(f => f.Key)
            .Select(g => WithoutHardLinks([.. g.Select(x => x.File)]))
            .Where(files => files.Count > 1)
            .Select(files => new DuplicateGroup([.. files.OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase)]))
            .OrderByDescending(g => g.Wasted)];
    }

    /// <summary>Keeps only one path per physical file.</summary>
    /// <remarks>
    /// Two hard links are two names for the <b>same</b> data; until 0.9.0 they showed up as duplicates
    /// with "wasted" space that deleting never frees. Detected by volume serial and NTFS file index.
    /// </remarks>
    private static List<DuplicateFile> WithoutHardLinks(List<DuplicateFile> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DuplicateFile>(files.Count);

        foreach (var file in files)
        {
            // No id (not NTFS, no access): the file counts as separate.
            if (Identity(file.Path) is { } id && !seen.Add(id)) continue;
            result.Add(file);
        }

        return result;
    }

    private static string? Identity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return GetFileInformationByHandle(handle, out var info)
                ? $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}"
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <remarks>
    /// <c>Pack = 4</c> because FILETIME is two DWORDs aligned to four bytes; with <c>long</c> everything after
    /// the first timestamp would shift by four bytes.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>One hash pass over the candidates, grouped by result.</summary>
    /// <param name="limit">How many bytes to read. <c>null</c> means everything.</param>
    private static async Task<List<(string Key, DuplicateFile File)>> HashPassAsync(
        IReadOnlyList<DuplicateFile> candidates,
        IProgress<ScanProgress> progress,
        string phase,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return [];

        var hashed = new System.Collections.Concurrent.ConcurrentBag<(string Key, DuplicateFile File)>();
        var done = 0;

        await Task.Run(() =>
        {
            // Four at a time, not one per core: more readers gain nothing on an SSD and hurt a spinning
            // disk, whose head would jump between files. Hashing is never the bottleneck.
            var parallelism = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                CancellationToken = cancellationToken,
            };

            Parallel.ForEach(candidates, parallelism, file =>
            {
                var hash = Hash(file.Path, limit);

                // An unreadable file gets no key and drops out; calling it unique would be a claim about
                // something never read.
                if (hash is not null)
                {
                    // The size belongs in the key: otherwise a head pass would lump together all files shorter
                    // than 64 KB that happen to start the same.
                    hashed.Add(($"{file.Size}:{hash}", file));
                }

                var count = Interlocked.Increment(ref done);
                if (count % 16 == 0 || count == candidates.Count)
                    progress.Report(new ScanProgress(phase, (double)count / candidates.Count, candidates.Count));
            });
        }, cancellationToken);

        // Only keys occurring more than once go to the next pass.
        return [.. hashed
            .GroupBy(x => x.Key)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)];
    }

    private static string? Hash(string path, int? limit)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 16, FileOptions.SequentialScan);

            if (limit is null) return Convert.ToHexString(SHA256.HashData(stream));

            var buffer = new byte[limit.Value];
            var read = stream.ReadAtLeast(buffer, limit.Value, throwOnEndOfStream: false);

            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception)
        {
            // Locked, deleted, no access: all happen, none is a reason to stop the run.
            return null;
        }
    }

    private static List<DuplicateFile> Collect(
        IReadOnlyList<string> folders,
        ScanOptions options,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken)
    {
        var files = new List<DuplicateFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,

            // Otherwise the run stops at the first system file the user has no rights for.
            IgnoreInaccessible = true,

            // Don't follow reparse points, or the search loops through a junction (e.g.
            // C:\Users\<name>\Application Data).
            //
            // Also skip cloud-only files: hashing means reading, and OneDrive would download every one.
            // ReparsePoint already catches them today (a OneDrive placeholder carries 0x400), but Windows can
            // hide placeholders from a process (RtlSetProcessPlaceholderCompatibilityMode); the explicit
            // attributes hold even then.
            AttributesToSkip = FileAttributes.ReparsePoint
                | FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen
                | (options.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System),
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (var path in Directory.EnumerateFiles(folder, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Overlapping selected folders would find every file twice and report it as its own duplicate.
                if (!seen.Add(path)) continue;

                try
                {
                    var info = new FileInfo(path);
                    if (info.Length < options.MinimumSize) continue;

                    if (options.Extensions.Count > 0)
                    {
                        var extension = info.Extension.TrimStart('.').ToLowerInvariant();
                        if (!options.Extensions.Contains(extension)) continue;
                    }

                    files.Add(new DuplicateFile(path, info.Length, info.LastWriteTime));
                }
                catch (Exception)
                {
                    // as above
                }

                if (files.Count % 500 == 0)
                    progress.Report(new ScanProgress("Collecting files", null, files.Count));
            }
        }

        progress.Report(new ScanProgress("Collecting files", null, files.Count));
        return files;
    }
}
