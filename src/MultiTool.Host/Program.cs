using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using MultiTool.Host.Services;

namespace MultiTool.Host;

internal static partial class Program
{
    /// <summary>Runs from the very first instruction; used to report the time to first frame.</summary>
    internal static readonly Stopwatch Uptime = Stopwatch.StartNew();

    /// <summary>
    /// Set by <c>--module &lt;id&gt;</c>: shows only this module in a window, without home page or tabs.
    /// </summary>
    internal static string? StartupModuleId { get; private set; }

    /// <summary>Module to open as a tab after startup.</summary>
    /// <remarks>
    /// Unlike <see cref="StartupModuleId"/> this starts the full app with one tab already open. Set by
    /// the elevated restart so the user continues where they were.
    /// </remarks>
    internal static string? StartupOpenModuleId { get; private set; }

    /// <summary>Set by <c>--with &lt;argument&gt;</c> next to <c>--open</c>: handed to the module's
    /// <c>OpenWith</c>. From the Explorer menu (0.13.0), e.g. a file to convert.</summary>
    internal static string? StartupOpenArgument { get; private set; }

    /// <summary>Set by <c>--background</c>: no window, only the tray icon. Used by autostart.</summary>
    internal static bool StartInBackground { get; private set; }

    // Avalonia needs STA and must not see a foreign SynchronizationContext before AppMain.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => WriteCrashLog(e.ExceptionObject as Exception, "AppDomain");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception, "Task");
            e.SetObserved();
        };

        // --selftest runs without a window and exits. First, because everything below builds UI.
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // Arguments after --selftest are module ids: test only those.
            var only = args.SkipWhile(a => !string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase))
                .Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

            Environment.Exit(SelfTest.Run(only));
            return;
        }

        // The elevated helper of "Run as administrator": creates or removes the scheduled task and exits.
        if (args.Contains(ElevatedStart.HelperSwitch, StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(ElevatedStart.RunHelper(args));
            return;
        }

        // The elevated helper of features that set a machine policy ("No forced restarts", widgets): writes
        // one known value and exits.
        if (args.Contains(MachinePolicy.HelperSwitch, StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(MachinePolicy.RunHelper(args));
            return;
        }

        ParseArguments(args);

        // --quit (from the installer): ends the instance running from this app folder, if any, and
        // never starts one itself.
        if (IsQuitRequest(args))
        {
            if (!SingleInstance.TryAcquire()) SingleInstance.Forward(args);
            else SingleInstance.Release();
            return;
        }

        // The elevated restart starts this instance before the old one exits. Wait for it, or the
        // single-instance check below would hand over to a process that is ending.
        if (WaitForProcessId is { } previous) SingleInstance.WaitForExit(previous);

        // A second instance forwards its command line and exits. --module is exempt: the single
        // module window is meant to be a separate process.
        if (StartupModuleId is null)
        {
            if (!SingleInstance.TryAcquire())
            {
                if (!SingleInstance.Forward(args))
                    MessageBox(0, "WinTo is already running.", "WinTo", 0x40);
                return;
            }

            // Called WinTo until 0.13.0: its data folder becomes WinTo's, once, while no other instance runs.
            AppPaths.MoveFormerData();

            // "Run as administrator": this start only launches the elevated task and hands over its command
            // line. The slot is freed first, so the elevated instance can take it.
            if (WantsElevatedStart())
            {
                SingleInstance.Release();
                if (ElevatedStart.TryHandOff(args, enabled: true)) return;

                if (!SingleInstance.TryAcquire())
                {
                    SingleInstance.Forward(args);
                    return;
                }
            }

            SingleInstance.Listen(ListenerStop.Token);
        }

        // MULTITOOL_TRACE=1 writes Avalonia's trace output, including every broken binding, to
        // %APPDATA%\WinTo\logs\trace.log.
        if (Environment.GetEnvironmentVariable("MULTITOOL_TRACE") == "1") EnableTrace();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, "Startup");
            throw;
        }
        finally
        {
            ListenerStop.Cancel();
            SingleInstance.Release();
        }
    }

    /// <summary>Whether this unelevated start should hand over to the elevated task.</summary>
    private static bool WantsElevatedStart()
    {
        if (Elevation.IsElevated || !ElevatedStart.IsSupported) return false;

        try
        {
            return JsonStore.Load(AppPaths.SettingsFile, static () => new AppSettings()).RunElevated;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stops listening for a second instance's handover.</summary>
    private static readonly CancellationTokenSource ListenerStop = new();

    /// <summary>Set by <c>--wait-for &lt;pid&gt;</c>: the process to wait for. From the elevated restart.</summary>
    private static int? WaitForProcessId { get; set; }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ParseArguments(string[] args)
    {
        StartInBackground = IsBackgroundStart(args);

        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--wait-for", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var pid))
            {
                WaitForProcessId = pid;
            }
            else if (string.Equals(args[i], "--open", StringComparison.OrdinalIgnoreCase))
            {
                StartupOpenModuleId ??= args[i + 1];
            }
            else if (string.Equals(args[i], "--with", StringComparison.OrdinalIgnoreCase))
            {
                StartupOpenArgument ??= args[i + 1];
            }
            else if (string.Equals(args[i], "--open-file", StringComparison.OrdinalIgnoreCase))
            {
                StartupOpenModuleId ??= ModuleForFile(args[i + 1]);
                StartupOpenArgument ??= args[i + 1];
            }
            else if (string.Equals(args[i], "--module", StringComparison.OrdinalIgnoreCase))
            {
                StartupModuleId ??= args[i + 1];
            }
        }
    }

    internal static bool IsBackgroundStart(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>--quit</c>: the installer closes the running app before replacing its files. Since 0.12.0;
    /// older versions treat it as an unknown argument and come forward, so the installer only sends it
    /// to 0.12.0 and later.
    /// </summary>
    internal static bool IsQuitRequest(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase));

    /// <summary>What a second instance forwarded: the module to open, if any.</summary>
    internal static string? OpenModuleIdFrom(IReadOnlyList<string> args) =>
        ValueAfter(args, "--open") ?? (ValueAfter(args, "--open-file") is { } file ? ModuleForFile(file) : null);

    /// <summary>What a second instance forwarded for that module's <c>OpenWith</c>, if anything.</summary>
    internal static string? OpenArgumentFrom(IReadOnlyList<string> args) => ValueAfter(args, "--with") ?? ValueAfter(args, "--open-file");

    /// <summary><c>--open-file</c> from Explorer's "Open with": videos to the Video tool, everything else to Photos.</summary>
    private static string ModuleForFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".mkv" or ".webm" or ".avi" or ".wmv"
            or ".flv" or ".mpg" or ".mpeg" or ".ts" or ".m2ts" or ".mts" or ".3gp" or ".ogv"
            ? "video"
            : "photos";

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];

        return null;
    }

    private static void EnableTrace()
    {
        try
        {
            AppPaths.EnsureDirectory(AppPaths.LogDirectory);
            var path = Path.Combine(AppPaths.LogDirectory, "trace.log");
            File.Delete(path);

            Trace.Listeners.Add(new TextWriterTraceListener(new StreamWriter(
                path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))));
            Trace.AutoFlush = true;
        }
        catch (Exception)
        {
            // The app starts without trace as well.
        }
    }

    internal static void WriteCrashLog(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            var text = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {source}")
                .AppendLine(ex.ToString())
                .AppendLine(new string('-', 72))
                .ToString();

            AppPaths.EnsureDirectory(AppPaths.LogDirectory);
            // With BOM like every log here: PowerShell 5.1 reads files without one as ANSI.
            File.AppendAllText(AppPaths.CrashLogFile, text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception)
        {
            // A failing crash log must not make the crash worse.
        }
    }
}
