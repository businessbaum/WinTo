using System.Text.Json.Serialization;

namespace MultiTool.Abstractions;

/// <summary>
/// What the host knows about a module <b>without loading its code</b>. Stored as
/// <c>module.json</c> next to the module DLL; the only file read at startup.
/// </summary>
/// <remarks>
/// Everything the home page shows must come from here. If even the icon came from the
/// assembly, every module would load at startup; hence SVG path data instead of a resource.
/// </remarks>
public sealed class ModuleManifest
{
    /// <summary>Stable id, lowercase with dashes. Folder name, settings key, CLI argument.</summary>
    public required string Id { get; init; }

    /// <summary>Display name on the tile.</summary>
    public required string Name { get; init; }

    /// <summary>One sentence under the name on the tile; must fit there.</summary>
    public string Summary { get; init; } = "";

    public string Version { get; init; } = "1.0.0";

    /// <summary>Lowest host version this module runs on. Empty means any.</summary>
    /// <remarks>
    /// The contract is ABI and modules are resolved by type name from JSON, so a mismatch would
    /// otherwise surface as a <c>MissingMethodException</c> deep inside the module.
    /// </remarks>
    public string? MinHostVersion { get; init; }

    /// <summary>Groups the home page. Free text.</summary>
    public string Category { get; init; } = "General";

    /// <summary>Order within the category, smaller first.</summary>
    public int Order { get; init; } = 100;

    /// <summary>SVG path data (the <c>d</c> attribute) in a 24×24 box.</summary>
    public string Icon { get; init; } = "";

    /// <summary>Tile accent as <c>#RRGGBB</c>. Empty means the app accent.</summary>
    public string? Accent { get; init; }

    /// <summary>File name of the module DLL, relative to the module folder.</summary>
    public required string Assembly { get; init; }

    /// <summary>Fully qualified name of the type implementing <see cref="IModule"/>.</summary>
    public required string EntryType { get; init; }

    /// <summary>Extra search terms. Name and summary are searched anyway.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Whether and when the module needs administrator rights.</summary>
    public ElevationRequirement Elevation { get; init; } = ElevationRequirement.Never;

    /// <summary>Whether the module holds something that closing would lose.</summary>
    /// <remarks>
    /// A download, a recording, an unsaved search result. If <c>true</c>, the shell asks before
    /// closing and keeps the tab or window if declined. There is no "closed but still running".
    /// </remarks>
    public bool KeepsRunning { get; init; }

    /// <summary>The question asked on close when <see cref="KeepsRunning"/> is set.</summary>
    /// <remarks>Without it the shell asks generically, which rarely fits every module.</remarks>
    public ClosePrompt? ClosePrompt { get; init; }

    /// <summary>External programs the module cannot work without.</summary>
    public IReadOnlyList<ToolRequirement> Requires { get; init; } = [];

    /// <summary>How the host determines the tile status <b>without</b> loading module code.</summary>
    public StatusProbe Status { get; init; } = StatusProbe.None;

    // --- filled by the host while reading, not part of the JSON ---

    /// <summary>Folder containing <c>module.json</c> and the DLL.</summary>
    [JsonIgnore] public string Directory { get; set; } = "";

    /// <summary>Full path to the module DLL.</summary>
    [JsonIgnore] public string AssemblyPath => System.IO.Path.Combine(Directory, Assembly);
}

public enum ElevationRequirement
{
    /// <summary>Always runs as the normal user.</summary>
    Never,

    /// <summary>Works without rights; single actions request them.</summary>
    OnDemand,

    /// <summary>Useless without administrator rights; the host restarts elevated for it.</summary>
    Required,
}

/// <summary>An external program a module calls.</summary>
public sealed class ToolRequirement
{
    /// <summary>Executable name without extension, e.g. <c>yt-dlp</c>.</summary>
    public required string Tool { get; init; }

    /// <summary>Where to get it; shown when it is missing.</summary>
    public string? Hint { get; init; }

    /// <summary>Whether the module cannot start without it.</summary>
    public bool Mandatory { get; init; } = true;

    /// <summary>URL the shell can download the program from.</summary>
    /// <remarks>
    /// If set, the shell asks <b>before</b> opening the module and downloads into the module data
    /// folder, where <see cref="IToolLocator.Find"/> finds it. Never downloads without asking.
    /// </remarks>
    public string? Url { get; init; }

    /// <summary>Expected SHA-256 of the file behind <see cref="Url"/>, hex. Since 0.9.0.</summary>
    /// <remarks>For URLs pointing at a fixed version. A mismatch is discarded and never run.</remarks>
    public string? Sha256 { get; init; }

    /// <summary>
    /// URL of a <c>sha256sum</c>-style list (<c>&lt;hex&gt;  &lt;file name&gt;</c> per line). Since 0.9.0.
    /// </summary>
    /// <remarks>
    /// For "latest" URLs that can't be pinned to one checksum. HTTPS only proves the file came
    /// from GitHub, not that it is the one the project published.
    /// </remarks>
    public string? ChecksumsUrl { get; init; }
}

/// <summary>The question asked when closing a module that holds something running.</summary>
/// <param name="Hint">One sentence: what keeping it open gives, or what closing costs.</param>
/// <param name="Keep">Label of the button that keeps view <b>and</b> module.</param>
/// <param name="Close">Label of the button that closes the view and releases the module.</param>
public sealed record ClosePrompt(string Hint, string Keep = "Keep open", string Close = "Close");
