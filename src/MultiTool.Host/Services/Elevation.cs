using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace MultiTool.Host.Services;

/// <summary>Administrator rights: the elevated restart and its safeguards.</summary>
/// <remarks>
/// Since 0.5.0 a module that needs rights restarts the whole app elevated
/// (<c>WinTo.exe --open &lt;id&gt;</c>). The dialog names the cost first: open modules close,
/// and an elevated window accepts no files dragged from Explorer (UIPI).
/// </remarks>
public static class Elevation
{
    /// <summary>Is this process running as administrator?</summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>Restarts <b>the whole app</b> elevated and opens a module.</summary>
    /// <returns><c>false</c> if the user cancelled the prompt.</returns>
    /// <remarks>
    /// Until 0.5.0 a second process with a single module window came up instead; the user asked
    /// WinTo for rights and got a second window while the first still said rights were
    /// missing. Elevated, <see cref="AppPaths.ModuleRoots"/> returns only the app folder, and
    /// <see cref="HasBundledModule"/> checks beforehand that the module is there.
    /// </remarks>
    public static bool RestartElevated(string? openModuleId)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        if (openModuleId is { Length: > 0 } && !HasBundledModule(openModuleId)) return false;

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        if (openModuleId is { Length: > 0 } id)
        {
            info.ArgumentList.Add("--open");
            info.ArgumentList.Add(id);
        }

        // The new instance waits for this one to exit; otherwise it would find the single-instance
        // slot taken and hand over to the instance that is just ending.
        info.ArgumentList.Add("--wait-for");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            return Process.Start(info) is not null;
        }
        catch (Win32Exception)
        {
            // 1223 = the user cancelled the prompt.
            return false;
        }
    }

    /// <summary>Is this module bundled next to the app?</summary>
    /// <remarks>
    /// Elevated, modules are only searched there. A module elsewhere (e.g. <c>build\modules</c>)
    /// would open an elevated window that cannot find it; better to say no beforehand.
    /// </remarks>
    public static bool HasBundledModule(string moduleId)
    {
        try
        {
            return File.Exists(Path.Combine(AppPaths.BundledModules, moduleId, "module.json"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
