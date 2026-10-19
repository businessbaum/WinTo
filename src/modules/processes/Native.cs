using System.Runtime.InteropServices;

namespace MultiTool.Modules.Processes;

/// <summary>The system calls, in one place.</summary>
/// <remarks>
/// <para>
/// <see cref="LibraryImport"/>, not <c>DllImport</c>: the latter raises SYSLIB1054, and warnings are
/// errors here.
/// </para>
/// <para>
/// <b>ntdll functions</b> (<c>NtSuspendProcess</c>, <c>NtTerminateProcess</c>,
/// <c>NtQueryInformationProcess</c>) aren't a promised API. They're used only where failure is harmless:
/// each call sits in a <c>try</c>, returns an NTSTATUS, and the ladder moves to the next rung on
/// failure. If one vanished in a future Windows, one rung would drop out, not the module.
/// </para>
/// <para>
/// Unlike <c>IPolicyConfig</c>, a COM interface where a shifted method order calls the wrong address
/// and kills the process, free ntdll functions are resolved by name; a missing one throws a catchable
/// <see cref="EntryPointNotFoundException"/>.
/// </para>
/// </remarks>
internal static partial class Native
{
    // ------------------------------------------------------------------ Access rights

    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessSetQuota = 0x0100;
    public const uint ProcessSuspendResume = 0x0800;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x0010_0000;

    public const uint ThreadTerminate = 0x0001;

    public const int StillActive = 259;

    // ------------------------------------------------------------------ kernel32

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, Span<char> buffer, ref uint size);

    /// <summary>The path to the executable.</summary>
    /// <remarks>
    /// Not <c>Process.MainModule.FileName</c>: that reads the module list and needs <c>PROCESS_VM_READ</c>,
    /// rarely granted for foreign processes, and fails for 32-bit processes from a 64-bit one.
    /// <c>QueryFullProcessImageName</c> only needs <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.
    /// </remarks>
    public static string? ImagePath(nint process)
    {
        Span<char> buffer = stackalloc char[1024];
        var size = (uint)buffer.Length;

        return QueryFullProcessImageName(process, 0, buffer, ref size) && size > 0
            ? new string(buffer[..(int)size])
            : null;
    }

    // ------------------------------------------------------------------ Job objects

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    public static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(nint job, uint exitCode);

    // ------------------------------------------------------------------ Toolhelp

    public const uint Th32SnapProcess = 0x0000_0002;
    public const uint Th32SnapThread = 0x0000_0004;

    /// <summary>A Toolhelp process list entry.</summary>
    /// <remarks><c>fixed char</c> instead of <c>MarshalAs(ByValTStr)</c>: LibraryImport needs blittable structs.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadEntry
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32First(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32Next(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32First(nint snapshot, ref ThreadEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32Next(nint snapshot, ref ThreadEntry entry);

    // ------------------------------------------------------------------ ntdll

    public const int ProcessBreakOnTermination = 29;
    public const int ProcessProtectionInformation = 61;

    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(nint process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtTerminateProcess(nint process, int exitStatus);

    [LibraryImport("ntdll.dll")]
    public static partial int NtTerminateThread(nint thread, int exitStatus);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(
        nint process, int informationClass, ref uint information, uint length, out uint returnLength);

    // ------------------------------------------------------------------ Requesting privileges

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges state, uint length, nint previous, nint returnLength);

    /// <summary>Enables <c>SeDebugPrivilege</c>. <c>true</c> if it is really held afterwards.</summary>
    /// <remarks>
    /// <para>
    /// <b>The biggest lever of the ladder</b>, and why <c>taskkill /f</c> in an admin console ends things
    /// Task Manager can't: with it a process may open any other with full access, whatever its ACL says
    /// (SYSTEM services, other users' processes, explicit deny entries for terminate).
    /// </para>
    /// <para>
    /// <b>The return value of <c>AdjustTokenPrivileges</c> is not the answer.</b> It reports success even if
    /// nothing was assigned; then <c>ERROR_NOT_ALL_ASSIGNED</c> is the last error. Without admin rights
    /// <c>SeDebugPrivilege</c> isn't in the token at all.
    /// </para>
    /// </remarks>
    private static bool? _debug;

    /// <summary>Requests <c>SeDebugPrivilege</c>, once per process run, result remembered.</summary>
    /// <remarks>
    /// Called already when <b>enumerating</b>, not only when killing: without it 148 of 391 processes here
    /// couldn't even be opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c> (no path, no protection status),
    /// so the list would be least informative exactly where it matters.
    /// </remarks>
    public static bool EnsureDebugPrivilege() => _debug ??= EnableDebugPrivilege();

    public static bool EnableDebugPrivilege()
    {
        var token = nint.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                return false;

            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid)) return false;

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };

            if (!AdjustTokenPrivileges(token, false, ref privileges, (uint)Marshal.SizeOf<TokenPrivileges>(),
                                       nint.Zero, nint.Zero))
                return false;

            return Marshal.GetLastPInvokeError() != ErrorNotAllAssigned;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (token != nint.Zero) CloseHandle(token);
        }
    }
}
