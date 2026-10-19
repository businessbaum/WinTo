using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiTool.Modules.SystemOverview;

/// <summary>What doesn't change while the computer runs.</summary>
/// <param name="Windows">"Windows 11 Pro 24H2 (Build 26200.1234)".</param>
/// <param name="Processor">Vendor string plus core count.</param>
/// <param name="Graphics">Name of the first display adapter, or <c>null</c>.</param>
/// <param name="TotalMemoryBytes">Physical memory.</param>
public sealed record MachineInfo(
    string ComputerName,
    string UserName,
    string Windows,
    string Processor,
    string? Graphics,
    ulong TotalMemoryBytes);

/// <summary>What changes every second.</summary>
/// <param name="CpuLoad">0…1, averaged over all cores.</param>
public sealed record MachineLoad(
    double CpuLoad,
    ulong UsedMemoryBytes,
    ulong TotalMemoryBytes,
    TimeSpan Uptime)
{
    public double MemoryLoad => TotalMemoryBytes == 0 ? 0 : (double)UsedMemoryBytes / TotalMemoryBytes;
}

/// <summary>Machine data via Win32 instead of WMI.</summary>
/// <remarks>
/// <para>
/// A first <c>Win32_OperatingSystem</c> query costs 200 ms to several seconds depending on the WMI
/// repository, and this module polls every second. The kernel API returns the three values instantly.
/// </para>
/// <para>
/// <b>CPU load is a difference, not a reading:</b> Windows keeps three total counters (idle, kernel,
/// user). The percentage comes from two queries and the time between; the first call returns 0.
/// </para>
/// </remarks>
internal static partial class Machine
{
    private static long _lastIdle, _lastKernel, _lastUser;

    public static MachineInfo Read()
    {
        var status = MemoryStatus();

        return new MachineInfo(
            ComputerName: Environment.MachineName,
            UserName: Environment.UserName,
            Windows: WindowsName(),
            Processor: ProcessorName(),
            Graphics: GraphicsName(),
            TotalMemoryBytes: status.ullTotalPhys);
    }

    public static MachineLoad ReadLoad()
    {
        var status = MemoryStatus();

        return new MachineLoad(
            CpuLoad: CpuLoad(),
            UsedMemoryBytes: status.ullTotalPhys - status.ullAvailPhys,
            TotalMemoryBytes: status.ullTotalPhys,
            Uptime: TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>Share of the time since the last call the CPU wasn't idle.</summary>
    private static double CpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        var idleDelta = idle - _lastIdle;
        var kernelDelta = kernel - _lastKernel;
        var userDelta = user - _lastUser;

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        // kernelTime already includes idle time, so total is kernel + user, not kernel + user + idle.
        var total = kernelDelta + userDelta;
        if (total <= 0) return 0;

        return Math.Clamp(1.0 - (double)idleDelta / total, 0, 1);
    }

    private static string WindowsName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        if (key is null) return RuntimeInformation.OSDescription;

        var product = key.GetValue("ProductName") as string ?? "Windows";
        var display = key.GetValue("DisplayVersion") as string;
        var build = key.GetValue("CurrentBuildNumber") as string ?? "";
        var revision = key.GetValue("UBR") as int?;

        // Windows 11 still registers as "Windows 10" here; the rename happened only in the UI. From
        // build 22000 it's 11; naming the user's own OS wrong would cost credibility at the first line.
        if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);

        var version = display is null ? "" : $" {display}";
        var full = revision is null ? build : $"{build}.{revision}";

        return $"{product}{version}  (Build {full})";
    }

    private static string ProcessorName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

        var name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
        var cores = Environment.ProcessorCount;

        return name is null or "" ? $"{cores} logical processors"
                                  : $"{name}  ·  {cores} logical processors";
    }

    /// <summary>Display adapter name from the "Display" device class.</summary>
    /// <remarks>
    /// The GUID is the fixed class GUID for display adapters, with one numbered subkey per card. The first
    /// with a <c>DriverDesc</c> is taken; on a laptop with two cards, the one Windows enumerates first.
    /// </remarks>
    private static string? GraphicsName()
    {
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");

            if (display is null) return null;

            foreach (var name in display.GetSubKeyNames())
            {
                if (!name.All(char.IsAsciiDigit)) continue;

                using var adapter = display.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string description && description.Length > 0)
                    return description;
            }
        }
        catch (Exception)
        {
            // A missing adapter name is an empty line, no reason to stop.
        }

        return null;
    }

    private static MemoryStatusEx MemoryStatus()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref status);
        return status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
