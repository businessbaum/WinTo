using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace MultiTool.Host.Services;

/// <summary>The few machine-wide policies features may set, and the elevated helper that writes them.</summary>
/// <remarks>
/// <para>
/// <b>A fixed list, by id.</b> The helper runs elevated with a command line any program could have written;
/// it accepts an id from this list and "on" or "off", never a key or a value.
/// </para>
/// <para>
/// <b>A plain static class</b>, apart from the features: <c>Program.Main</c> calls <see cref="RunHelper"/>,
/// and a call into a feature class would load its base types (the MVVM toolkit) on every start. Measured:
/// one assembly more before the first module (30 instead of 29).
/// </para>
/// </remarks>
internal static class MachinePolicy
{
    public const string HelperSwitch = "--machine-policy";

    /// <summary>"No auto-restart with logged on users".</summary>
    public const string NoAutoRestart = "no-auto-restart";

    /// <summary>"Allow widgets" off: no widgets button, no widgets board. Windows refuses <c>TaskbarDa</c> from other programs.</summary>
    public const string NoWidgets = "no-widgets";

    private sealed record Policy(string Key, string Name, int On);

    private static readonly Dictionary<string, Policy> Known = new(StringComparer.Ordinal)
    {
        [NoAutoRestart] = new(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1),
        [NoWidgets] = new(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0),
    };

    private const string PendingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

    /// <summary>Whether the policy is set as the feature wants it. Readable without rights.</summary>
    public static bool IsSet(string id)
    {
        if (!Known.TryGetValue(id, out var policy)) return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(policy.Key);
            return key?.GetValue(policy.Name) is int value && value == policy.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether Windows waits for a restart to finish installing updates.</summary>
    public static bool RestartPending()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PendingKey);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sets or removes the policy: directly when elevated, otherwise through one administrator prompt.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static async Task<string?> SetAsync(string id, bool on)
    {
        if (Elevation.IsElevated) return Write(id, on) ? null : "The policy could not be written.";
        if (Environment.ProcessPath is not { } exe) return "WinTo's path is unknown.";

        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(HelperSwitch);
        info.ArgumentList.Add(id);
        info.ArgumentList.Add(on ? "on" : "off");

        try
        {
            using var process = Process.Start(info);
            if (process is null) return "The administrator prompt didn't open.";

            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : $"The policy could not be written (code {process.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The administrator prompt was declined.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool Write(string id, bool on)
    {
        if (!Known.TryGetValue(id, out var policy)) return false;

        try
        {
            if (on)
            {
                using var key = Registry.LocalMachine.CreateSubKey(policy.Key, writable: true);
                key.SetValue(policy.Name, policy.On, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(policy.Key, writable: true);
                key?.DeleteValue(policy.Name, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><c>--machine-policy &lt;id&gt; on|off</c>, in the elevated helper process.</summary>
    /// <returns>The exit code: 0 done, 2 refused, 1 failed.</returns>
    public static int RunHelper(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, HelperSwitch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length || !Elevation.IsElevated) return 2;

        var id = args[index + 1];
        if (!Known.ContainsKey(id)) return 2;

        return args[index + 2] switch
        {
            "on" => Write(id, true) ? 0 : 1,
            "off" => Write(id, false) ? 0 : 1,
            _ => 2,
        };
    }
}
