namespace MultiTool.Modules.Soundboard.Models;

/// <summary>A sound library entry as stored in sounds.json.</summary>
public sealed class SoundEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public Hotkey? Hotkey { get; set; }

    public float Volume { get; set; } = 1f;

    public bool Loop { get; set; }

    /// <summary>Cached length so the list doesn't open every file at startup. 0 = unknown.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Folder containing the sound. Null = top level.</summary>
    public Guid? FolderId { get; set; }

    /// <summary>Order within the folder.</summary>
    public int Order { get; set; }
}

/// <summary>A folder in the library. Folders can be nested.</summary>
public sealed class FolderEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New folder";

    public Guid? ParentId { get; set; }

    public bool IsExpanded { get; set; } = true;

    public int Order { get; set; }
}

public sealed class SoundLibraryFile
{
    public int Version { get; set; } = 2;

    public List<FolderEntry> Folders { get; set; } = [];

    public List<SoundEntry> Sounds { get; set; } = [];
}
