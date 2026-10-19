using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiTool.Abstractions;

/// <summary>A power plan.</summary>
public sealed record PowerPlan(Guid Id, string Name)
{
    public string IdText => Id.ToString();
}

/// <summary>
/// Reads and switches power plans via <c>powrprof.dll</c>, without <c>powercfg</c>. In the contract since 0.10.0.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the game mode module and the shell's control center.
/// </para>
/// <para>
/// Switching needs no rights; creating does (<c>PowerDuplicateScheme</c> writes under HKLM
/// and fails with error 5 unelevated).
/// </para>
/// <para>
/// The Windows 11 power mode slider (overlay schemes on Modern Standby devices) is not
/// supported: <c>PowerSetActiveOverlayScheme</c> is undocumented.
/// </para>
/// </remarks>
public static unsafe partial class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>
    /// The "Ultimate Performance" template. Not in the list; adding it creates a copy with its
    /// own id and the same name.
    /// </summary>
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;

    public static List<PowerPlan> All()
    {
        var result = new List<PowerPlan>();
        var buffer = stackalloc byte[16];

        for (uint index = 0; ; index++)
        {
            uint size = 16;
            var error = PowerEnumerate(0, 0, 0, AccessScheme, index, buffer, ref size);
            if (error == ErrorNoMoreItems || error != 0) break;

            var id = *(Guid*)buffer;
            result.Add(new PowerPlan(id, NameOf(id) ?? id.ToString()));
        }

        return result;
    }

    public static Guid? Active()
    {
        if (PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0) return null;

        try
        {
            return *(Guid*)pointer;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? Activate(Guid id)
    {
        var error = PowerSetActiveScheme(0, &id);
        return error == 0 ? null : $"Windows reported error {error}.";
    }

    /// <summary>Display name of a plan, including templates that are not in the list.</summary>
    public static string? NameOf(Guid id)
    {
        uint size = 0;
        if (PowerReadFriendlyName(0, &id, 0, 0, null, ref size) != 0 || size < 2) return null;

        var buffer = new byte[size];
        fixed (byte* bytes = buffer)
        {
            if (PowerReadFriendlyName(0, &id, 0, 0, bytes, ref size) != 0) return null;
            return new string((char*)bytes).TrimEnd('\0');
        }
    }

    /// <summary>Creates a copy of the "Ultimate Performance" template.</summary>
    /// <returns>The new id, or why it failed.</returns>
    public static (Guid? Id, string? Problem) AddUltimate()
    {
        var source = UltimateTemplate;
        nint created = 0;

        var error = PowerDuplicateScheme(0, &source, &created);
        if (error != 0)
        {
            return (null, error == 5
                ? "Requires administrator rights."
                : $"Windows reported error {error}.");
        }

        try
        {
            return (*(Guid*)created, null);
        }
        finally
        {
            LocalFree(created);
        }
    }

    /// <summary>The best plan for gaming: a copy of "Ultimate Performance", else "High performance".</summary>
    /// <remarks>The copy has a random id; it is recognized by the template's localized name.</remarks>
    public static PowerPlan? BestForGames(IReadOnlyList<PowerPlan> plans)
    {
        var ultimate = NameOf(UltimateTemplate);

        return plans.FirstOrDefault(p => ultimate is not null && p.Name == ultimate)
            ?? plans.FirstOrDefault(p => p.Id == HighPerformance);
    }

    /// <summary>Is there a copy of "Ultimate Performance"?</summary>
    public static bool HasUltimate(IReadOnlyList<PowerPlan> plans) =>
        NameOf(UltimateTemplate) is { } name && plans.Any(p => p.Name == name);

    // ------------------------------------------------------------------ Battery

    /// <summary>Is the machine running on battery?</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var status) && status.AcLineStatus == 0;

    // ------------------------------------------------------------------ Windows Game Mode

    private const string GameBarKey = @"Software\Microsoft\GameBar";

    /// <summary>Windows Game Mode (Settings › Gaming). A missing value means on, as shipped.</summary>
    public static bool WindowsGameMode
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(GameBarKey);
                return key?.GetValue("AutoGameModeEnabled") is not int value || value != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? SetWindowsGameMode(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GameBarKey, writable: true);
            key.SetValue("AutoGameModeEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(nint rootKey, nint scheme, nint subGroup, uint access, uint index, byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint rootKey, out nint activeScheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint rootKey, Guid* scheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(nint rootKey, Guid* scheme, nint subGroup, nint setting, byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerDuplicateScheme(nint rootKey, Guid* source, nint* destination);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
