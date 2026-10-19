using MultiTool.Modules.Soundboard.Audio;
using MultiTool.Modules.Soundboard.Models;

namespace MultiTool.Modules.Soundboard.Services;

public static class SoundLibrary
{
    public static (List<FolderEntry> Folders, List<SoundEntry> Sounds) Load(string libraryFile)
    {
        var file = JsonStore.Load(libraryFile, () => new SoundLibraryFile());

        var folders = file.Folders ?? [];
        var sounds = file.Sounds
            .Where(s => !string.IsNullOrWhiteSpace(s.FilePath))
            .ToList();

        // Clean up orphaned assignments: a sound pointing to a deleted folder goes back to the top
        // instead of becoming invisible.
        var known = folders.Select(f => f.Id).ToHashSet();
        foreach (var sound in sounds)
        {
            if (sound.FolderId is { } id && !known.Contains(id)) sound.FolderId = null;
        }

        foreach (var folder in folders)
        {
            if (folder.ParentId is { } parent && (!known.Contains(parent) || parent == folder.Id))
                folder.ParentId = null;
        }

        BreakCycles(folders);
        return (folders, sounds);
    }

    /// <summary>
    /// Guards against a folder that is indirectly its own ancestor. Only possible with a hand-edited file,
    /// but it would send building the tree into an endless loop.
    /// </summary>
    private static void BreakCycles(List<FolderEntry> folders)
    {
        var byId = folders.ToDictionary(f => f.Id);

        foreach (var folder in folders)
        {
            var seen = new HashSet<Guid> { folder.Id };
            var current = folder;

            while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                if (!seen.Add(parent.Id))
                {
                    folder.ParentId = null;
                    break;
                }

                current = parent;
            }
        }
    }

    public static void Save(string libraryFile, IEnumerable<FolderEntry> folders, IEnumerable<SoundEntry> sounds) =>
        JsonStore.WriteText(libraryFile, Serialize(folders, sounds));

    /// <summary>For debounced saving: serialize on the UI thread, write elsewhere.</summary>
    public static string Serialize(IEnumerable<FolderEntry> folders, IEnumerable<SoundEntry> sounds) =>
        JsonStore.Serialize(new SoundLibraryFile
        {
            Folders = folders.ToList(),
            Sounds = sounds.ToList(),
        });

    /// <summary>Expands a selection of files and folders into a list of importable audio files. Folders are searched recursively.</summary>
    public static List<string> ExpandPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        if (AudioFormat.IsSupported(file) && seen.Add(Path.GetFullPath(file)))
                            files.Add(file);
                    }
                }
                else if (File.Exists(path) && AudioFormat.IsSupported(path) && seen.Add(Path.GetFullPath(path)))
                {
                    files.Add(path);
                }
            }
            catch (Exception)
            {
                // Skip unreadable folders (permissions, broken links).
            }
        }

        files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase));
        return files;
    }

    public static SoundEntry CreateEntry(string filePath) => new()
    {
        Name = Path.GetFileNameWithoutExtension(filePath),
        FilePath = Path.GetFullPath(filePath),
    };
}
