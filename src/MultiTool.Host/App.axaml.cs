using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using MultiTool.Host.ControlCenter;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;
using MultiTool.Host.Views;
using MultiTool.UI;

namespace MultiTool.Host;

public partial class App : Application
{
    /// <summary>The shell. Exactly one per process.</summary>
    internal static ShellViewModel? Shell { get; private set; }

    /// <summary>Set once the app really ends; no window asks anything after that.</summary>
    internal static bool Quitting { get; private set; }

    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static TrayIcon? _tray;
    private static DispatcherTimer? _trimTimer;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            var shell = new ShellViewModel();
            Shell = shell;

            // Before any window, so nothing is drawn in the default color first.
            Accent.Apply(shell.Settings.Accent);

            // Before the first window: from here on, a UI thread error no longer ends the app.
            CrashGuard.Install(message => shell.ShowNotice(message, Abstractions.StatusLevel.Bad));

            if (Program.StartupModuleId is { } moduleId)
            {
                // Single module window (--module): one module, one window, no home page.
                desktop.MainWindow = SingleModuleWindow.Create(shell, moduleId);
            }
            else
            {
                // The main window no longer carries the app's lifetime: closing it keeps the app in the
                // tray, quitting goes through the tray icon. See MainWindow.OnClosed and Quit.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _tray = CreateTray(shell);

                // Features after the first frame: hooks and watchers are not part of the window appearing.
                Dispatcher.UIThread.Post(shell.Features.StartAll, DispatcherPriority.Background);

                // Into the tray: always with --background (autostart), and on a normal start unless the
                // window is wanted. The very first start shows the window with the tour. --open wins.
                var trayOnly = Program.StartupOpenModuleId is null
                    && (Program.StartInBackground || (shell.Settings.IntroShown && !shell.Settings.OpenWindowOnStart));

                if (!trayOnly)
                {
                    var window = new MainWindow { DataContext = shell };
                    shell.MainWindow = window;
                    desktop.MainWindow = window;
                }
                else if (!Program.StartInBackground)
                {
                    // Started by hand: say where it went. Autostart stays silent.
                    Dispatcher.UIThread.Post(() => Views.Toast.Show("WinTo runs in the tray",
                        "Click here or the tray icon to open it.", ShowMainWindow),
                        DispatcherPriority.Background);
                }

                // After the first frame: opening loads module code, and nothing is loaded at startup.
                if (Program.StartupOpenModuleId is { Length: > 0 } openId)
                {
                    Dispatcher.UIThread.Post(
                        () => shell.OpenModule(openId, ModuleOpenMode.Tab, Program.StartupOpenArgument),
                        DispatcherPriority.Background);
                }

                // Handover from a second instance: open a module or just come forward. A second
                // autostart (--background) wants neither.
                SingleInstance.SetHandler(args =>
                {
                    // The installer: no questions, an update is what the user just chose.
                    if (Program.IsQuitRequest(args))
                    {
                        Dispatcher.UIThread.Post(Quit);
                        return;
                    }

                    if (Program.IsBackgroundStart(args)) return;

                    Dispatcher.UIThread.Post(
                        () => shell.ReceiveForwarded(Program.OpenModuleIdFrom(args), Program.OpenArgumentFrom(args)),
                        DispatcherPriority.Background);
                });

                if (trayOnly)
                {
                    shell.ToolList.Suspend();
                    TrimSoon();
                }
            }

