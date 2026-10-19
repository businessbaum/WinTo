using System.Diagnostics;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Processes;

/// <summary>How strongly Windows protects a process from user mode.</summary>
public enum Protection
{
    /// <summary>Normal. Can be ended with sufficient rights.</summary>
    None,

    /// <summary>PPL: antivirus, anticheat, LSA. Can't be ended from user mode.</summary>
    Light,

    /// <summary>PP: the core of the system. Even less so.</summary>
    Full,
}

/// <param name="ParentId">
/// From Toolhelp. <b>Don't trust it blindly:</b> Windows reuses process ids, so a long-gone parent's
/// number may belong to an unrelated process now. <see cref="ProcessList.Descendants"/> also checks the
/// start time.
/// </param>
/// <param name="Cpu">Share of one core since the last query, 0…1. <c>null</c> on the first frame.</param>
public sealed record ProcessInfo(
    int Id,
    int ParentId,
    string Name,
    string? Title,
    string? Path,
    long WorkingSet,
    int Threads,
    DateTime? Started,
    bool HasWindow,
    Protection Protection,
    int Signer,
    bool IsCritical,
    TimeSpan? Processor,
    bool Inspectable)
{
    public double? Cpu { get; set; }

    public bool IsProtected => Protection is not Protection.None;

    public string MemoryText => Humanize.Bytes(WorkingSet);

    public string CpuText => Cpu is { } value ? $"{value * 100:0.0} %" : "–";

    public string StartedText => Started is { } when ? Humanize.Ago(when) : "";

    /// <summary>"Application" means: has a window. Everything else is background.</summary>
    public bool IsApplication => HasWindow;

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title!;

    /// <summary>What is shown in the row below the name.</summary>
    public string Detail => Path is { Length: > 0 } path ? path : $"PID {Id}";

    public string ProtectionText => Protection switch
    {
        Protection.Light => $"protected (PPL, {SignerName})",
        Protection.Full => $"protected (PP, {SignerName})",
        _ => Inspectable ? "" : "not inspectable",
    };

    /// <summary>The names from <c>PS_PROTECTED_SIGNER</c>. They say <i>who</i> protects.</summary>
    private string SignerName => Signer switch
    {
        1 => "Authenticode",
        2 => "CodeGen",
        3 => "Antimalware",
        4 => "LSA",
        5 => "Windows",
        6 => "WinTcb",
        7 => "WinSystem",
        8 => "App",
        _ => "unknown",
    };
}

