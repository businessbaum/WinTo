using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MultiTool.Abstractions;
using MultiTool.Host.Features;
using MultiTool.Host.Services;

namespace MultiTool.Host.ViewModels;

/// <summary>A recent screenshot on the dashboard.</summary>
public sealed record RecentCapture(string Path, string Name, Bitmap? Thumbnail);

/// <summary>The start page: what runs, the state of this PC, the latest captures and the most used tools.</summary>
/// <remarks>
/// <para>
/// <b>States, not reports</b>, like the tool tiles: free space, pending updates, a pending restart are still
/// true the next time someone looks. Read when the page is shown (<see cref="RefreshAsync"/>), off the UI
/// thread; nothing here runs while the page is hidden.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject
{
    private const int RecentCount = 6;
    private const int TopToolCount = 6;

    private readonly ShellViewModel _shell;
    private bool _refreshing;

    public DashboardViewModel(ShellViewModel shell)
    {
        _shell = shell;
        // The Windows tweaks are set once and left; the dashboard shows what is used day to day.
        Features = [.. shell.Features.All.Where(f => f is not TweakFeature)];
        TopTools = [.. shell.ToolList.AllTiles.Take(TopToolCount)];
        AppsTile = shell.ToolList.AllTiles.FirstOrDefault(t => t.Id == ModuleIds.Apps);
    }

    /// <summary>
    /// The apps tool's tile. Its status is the update count, kept by the tool and refreshed by the status
    /// scan; bound directly, because the scan finishes after the dashboard was first shown.
    /// </summary>
    public TileViewModel? AppsTile { get; }

    public IReadOnlyList<Feature> Features { get; }

    public IReadOnlyList<TileViewModel> TopTools { get; }

    public ObservableCollection<RecentCapture> Recent { get; } = [];

    [ObservableProperty] public partial bool HasRecent { get; set; }

    [ObservableProperty] public partial string DiskTitle { get; set; } = "Drive";
    [ObservableProperty] public partial string DiskText { get; set; } = "";
    [ObservableProperty] public partial double DiskUsed { get; set; }
    [ObservableProperty] public partial bool DiskLow { get; set; }

    [ObservableProperty] public partial string MemoryText { get; set; } = "";
    [ObservableProperty] public partial double MemoryUsed { get; set; }

    [ObservableProperty] public partial bool RestartPending { get; set; }
    [ObservableProperty] public partial string UptimeText { get; set; } = "";

    /// <summary>When the dashboard comes into view.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;

        try
        {
            var folder = KnownFolders.Screenshots;
            var (disk, memory, restart, shots) = await Task.Run(() =>
                (ReadDisk(), ReadMemory(), ReadRestartPending(), ReadRecent(folder)));

            if (disk is { } d)
            {
                DiskTitle = $"Drive {d.Name}";
                DiskUsed = d.Total == 0 ? 0 : (double)(d.Total - d.Free) / d.Total;
                DiskText = $"{Humanize.Bytes(d.Free)} free of {Humanize.Bytes(d.Total)}";
                DiskLow = d.Free < 20L * 1024 * 1024 * 1024 || DiskUsed > 0.92;
            }

            if (memory is { } m)
            {
                MemoryUsed = m.Total == 0 ? 0 : (double)m.Used / m.Total;
                MemoryText = $"{Humanize.Bytes(m.Used)} of {Humanize.Bytes(m.Total)} in use";
            }

            RestartPending = restart;
            UptimeText = $"Running for {Humanize.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64))}";

            foreach (var old in Recent) old.Thumbnail?.Dispose();
            Recent.Clear();
            foreach (var shot in shots) Recent.Add(shot);
            HasRecent = Recent.Count > 0;
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private void OpenCapture(RecentCapture? shot)
    {
        if (shot is not null) _shell.OpenModule(ModuleIds.Capture, argument: shot.Path);
    }

    [RelayCommand]
    private void OpenScreenshotsFolder() => _shell.Api.RevealInExplorer(KnownFolders.Screenshots);

    [RelayCommand]
    private void OpenTool(TileViewModel? tile)
    {
        if (tile is not null) _shell.OpenModule(tile.Id);
    }

    [RelayCommand]
    private void OpenApps()
    {
        if (AppsTile is { } apps) _shell.OpenModule(apps.Id, argument: "updates");
    }

    [RelayCommand]
    private void OpenStorage()
    {
        if (_shell.Catalog.Find(ModuleIds.Storage) is not null) _shell.OpenModule(ModuleIds.Storage);
    }

    [RelayCommand]
    private void OpenWindowsUpdate() => _shell.Api.OpenUrl("ms-settings:windowsupdate");

    [RelayCommand]
    private void ShowFeatures() => _shell.Navigate("features");

    [RelayCommand]
    private void ShowTools() => _shell.Navigate("tools");

    // ------------------------------------------------------------------ reads

    private static (string Name, long Free, long Total)? ReadDisk()
    {
        try
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(system);
            return (drive.Name.TrimEnd('\\'), drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (long Used, long Total)? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;

        return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
    }

    /// <summary>Windows Update or servicing waits for a restart.</summary>
    private static bool ReadRestartPending()
    {
        try
        {
            using var update = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (update is not null) return true;

            using var servicing = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return servicing is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The newest screenshots, decoded small: a desktop capture is millions of pixels.</summary>
    private static List<RecentCapture> ReadRecent(string folder)
    {
        var result = new List<RecentCapture>();

        try
        {
            if (!Directory.Exists(folder)) return result;

            var files = new DirectoryInfo(folder)
                .EnumerateFiles("*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(RecentCount);

            foreach (var file in files)
            {
                Bitmap? thumbnail = null;
                try
                {
                    using var stream = file.OpenRead();
                    thumbnail = Bitmap.DecodeToHeight(stream, 140, BitmapInterpolationMode.MediumQuality);
                }
                catch (Exception)
                {
                    // An unreadable file still shows by name.
                }

                result.Add(new RecentCapture(file.FullName, file.Name, thumbnail));
            }
        }
        catch (Exception)
        {
            // An unreadable folder costs the strip, nothing else.
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
