using System.Collections.Concurrent;
using Avalonia.Media;

namespace MultiTool.UI;

/// <summary>Turns manifest SVG path data into a <see cref="Geometry"/>.</summary>
/// <remarks>
/// Cached because tiles are rebuilt on every home page filter. An invalid path yields the
/// fallback icon instead of an exception.
/// </remarks>
public static class IconCache
{
    /// <summary>A rounded square, used when a module has no icon.</summary>
    public const string FallbackPath =
        "M6 3h12a3 3 0 0 1 3 3v12a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3Zm0 2a1 1 0 0 0-1 1v12a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1H6Z";

    private static readonly ConcurrentDictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    public static Geometry Get(string? pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData)) return Fallback;

        return Cache.GetOrAdd(pathData, static data =>
        {
            try
            {
                return Geometry.Parse(data);
            }
            catch (Exception)
            {
                return Fallback;
            }
        });
    }

    private static Geometry Fallback { get; } = Geometry.Parse(FallbackPath);
}
