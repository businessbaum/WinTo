using Avalonia.Controls;

namespace MultiTool.Abstractions;

/// <summary>What the shell does for a module.</summary>
public interface IShell
{
    /// <summary>The window the module currently lives in: the main window or its own.</summary>
    Window? OwnerWindow { get; }

    /// <summary>Is the process running with administrator rights?</summary>
    bool IsElevated { get; }

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false);

    Task AlertAsync(string title, string message);

    /// <summary>Short notice at the bottom. Does not block.</summary>
    void Notify(string message, StatusLevel level = StatusLevel.Neutral);

    /// <summary>
    /// A card at the top right of the screen, also while no WinTo window is visible. Since 0.12.0.
    /// </summary>
    /// <remarks>
    /// For results of work started outside the window, such as a Win+Shift+S capture. Takes no focus and
    /// closes by itself; a click opens WinTo and <see cref="Announcement.OpenModuleId"/>. Everything
    /// else uses <see cref="Notify"/>.
    /// </remarks>
    void Announce(Announcement announcement);

    Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple = true, params FilePattern[] filters);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, params FilePattern[] filters);

    /// <summary>Folder picker. Several at once is the normal case.</summary>
    Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool multiple = true);

    /// <summary>Convenience for exactly one folder.</summary>
    async Task<string?> PickFolderAsync(string title) =>
        await PickFoldersAsync(title, multiple: false) is [var single, ..] ? single : null;

    /// <summary>Opens another module, as tab or window depending on the settings.</summary>
    void OpenModule(string moduleId);

    /// <summary>
    /// Opens another module and hands it something to work on: a folder, a file, a URL. Since 0.9.0.
    /// </summary>
    /// <remarks>
    /// The target receives it through <see cref="IModule.OpenWith"/> once its view exists, even
    /// if it was already open. The target defines what the text means.
    /// </remarks>
    void OpenModule(string moduleId, string argument);

    /// <summary>Opens Explorer with the file selected.</summary>
    void RevealInExplorer(string path);

    void OpenUrl(string url);

    /// <summary>
    /// Restarts the <b>whole app</b> elevated and opens this module there.
    /// </summary>
    /// <remarks>
    /// Asks first, naming the cost: open modules close, and an elevated window accepts no
    /// files dragged from Explorer. On success the current app quits, including the caller.
    /// <c>false</c>: continues unelevated (declined, or the restart failed). <c>true</c> without
    /// a restart only if already elevated. <paramref name="argument"/> has no effect since 0.5.0.
    /// </remarks>
    Task<bool> RunElevatedAsync(string moduleId, string? argument = null);
}

/// <param name="Name">Shown in the file dialog, e.g. "Videos".</param>
/// <param name="Extensions">Without dot, e.g. <c>["mp4", "mkv"]</c>.</param>
public sealed record FilePattern(string Name, params string[] Extensions);
