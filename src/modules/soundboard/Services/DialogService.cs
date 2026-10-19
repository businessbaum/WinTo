using MultiTool.Abstractions;
using MultiTool.Modules.Soundboard.Audio;

namespace MultiTool.Modules.Soundboard.Services;

/// <summary>Everything a view model would need a window for. Keeps view models free of Avalonia types and testable.</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickAudioFilesAsync();

    Task<IReadOnlyList<string>> PickFoldersAsync();

    void RevealInExplorer(string path);

    void OpenUrl(string url);
}

/// <summary>The implementation via the shell.</summary>
/// <remarks>
/// <para>
/// In the standalone app this class held a fixed <c>TopLevel</c>. As a module the view moves between tab and
/// own window, and a dialog belongs to whichever it's in. So <see cref="IShell"/> is held and the window is
/// asked for on each call via <c>Shell.OwnerWindow</c>.
/// </para>
/// <para>
/// The interface stays although <see cref="IShell"/> does almost the same: it's cut to the four things this
/// module needs, and the view models didn't need touching when porting.
/// </para>
/// </remarks>
public sealed class ShellDialogService(IShell shell) : IDialogService
{
    public Task<IReadOnlyList<string>> PickAudioFilesAsync() =>
        shell.PickFilesAsync(
            "Select sounds",
            multiple: true,
            new FilePattern("Audio files", [.. AudioFormat.SupportedExtensions.Select(e => e.TrimStart('.'))]),
            new FilePattern("All files", "*"));

    public Task<IReadOnlyList<string>> PickFoldersAsync() =>
        shell.PickFoldersAsync("Select folders with sounds");

    public void RevealInExplorer(string path) => shell.RevealInExplorer(path);

    public void OpenUrl(string url) => shell.OpenUrl(url);
}
