using System.Reflection;
using System.Runtime.Loader;
using MultiTool.Abstractions;

namespace MultiTool.Host.Services;

/// <summary>The load context of a single module.</summary>
/// <remarks>
/// <para>
/// <b>Shared stays shared.</b> <c>Control</c> from a second Avalonia copy is a different type
/// ("Object of type 'Control' cannot be converted to type 'Control'"), so Avalonia, the contract
/// and the UI library always come from the default context.
/// </para>
/// <para>
/// <b>Own stays own.</b> Everything else loads from the module folder via
/// <see cref="AssemblyDependencyResolver"/>, which needs the <c>.deps.json</c> next to the DLL.
/// Two modules may use different versions of a library.
/// </para>
/// <para>
/// <b>Not collectible, on purpose.</b> Once a module view is shown,
/// <c>AvaloniaPropertyRegistry.Instance</c> keeps its type forever, so the context would never
/// be collected, while being collectible would still disable ReadyToRun for the module.
/// Closing frees the module's data (devices, buffers, threads) via <see cref="IDisposable"/>,
/// not its few hundred KB of IL.
/// </para>
/// </remarks>
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    // The resolver is created before the base constructor: that one registers the context in
    // AssemblyLoadContext.All forever, so a resolver failure afterwards (missing .deps.json)
    // would leave an empty, uncollectible context behind on every attempt.
    public ModuleLoadContext(ModuleManifest manifest)
        : this(manifest, new AssemblyDependencyResolver(manifest.AssemblyPath))
    {
    }

    private ModuleLoadContext(ModuleManifest manifest, AssemblyDependencyResolver resolver)
        : base(name: "module:" + manifest.Id, isCollectible: false)
    {
        ModuleId = manifest.Id;
        _resolver = resolver;
    }

    public string ModuleId { get; }

    /// <summary>Assemblies that must come from the default context so host and module share types.</summary>
    /// <remarks>
    /// Short and explicit on purpose. "Share whatever the host has loaded" would silently hand a
    /// module older versions of arbitrary libraries.
    /// </remarks>
    private static bool IsShared(string? name) =>
        name is not null && (
            name.StartsWith("MultiTool.Abstractions", StringComparison.Ordinal) ||
            name.StartsWith("MultiTool.UI", StringComparison.Ordinal) ||
            name.StartsWith("Avalonia", StringComparison.Ordinal) ||
            name.StartsWith("SkiaSharp", StringComparison.Ordinal) ||
            name.StartsWith("HarfBuzzSharp", StringComparison.Ordinal) ||
            name == "CommunityToolkit.Mvvm");

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName.Name))
        {
            // Return the loaded copy explicitly; with null the runtime might find a second copy in
            // the module folder.
            var shared = Default.Assemblies.FirstOrDefault(
                a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.Ordinal));

            return shared ?? Default.LoadFromAssemblyName(assemblyName);
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        // null lets the runtime fall back to the default context, as intended for the framework.
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
