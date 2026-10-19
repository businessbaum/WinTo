using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MultiTool.Abstractions;
using MultiTool.Host.ViewModels;
using MultiTool.Host.Views;

namespace MultiTool.Host.Services;

/// <summary>Implements <see cref="IShell"/>: everything the shell does for a module.</summary>
internal sealed class ShellService(ShellViewModel shell) : IShell
{
    /// <summary>
    /// The active window, not the main window: a module may live in its own window, and its
    /// dialogs belong there.
    /// </summary>
    public Window? OwnerWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.FirstOrDefault(w => w.IsActive)
        ?? shell.MainWindow;

    public bool IsElevated => Elevation.IsElevated;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            MessageDialog.ConfirmAsync(OwnerWindow, title, message, confirmText, danger));

    public Task AlertAsync(string title, string message) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            MessageDialog.AlertAsync(OwnerWindow, title, message));

    public void Notify(string message, StatusLevel level = StatusLevel.Neutral) =>
        Dispatcher.UIThread.Post(() => shell.ShowNotice(message, level));

    public void Announce(Announcement announcement) =>
        Dispatcher.UIThread.Post(() => shell.ShowAnnouncement(announcement));

    public async Task<IReadOnlyList<string>> PickFilesAsync(
        string title, bool multiple = true, params FilePattern[] filters)
    {
        var provider = OwnerWindow?.StorageProvider;
        if (provider is null) return [];

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = ToFileTypes(filters),
        });

        return files.Select(f => f.Path.LocalPath).ToList();
    }

    public async Task<string?> PickSaveFileAsync(
        string title, string suggestedName, params FilePattern[] filters)
    {
        var provider = OwnerWindow?.StorageProvider;
        if (provider is null) return null;

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ToFileTypes(filters),
        });

        return file?.Path.LocalPath;
    }

    public async Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool multiple = true)
    {
        var provider = OwnerWindow?.StorageProvider;
        if (provider is null) return [];

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
        });

        // TryGetLocalPath instead of Path.LocalPath: libraries, network places and "This PC" have
        // no local path.
        return folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
    }

    public void OpenModule(string moduleId) =>
        Dispatcher.UIThread.Post(() => shell.OpenModule(moduleId));

    public void OpenModule(string moduleId, string argument) =>
        Dispatcher.UIThread.Post(() => shell.OpenModule(moduleId, argument: argument));

    public void RevealInExplorer(string path)
    {
        // Process.Start off the UI thread; the antivirus can cost hundreds of ms on first launch.
        _ = Task.Run(() =>
        {
            try
            {
                // /select highlights the file; for a folder it would show the parent instead.
                var argument = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Notify("Explorer could not be opened.", StatusLevel.Warn);
            }
        });
    }

    public void OpenUrl(string url)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Notify("The address could not be opened.", StatusLevel.Warn);
            }
        });
    }

    /// <summary>Restarts WinTo with administrator rights and reopens this module.</summary>
    /// <remarks>
    /// The whole app, not a second window (see <see cref="Elevation.RestartElevated"/>). The dialog
    /// names the cost: open modules close, and an elevated window accepts no dragged files.
    /// </remarks>
    public async Task<bool> RunElevatedAsync(string moduleId, string? argument = null)
    {
        if (Elevation.IsElevated) return true;

        var open = shell.OpenModules();

        var message = "WinTo restarts with administrator rights."
            + Environment.NewLine + Environment.NewLine
            + (open.Count > 0
                ? $"{open.Count} open modules will close. Unsaved work is lost."
                  + Environment.NewLine
                : "")
            + "As administrator, the window no longer accepts files dragged from Explorer.";

        if (!await ConfirmAsync("Restart as administrator", message, "Restart", danger: true))
            return false;

        if (!Elevation.RestartElevated(moduleId))
        {
            Notify("The restart with administrator rights was cancelled.", StatusLevel.Warn);
            return false;
        }

        // Releases modules first, then ends the process; the window no longer carries the
        // app's lifetime, so closing it alone would leave this instance in the tray.
        App.Quit();

        return true;
    }

    private static List<FilePickerFileType>? ToFileTypes(FilePattern[] filters)
    {
        if (filters.Length == 0) return null;

        return filters
            .Select(f => new FilePickerFileType(f.Name)
            {
                Patterns = f.Extensions.Select(e => "*." + e.TrimStart('.')).ToArray(),
            })
            .ToList();
    }
}
