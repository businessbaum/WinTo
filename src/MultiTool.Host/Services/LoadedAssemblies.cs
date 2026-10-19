using System.Runtime.Loader;

namespace MultiTool.Host.Services;

/// <summary>What is loaded in the process right now.</summary>
/// <remarks>
/// "Modules are not preloaded" is a claim about memory, so the diagnostics page shows the
/// load contexts and their assemblies instead of asking to be believed.
/// </remarks>
public static class LoadedAssemblies
{
    /// <summary>Is an assembly with this name loaded in any context?</summary>
    public static bool IsLoaded(string simpleName) =>
        AssemblyLoadContext.All
            .SelectMany(static c => c.Assemblies)
            .Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

    public static int Count =>
        AssemblyLoadContext.All.Sum(static c => c.Assemblies.Count());

    /// <summary>Each load context with its name and assembly names.</summary>
    public static IReadOnlyList<ContextInfo> Contexts() =>
        AssemblyLoadContext.All
            .Select(static c => new ContextInfo(
                c.Name ?? "(unnamed)",
                c.Assemblies.Select(static a => a.GetName().Name ?? "?")
                    .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(static c => c.Name == "Default" ? 0 : 1)
            .ThenBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public sealed record ContextInfo(string Name, IReadOnlyList<string> Assemblies);
}