/// <summary>Enumerates running processes. <b>Belongs on a background thread.</b></summary>
/// <remarks>
/// Three sources, none enough alone: <see cref="Process.GetProcesses()"/> for name, memory and window,
/// Toolhelp for the parent (unknown to .NET), and one <c>OpenProcess</c> per process for path and
/// protection. The third is the costliest and most important: without it, a protected process would
/// only show it couldn't be ended after the failed attempt.
/// </remarks>
internal static class ProcessList
{
    public static List<ProcessInfo> Read()
    {
        // Before enumerating, not only when ending: without the privilege it stays unknown whether a
        // SYSTEM service is protected. Unelevated the call fails at no cost.
        Native.EnsureDebugPrivilege();

        var parents = ReadParents();
        var windows = WindowList.MainWindows();
        var result = new List<ProcessInfo>(400);
        var alive = new HashSet<(int, DateTime)>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    result.Add(Describe(process, parents, windows, alive));
                }
                catch (Exception)
                {
                    // A process ending between enumerating and querying is normal with 350 running programs.
                }
            }
        }

        // Whatever no longer runs leaves the cache, or it would grow with every short-lived process.
        foreach (var key in Inspected.Keys)
            if (!alive.Contains(key)) Inspected.TryRemove(key, out _);

        return result;
    }

    /// <summary>Path, protection and critical flag per running process, by id <b>and</b> start time.</summary>
    /// <remarks>
    /// <para>
    /// None of it changes while a process runs; until 0.9.0 it was still queried every two seconds for
    /// ~400 processes. Now only for new ones.
    /// </para>
    /// <para>
    /// The start time belongs in the key because Windows reuses ids. Processes without a readable start
    /// time (the uninspectable ones) aren't cached; OpenProcess fails for them right away anyway.
    /// </para>
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Id, DateTime Started), Inspection> Inspected = new();

    private sealed record Inspection(string? Path, Protection Protection, int Signer, bool Critical, bool Inspectable);

    private static ProcessInfo Describe(
        Process process,
        IReadOnlyDictionary<int, (int Parent, int Threads)> parents,
        IReadOnlyDictionary<int, string> windows,
        HashSet<(int, DateTime)> alive)
    {
        var id = process.Id;

        // From one window enumeration per pass. Process.MainWindowHandle re-enumerates every window
        // on each call: 28 ms per refresh measured with 460 processes, just for that.
        var hasWindow = windows.TryGetValue(id, out var title);

        DateTime? started = null;
        try { started = process.StartTime; }
        catch (Exception) { }

        long memory = 0;
        try { memory = process.WorkingSet64; }
        catch (Exception) { }

        // CPU time used, not load: load comes from the difference of two queries, so it belongs in
        // the view model.
        TimeSpan? processor = null;
        try { processor = process.TotalProcessorTime; }
        catch (Exception) { }

        Inspection inspection;
        if (started is { } start)
        {
            alive.Add((id, start));
            inspection = Inspected.GetOrAdd((id, start), static key => Inspect(key.Id));
        }
        else
        {
            inspection = Inspect(id);
        }

        var (path, protection, signer, critical, inspectable) = inspection;

        parents.TryGetValue(id, out var relation);

        return new ProcessInfo(
            Id: id,
            ParentId: relation.Parent,
            Name: process.ProcessName,
            Title: title,
            Path: path,
            WorkingSet: memory,
            Threads: relation.Threads,
            Started: started,
            HasWindow: hasWindow,
            Protection: protection,
            Signer: signer,
            IsCritical: critical,
            Processor: processor,
            Inspectable: inspectable);
    }

    /// <summary>Path, protection status and whether Windows crashes on ending it.</summary>
    /// <remarks>All three from <b>one</b> handle; separate OpenProcess calls would be a thousand system calls per refresh.</remarks>
    private static Inspection Inspect(int id)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)id);

        // Can't be opened means "no information", not "normal". That's the whole point: unelevated,
        // 148 of 391 processes here couldn't be opened and silently showed as unprotected, every
        // SYSTEM service among them.
        if (handle == nint.Zero) return new Inspection(null, Protection.None, 0, false, false);

        try
        {
            var path = Native.ImagePath(handle);

            var protection = Protection.None;
            var signer = 0;

            try
            {
                // PS_PROTECTION is a single byte: lower three bits the type, bits 4 to 7 the signer. Hence
                // length 1, not 4; the wrong length returns STATUS_INFO_LENGTH_MISMATCH and writes nothing.
                uint value = 0;
                if (Native.NtQueryInformationProcess(
                        handle, Native.ProcessProtectionInformation, ref value, 1, out _) == 0)
                {
                    protection = (value & 0x07) switch
                    {
                        1 => Protection.Light,
                        2 => Protection.Full,
                        _ => Protection.None,
                    };

                    signer = (int)((value >> 4) & 0x0F);
                }
            }
            catch (Exception)
            {
                // Without the function the process counts as unprotected; ending it then fails with "access
                // denied" instead of the better explanation.
            }

            var critical = false;

            try
            {
                uint breakOnTermination = 0;
                if (Native.NtQueryInformationProcess(
                        handle, Native.ProcessBreakOnTermination, ref breakOnTermination, 4, out _) == 0)
                {
                    critical = breakOnTermination != 0;
                }
            }
            catch (Exception)
            {
            }

            return new Inspection(path, protection, signer, critical, true);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Id to parent and thread count, from Toolhelp since .NET doesn't know it.</summary>
    private static Dictionary<int, (int Parent, int Threads)> ReadParents()
    {
        var result = new Dictionary<int, (int, int)>(400);
        var snapshot = nint.Zero;

        try
        {
            snapshot = Native.CreateToolhelp32Snapshot(Native.Th32SnapProcess, 0);
            if (snapshot == nint.Zero || snapshot == -1) return result;

            var entry = new Native.ProcessEntry();
            unsafe { entry.Size = (uint)sizeof(Native.ProcessEntry); }

            if (!Native.Process32First(snapshot, ref entry)) return result;

            do
            {
                result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, (int)entry.Threads);
            }
            while (Native.Process32Next(snapshot, ref entry));
        }
        catch (Exception)
        {
            // Without Toolhelp the parent is missing; the list still works, tree kill falls back to the
            // single process.
        }
        finally
        {
            if (snapshot != nint.Zero && snapshot != -1) Native.CloseHandle(snapshot);
        }

        return result;
    }

    /// <summary>All descendants of a process, leaves first.</summary>
    /// <remarks>
    /// <para>
    /// <b>The start time decides too, not just the parent id.</b> When a parent ends and its number goes to a
    /// new process, the old children still claim that parent, and a tree kill would take unrelated
    /// programs. A child can't start before its parent; that filters exactly this case.
    /// </para>
    /// <para>Leaves first, so an intermediate isn't gone when its children's turn comes.</para>
    /// </remarks>
    public static List<ProcessInfo> Descendants(ProcessInfo root, IReadOnlyList<ProcessInfo> all)
    {
        var byParent = all
            .Where(p => p.Id != p.ParentId && p.Id > 4)
            .GroupBy(p => p.ParentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ProcessInfo>();
        var seen = new HashSet<int> { root.Id };

        Collect(root);
        result.Reverse();
        return result;

        void Collect(ProcessInfo parent)
        {
            if (!byParent.TryGetValue(parent.Id, out var children)) return;

            foreach (var child in children)
            {
                if (!seen.Add(child.Id)) continue;

                if (parent.Started is { } parentStart && child.Started is { } childStart
                    && childStart < parentStart)
                {
                    // Reused id. Not this child.
                    continue;
                }

                result.Add(child);
                Collect(child);
            }
        }
    }
}