            // Logoff or shutdown: no more questions.
            desktop.ShutdownRequested += (_, _) =>
            {
                Quitting = true;
                (shell.MainWindow as MainWindow)?.RememberPlacement();
                shell.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The tray icon: left click opens the control center, the menu has the rest.</summary>
    /// <remarks>
    /// Registered through <see cref="TrayIcon.SetIcons"/> so Avalonia removes it on exit; otherwise it
    /// lingers as a ghost until hovered.
    /// </remarks>
    private TrayIcon CreateTray(ShellViewModel shell)
    {
        var controlCenter = new NativeMenuItem("Control center");
        controlCenter.Click += (_, _) => ControlCenterHost.Show(shell);

        var open = new NativeMenuItem("Open WinTo");
        open.Click += (_, _) => ShowMainWindow();

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAskingAsync();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://WinTo/Assets/app.ico"))),
            ToolTipText = "WinTo",
            Menu = new NativeMenu { Items = { controlCenter, open, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };

        tray.Clicked += (_, _) => ControlCenterHost.Toggle(shell);

        TrayIcon.SetIcons(this, [tray]);
        return tray;
    }

    /// <summary>Brings the main window forward, rebuilding it if it was closed to the tray.</summary>
    internal static void ShowMainWindow()
    {
        if (Shell is not { } shell || _desktop is not { } desktop || Quitting) return;
        if (Program.StartupModuleId is not null) return;

        if (shell.MainWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show();
            existing.Activate();
            return;
        }

        shell.EnsureHomeTab();

        var window = new MainWindow { DataContext = shell };
        shell.MainWindow = window;
        desktop.MainWindow = window;

        window.Show();
        window.Activate();
    }

    /// <summary>The main window is closed; the app stays in the tray.</summary>
    /// <remarks>
    /// Modules are already released (see <see cref="ShellViewModel.EnterBackground"/>); only the shell
    /// remains: catalog, settings, tray icon.
    /// </remarks>
    internal static void EnterBackground()
    {
        if (Shell is not { } shell || _desktop is not { } desktop) return;

        shell.EnterBackground();
        desktop.MainWindow = null;
        TrimSoon();
    }

    /// <summary>Quit from the tray or the control center: asks only if something would be lost.</summary>
    internal static async Task QuitAskingAsync()
    {
        if (Quitting || Shell is not { } shell) return;

        var losses = shell.PendingLosses();
        if (losses.Count > 0)
        {
            var owner = shell.MainWindow is { IsVisible: true } visible ? visible : null;
            var message = string.Join(Environment.NewLine, losses.Select(line => "· " + line))
                + Environment.NewLine + Environment.NewLine + "Quitting stops this.";

            if (!await MessageDialog.ConfirmAsync(owner, "Quit WinTo", message, "Quit", danger: true)) return;
        }

        Quit();
    }

    /// <summary>Quits the app. The only way, since the window no longer carries its lifetime.</summary>
    internal static void Quit()
    {
        if (Quitting) return;
        Quitting = true;

        ControlCenterHost.Close();
        Shell?.Features.StopAll();

        if (Shell is { } shell)
        {
            if (shell.MainWindow is MainWindow { IsVisible: true } window) window.RememberPlacement();
            shell.Shutdown();
        }

        if (_tray is { } tray)
        {
            tray.IsVisible = false;
            tray.Dispose();
            _tray = null;
        }

        _desktop?.Shutdown();
    }

    /// <summary>Returns memory once no window is open.</summary>
    /// <remarks>
    /// <para>
    /// Two seconds delay so reopening the control center right away doesn't wait for paged-out memory.
    /// </para>
    /// <para>
    /// GC releases managed pages, Skia drops its font and image caches, and
    /// <c>SetProcessWorkingSetSizeEx(-1, -1)</c> lets Windows page out the working set. Memory an idle
    /// process holds is missing from a running game.
    /// </para>
    /// </remarks>
    internal static void TrimSoon()
    {
        if (_trimTimer is null)
        {
            _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _trimTimer.Tick += (_, _) =>
            {
                _trimTimer.Stop();
                if (_desktop is null || _desktop.Windows.Count > 0 || Quitting) return;
                TrimNow();
            };
        }

        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private static void TrimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            SkiaSharp.SKGraphics.PurgeAllCaches();

            SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
        }
        catch (Exception ex)
        {
            new ModuleLog("shell").Warn($"Memory could not be released: {ex.Message}");
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(nint process, nint minimum, nint maximum, uint flags);

    /// <summary>An icon from <c>MultiTool.UI/Theme/Icons.axaml</c>. For the shell; modules bring theirs in the manifest.</summary>
    internal static Geometry SharedIcon(string key) =>
        Current?.Resources.TryGetResource(key, ThemeVariant.Dark, out var value) == true && value is Geometry geometry
            ? geometry
            : IconCache.Get(null);
}
