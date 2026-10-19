namespace MultiTool.Abstractions;

/// <summary>
/// A result worth showing outside the window: a title, a detail line, optionally a picture and what a click
/// opens. Shown through <see cref="IShell.Announce"/>. Since 0.12.0.
/// </summary>
/// <param name="Title">One short statement, e.g. "Screenshot in the clipboard".</param>
/// <param name="Detail">One line below it; may be empty.</param>
public sealed record Announcement(string Title, string Detail = "")
{
    /// <summary>An image file shown as a thumbnail, e.g. the saved screenshot.</summary>
    public string? ImagePath { get; init; }

    /// <summary>The module a click opens; <c>null</c> opens only the main window.</summary>
    public string? OpenModuleId { get; init; }

    /// <summary>Handed to <see cref="IModule.OpenWith"/> of <see cref="OpenModuleId"/>.</summary>
    public string? OpenArgument { get; init; }

    /// <summary>Colors the marker; <see cref="StatusLevel.Good"/> for a plain success.</summary>
    public StatusLevel Level { get; init; } = StatusLevel.Good;
}
