using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace MultiTool.Host.Services;

/// <summary>"Run as administrator": every start elevated, with one prompt when switching it on. Since 0.13.0.</summary>
/// <remarks>
/// <para>
/// <b>A scheduled task with "highest privileges"</b> is the only way Windows offers to start a program elevated
/// without a prompt each time. Creating it needs administrator rights, which is the one prompt. Every later
/// start (Start menu, autostart, a second click) runs as a short-lived launcher: it starts the task, hands its
/// command line to the elevated instance over the single-instance pipe, and exits. <c>--background</c> stays in
/// the tray, anything else shows the window.
/// </para>
/// <para>
/// <b>Only for the installed app under Program Files.</b> The task starts a fixed EXE elevated without asking.
/// In a folder the user can write to, any program could replace that EXE and get administrator rights without
/// a prompt; the elevated helper that creates the task checks its own path again.
/// </para>
/// <para>
/// <b>The cost, said in the settings:</b> an elevated window accepts no files dragged from Explorer (UIPI).
/// </para>
/// </remarks>
public static class ElevatedStart
{
    /// <summary>In its own folder in Task Scheduler, so it is recognizable.</summary>
    private const string TaskName = @"WinTo\Run as administrator";

    /// <summary>Arguments of the elevated helper that creates or removes the task.</summary>
    public const string HelperSwitch = "--elevated-start-task";

    /// <summary>Whether this copy of the app may use it: installed under Program Files.</summary>
    public static bool IsSupported
    {
        get
        {
            var app = Path.TrimEndingDirectorySeparator(AppPaths.AppDirectory) + Path.DirectorySeparatorChar;

            return new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                }
                .Where(root => root.Length > 0)
                .Any(root => app.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------ launcher

    /// <summary>
    /// At startup, before anything else claims the single-instance slot: hands over to the elevated task.
    /// </summary>
    /// <returns><c>true</c> if the elevated instance took over and this process should exit.</returns>
    public static bool TryHandOff(string[] args, bool enabled)
    {
        if (!enabled || Elevation.IsElevated || !IsSupported) return false;

        if (!RunTask())
        {
            AppLogFallback("The elevated task could not be started; starting without administrator rights.");
            return false;
        }

        // The elevated instance needs a moment to come up and listen.
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            if (SingleInstance.Forward(args)) return true;
            Thread.Sleep(200);
        }

        AppLogFallback("The elevated instance didn't answer within 15 s; starting without administrator rights.");
        return false;
    }

    private static bool RunTask() => Run("schtasks.exe", ["/run", "/tn", TaskName]) == 0;

    // ------------------------------------------------------------------ switching

    /// <summary>Creates the task. Asks for administrator rights once unless already elevated.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static async Task<string?> EnableAsync()
    {
        if (!IsSupported) return "Only for the installed WinTo under Program Files.";

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return "The user account could not be read.";

        if (Elevation.IsElevated) return Register(sid);

        return await RunHelperAsync("register", sid);
    }

    /// <summary>Removes the task. Asks for administrator rights unless already elevated.</summary>
    public static async Task<string?> DisableAsync()
    {
        if (!Exists()) return null;
        if (Elevation.IsElevated) return Unregister();

        return await RunHelperAsync("unregister", null);
    }

    /// <summary>Whether the task exists. Readable without administrator rights.</summary>
    public static bool Exists() => Run("schtasks.exe", ["/query", "/tn", TaskName]) == 0;

    /// <summary>
    /// Restarts through the task: a new launcher waits for this process to end, then hands over.
    /// </summary>
    public static bool RestartElevated()
    {
        if (Environment.ProcessPath is not { } exe) return false;

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("--wait-for");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        try
        {
            return Process.Start(info) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string?> RunHelperAsync(string action, string? sid)
    {
        if (Environment.ProcessPath is not { } exe) return "WinTo's path is unknown.";

        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(HelperSwitch);
        info.ArgumentList.Add(action);
        if (sid is not null) info.ArgumentList.Add(sid);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return "The administrator prompt didn't open.";

            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : $"Windows refused the task (code {process.ExitCode}).";
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

    // ------------------------------------------------------------------ elevated helper

    /// <summary>
    /// <c>--elevated-start-task register &lt;sid&gt;</c> or <c>unregister</c>, in the elevated helper process.
    /// </summary>
    /// <returns>The exit code: 0 done, 2 refused, 1 failed.</returns>
    public static int RunHelper(string[] args)
    {
        var index = Array.IndexOf(args, HelperSwitch);
        var action = index >= 0 && index + 1 < args.Length ? args[index + 1] : "";

        // The same check as in the app, again here: this code runs elevated.
        if (!Elevation.IsElevated || !IsSupported) return 2;

        switch (action)
        {
            case "register" when index + 2 < args.Length && IsUserSid(args[index + 2]):
                return Register(args[index + 2]) is null ? 0 : 1;
            case "unregister":
                return Unregister() is null ? 0 : 1;
            default:
                return 2;
        }
    }

    /// <summary>Only a user account SID; anything else could inject XML into the task.</summary>
    private static bool IsUserSid(string sid)
    {
        try
        {
            var parsed = new SecurityIdentifier(sid);
            return parsed.AccountDomainSid is not null && parsed.Value == sid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? Register(string sid)
    {
        if (Environment.ProcessPath is not { } exe) return "WinTo's path is unknown.";

        var file = Path.Combine(Path.GetTempPath(), $"multitool-task-{Guid.NewGuid():N}.xml");

        try
        {
            // UTF-16 with BOM, as schtasks expects for /xml.
            File.WriteAllText(file, TaskXml(sid, exe), Encoding.Unicode);
            return Run("schtasks.exe", ["/create", "/tn", TaskName, "/xml", file, "/f"]) == 0
                ? null
                : "schtasks refused the task.";
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    private static string? Unregister() =>
        Run("schtasks.exe", ["/delete", "/tn", TaskName, "/f"]) == 0 ? null : "schtasks refused to delete the task.";

    /// <summary>
    /// Runs as the user who switched it on, in their session, elevated; normal priority (a task's default is
    /// below normal), no time limit, not stopped on battery.
    /// </summary>
    private static string TaskXml(string sid, string exe) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts WinTo with administrator rights without a prompt. Created by "Run as administrator" in WinTo's settings; switching it off there removes it.</Description>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(sid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
              <Arguments>--background</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private static int Run(string program, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return -1;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void AppLogFallback(string message) => new ModuleLog("shell").Warn(message);
}
