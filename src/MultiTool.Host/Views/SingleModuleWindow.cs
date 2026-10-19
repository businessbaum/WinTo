using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MultiTool.Abstractions;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Views;

/// <summary>One module, one window, no home page.</summary>
/// <remarks>
/// Built by <c>WinTo.exe --module &lt;id&gt;</c>. Elevation no longer uses this path (since
/// 0.5.0); it stays for manual use, together with the app-folder-only check.
/// </remarks>
internal static class SingleModuleWindow
{
    public static Window Create(ShellViewModel shell, string moduleId)
    {
        var manifest = shell.Catalog.Find(moduleId);
        if (manifest is null) return ErrorWindow($"Unknown module: {moduleId}");

        // Second check to AppPaths.ModuleRoots, on the file itself: elevated, only code from the
        // app folder is loaded.
        if (Elevation.IsElevated && !AppPaths.IsInsideAppDirectory(manifest.AssemblyPath))
            return ErrorWindow(
                "With administrator rights, only modules from the app folder are loaded. " +
                $"This one is located at:{Environment.NewLine}{manifest.Directory}");

        ModuleSession session;
        Control view;
        try
        {
            session = shell.Modules.Load(manifest);
            view = session.Module.CreateView();
        }
        catch (Exception ex)
        {
            return ErrorWindow(ex.Message);
        }

        session.View = view;

        var window = new ModuleWindow(shell, manifest, view)
        {
            Title = manifest.Name
                + (Elevation.IsElevated ? " – WinTo (Administrator)" : " – WinTo"),
        };

        // No main window to dock into.
        window.HideDockButton();

        // This path bypasses ShellViewModel, so the window reports ViewActivated/ViewDeactivated
        // itself: on open, minimize and before close. Repeated values are swallowed.
        var visible = false;
        void SetVisible(bool value)
        {
            if (visible == value) return;
            visible = value;
            try
            {
                if (value) session.Module.ViewActivated(view);
                else session.Module.ViewDeactivated(view);
            }
            catch (Exception)
            {
                // A module throwing here must not take the window down.
            }
        }

        window.Opened += (_, _) => SetVisible(true);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
                SetVisible(window.WindowState != WindowState.Minimized);
        };

        window.Closed += (_, _) =>
        {
            SetVisible(false);

            try
            {
                session.Module.ViewClosed(view);
            }
            catch (Exception)
            {
                // Ignore module errors while closing.
            }

            shell.Shutdown();
        };

        return window;
    }

    private static Window ErrorWindow(string message) => new()
    {
        Title = "WinTo",
        Width = 480,
        SizeToContent = SizeToContent.Height,
        Background = new SolidColorBrush(Color.Parse("#101118")),
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "The module could not be opened", Classes = { "h1" }, FontSize = 17 },
                new TextBlock
                {
                    Text = message,
                    Classes = { "caption" },
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        },
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
}
