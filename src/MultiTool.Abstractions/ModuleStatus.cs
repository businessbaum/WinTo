namespace MultiTool.Abstractions;

/// <summary>
/// How the host determines a tile's status.
/// </summary>
/// <remarks>
/// <para>
/// Computing a status means running code, which means loading the assembly, which must not
/// happen at startup. Most real statuses fit a few shapes the <b>host</b> can evaluate on its
/// own: is a process running, does a path exist, how many files are in it, are the tools
/// installed. Anything else the module writes to its status file while running
/// (<see cref="StatusProbeKind.Cached"/>).
/// </para>
/// <para>Probes run after the first frame on a background thread.</para>
/// </remarks>
public sealed class StatusProbe
{
    public static readonly StatusProbe None = new() { Kind = StatusProbeKind.None };

    public StatusProbeKind Kind { get; init; } = StatusProbeKind.None;

    /// <summary>Path for <see cref="StatusProbeKind.Path"/> and <see cref="StatusProbeKind.Count"/>. Environment variables are expanded.</summary>
    public string? Path { get; init; }

    /// <summary>File pattern for <see cref="StatusProbeKind.Count"/>, e.g. <c>*.mp4</c>.</summary>
    public string? Pattern { get; init; }

    /// <summary>Process name without <c>.exe</c> for <see cref="StatusProbeKind.Process"/>.</summary>
    public string? Process { get; init; }

    /// <summary>Text on a match. <c>{n}</c> is replaced by the count.</summary>
    public string? WhenTrue { get; init; }

    /// <summary>Text without a match.</summary>
    public string? WhenFalse { get; init; }
}

public enum StatusProbeKind
{
    /// <summary>No status; the tile shows name and summary only.</summary>
    None,

    /// <summary>The module wrote its status last time; the host reads the file.</summary>
    Cached,

    /// <summary>Is a process with this name running?</summary>
    Process,

    /// <summary>Does this path exist?</summary>
    Path,

    /// <summary>How many files are under <see cref="StatusProbe.Path"/>?</summary>
    Count,

    /// <summary>Can every <see cref="ToolRequirement.Mandatory"/> program be found?</summary>
    Tools,
}

/// <summary>A probe result, or what a module reported about itself.</summary>
/// <param name="Level">Colors the dot on the tile.</param>
/// <param name="Text">Short; the tile has room for about 24 characters.</param>
/// <param name="AsOf">When it was determined. Cached states show their age.</param>
/// <param name="Detail">Longer explanation for the tooltip.</param>
public sealed record ModuleStatus(
    StatusLevel Level,
    string Text,
    DateTimeOffset? AsOf = null,
    string? Detail = null)
{
    public static readonly ModuleStatus Unknown = new(StatusLevel.Neutral, "");
}

public enum StatusLevel
{
    Neutral,
    Good,
    Warn,
    Bad,

    /// <summary>The module is working: download, recording, scan.</summary>
    Busy,
}
