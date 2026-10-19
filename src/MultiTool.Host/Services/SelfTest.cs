using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Host.Services;

/// <summary><c>WinTo.exe --selftest</c>: checks the module system without opening a window.</summary>
/// <remarks>
/// <para>
/// The app's central promise ("no module code before the first open") is a claim about memory
/// and belongs tested. <c>SetupWithoutStarting()</c> builds Avalonia completely without showing
/// anything, so nothing steals focus.
/// </para>
/// <para>
/// Exit codes: <b>0</b> all good · <b>1</b> a module failed to load or show · <b>2</b> the promise
/// was broken, module code was loaded before the first open.
/// </para>
/// </remarks>
internal static class SelfTest
{
    /// <param name="only">Empty means all modules, otherwise only these ids.</param>
    public static int Run(IReadOnlyCollection<string> only)
    {
        var report = new StringBuilder();
        var exitCode = 0;

        void Say(string line)
        {
            report.AppendLine(line);
            Console.WriteLine(line);
        }

        AttachToParentConsole();

        Say($"WinTo self-test · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        Say(new string('-', 72));

        var catalog = ModuleCatalog.Discover();
        Say($"Modules found: {catalog.Modules.Count}");
        foreach (var problem in catalog.Problems)
            Say($"  PROBLEM  {problem.Message}  ({problem.Path})");

        // --- the actual check: is really nothing of it loaded beforehand? ---
        var leakedEarly = catalog.Modules
            .Where(m => LoadedAssemblies.IsLoaded(Path.GetFileNameWithoutExtension(m.Assembly)))
            .ToList();

        var before = LoadedAssemblies.Count;
        Say("");
        Say($"Assemblies before the first module: {before}");

        // MULTITOOL_ASSEMBLIES=1 names them, to find what a change pulled into the start.
        if (Environment.GetEnvironmentVariable("MULTITOOL_ASSEMBLIES") == "1")
            foreach (var context in LoadedAssemblies.Contexts())
                Say($"  {context.Name}: {string.Join(", ", context.Assemblies)}");

        if (leakedEarly.Count > 0)
        {
            exitCode = 2;
            Say("");
            Say("FAILED – these modules were loaded before being opened:");
            foreach (var m in leakedEarly) Say($"  {m.Id}  ({m.Assembly})");
            Say("");
            Say("Most common cause: a StyleInclude or ResourceInclude pointing at");
            Say("avares://<module>/… in App.axaml or MultiTool.UI. A single such");
            Say("reference loads the module DLL at startup.");
        }

        // Avalonia must be set up before a control can be built, but without a window.
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();

        // Manifest icons: an unreadable path silently falls back to the default icon, so nobody
        // would notice otherwise.
        foreach (var manifest in catalog.Modules)
        {
            if (string.IsNullOrWhiteSpace(manifest.Icon)) continue;

            try
            {
                Avalonia.Media.Geometry.Parse(manifest.Icon);
            }
            catch (Exception ex)
            {
                Say($"  WARNING  icon of {manifest.Id} unreadable: {ex.Message}");
            }
        }

        var shell = new HeadlessShell();
        var modules = new ModuleManager(shell, CancellationToken.None);

        Say("");
        Say("Module                    load     view      state");
        Say(new string('-', 72));

        var tested = only.Count == 0
            ? catalog.Modules
            : [.. catalog.Modules.Where(m => only.Contains(m.Id, StringComparer.OrdinalIgnoreCase))];

        foreach (var manifest in tested)
        {
            var name = manifest.Id.Length > 24 ? manifest.Id[..24] : manifest.Id.PadRight(24);
            try
            {
                var session = modules.Load(manifest);

                var viewWatch = Stopwatch.StartNew();
                var view = session.Module.CreateView();
                viewWatch.Stop();

                // The full lifecycle, in the order the shell uses.
                session.Module.ViewActivated(view);
                session.Module.ViewDeactivated(view);
                session.Module.ViewClosed(view);
                modules.Release(manifest.Id, force: true);

                Say($"{name}  {session.LoadTime.TotalMilliseconds,6:0} ms  "
                    + $"{viewWatch.ElapsedMilliseconds,6} ms   ok");
            }
            catch (Exception ex)
            {
                exitCode = Math.Max(exitCode, 1);
                Say($"{name}       —         —   ERROR");
                Say($"    {ex.GetType().Name}: {ex.Message}");
            }
        }

        var after = LoadedAssemblies.Count;
        Say(new string('-', 72));
        Say($"Assemblies after all modules: {after}   (+{after - before})");
        Say($"Load contexts: {string.Join(", ", LoadedAssemblies.Contexts().Select(c => c.Name))}");
        Say($"Managed memory: {GC.GetTotalMemory(true) / (1024 * 1024)} MB");
        Say("");
        Say(exitCode switch
        {
            0 => "Result: passed.",
            1 => "Result: at least one module failed to open.",
            _ => "Result: the promise \"nothing is preloaded\" is broken.",
        });

        try
        {
            AppPaths.EnsureDirectory(AppPaths.LogDirectory);
            // With BOM: PowerShell 5.1 reads files without one as ANSI.
            File.WriteAllText(Path.Combine(AppPaths.LogDirectory, "selftest.txt"),
                report.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception)
        {
            // The report is on the console as well.
        }

        return exitCode;
    }

    /// <summary>Attaches to the calling console.</summary>
    /// <remarks>
    /// The app is a <c>WinExe</c> without a console of its own; <c>AttachConsole(-1)</c> takes the
    /// parent's, e.g. the PowerShell it was started from.
    /// </remarks>
    private static void AttachToParentConsole()
    {
        try
        {
            if (!AttachConsole(-1)) return;

            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
        }
        catch (Exception)
        {
            // Without a console the report stays in the file.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>A shell that shows nothing; a dialog during the self-test could never be dismissed.</summary>
    private sealed class HeadlessShell : IShell
    {
        public Window? OwnerWindow => null;

        public bool IsElevated => Elevation.IsElevated;

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false) =>
            Task.FromResult(false);

        public Task AlertAsync(string title, string message) => Task.CompletedTask;

        public void Notify(string message, StatusLevel level = StatusLevel.Neutral) { }

        public void Announce(Announcement announcement) { }

        public Task<IReadOnlyList<string>> PickFilesAsync(
            string title, bool multiple = true, params FilePattern[] filters) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSaveFileAsync(
            string title, string suggestedName, params FilePattern[] filters) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool multiple = true) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public void OpenModule(string moduleId) { }

        public void OpenModule(string moduleId, string argument) { }

        public void RevealInExplorer(string path) { }

        public void OpenUrl(string url) { }

        public Task<bool> RunElevatedAsync(string moduleId, string? argument = null) =>
            Task.FromResult(false);
    }
}