/// <summary>The main window per process, from a single enumeration.</summary>
/// <remarks>Same rule as <see cref="Process.MainWindowHandle"/>: the first visible unowned window in Z-order.</remarks>
internal static unsafe partial class WindowList
{
    private const uint GwOwner = 4;

    /// <summary>Id to window title. An entry means the process has a main window.</summary>
    public static Dictionary<int, string> MainWindows()
    {
        var result = new Dictionary<int, string>();
        var state = System.Runtime.InteropServices.GCHandle.Alloc(result);

        try
        {
            EnumWindows(&Visit, System.Runtime.InteropServices.GCHandle.ToIntPtr(state));
        }
        catch (Exception)
        {
            // Without a window list no process counts as an application.
        }
        finally
        {
            state.Free();
        }

        return result;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int Visit(nint window, nint state)
    {
        try
        {
            if (IsWindowVisible(window) == 0 || GetWindow(window, GwOwner) != 0) return 1;

            GetWindowThreadProcessId(window, out var pid);

            var map = (Dictionary<int, string>)System.Runtime.InteropServices.GCHandle.FromIntPtr(state).Target!;
            if (map.ContainsKey((int)pid)) return 1;

            map[(int)pid] = Title(window);
        }
        catch (Exception)
        {
            // An exception must not cross back into user32.
        }

        return 1;
    }

    private static string Title(nint window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0) return "";

        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            var read = GetWindowTextW(window, text, buffer.Length);
            return new string(text, 0, Math.Max(0, read));
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint window);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint window);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(nint window, char* text, int max);
}
