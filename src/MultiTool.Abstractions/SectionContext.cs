using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTool.Abstractions;

/// <summary>
/// The context for one section of a module made from several former modules. Since 0.13.0.
/// </summary>
/// <remarks>
/// <para>
/// Since 0.13.0 several tools are one module each (screenshot and recording became "Capture", packages and
/// the uninstaller "Apps"). Their view models were written against their own <see cref="IModuleContext"/>; this
/// gives each section exactly that, so the code moves without rewriting how it stores things.
/// </para>
/// <para>
/// <b>Settings</b> live under the section's name inside the merged module's <c>settings.json</c>. Until the
/// section saves for the first time, <see cref="IModuleSettings.Load{T}"/> reads the former module's file, so
/// nothing set before the merge is lost; that file is left as it was.
/// </para>
/// <para>
/// <b>The data folder</b> stays the former module's (<c>modules\&lt;former id&gt;</c>): a ring buffer, backups and
/// downloaded tools remain where they were.
/// </para>
/// <para>
/// <b>Only one section reports the tile status</b> (<paramref name="publishesStatus"/>); a tile shows one
/// state, and two sections would overwrite each other.
/// </para>
/// </remarks>
public sealed class SectionContext : IModuleContext
{
    private readonly IModuleContext _module;
    private readonly bool _publishesStatus;

    /// <param name="module">The merged module's own context.</param>
    /// <param name="formerId">The id the section had as a module of its own.</param>
    /// <param name="publishesStatus">Whether this section's status goes to the tile.</param>
    public SectionContext(IModuleContext module, string formerId, bool publishesStatus = false)
    {
        _module = module;
        _publishesStatus = publishesStatus;

        var modulesRoot = Path.GetDirectoryName(module.DataDirectory.TrimEnd(Path.DirectorySeparatorChar))
            ?? module.DataDirectory;

        _dataDirectory = Path.Combine(modulesRoot, formerId);
        Settings = new SectionSettings(module.Settings, formerId, Path.Combine(_dataDirectory, "settings.json"));
    }

    public ModuleManifest Manifest => _module.Manifest;

    public string ModuleDirectory => _module.ModuleDirectory;

    private readonly string _dataDirectory;

    /// <summary>Created on first access, like the host's.</summary>
    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(_dataDirectory);
            return _dataDirectory;
        }
    }

    public IModuleSettings Settings { get; }

    public IShell Shell => _module.Shell;

    public IToolLocator Tools => _module.Tools;

    public IModuleLog Log => _module.Log;

    public CancellationToken Shutdown => _module.Shutdown;

    public void PublishStatus(ModuleStatus status)
    {
        if (_publishesStatus) _module.PublishStatus(status);
    }

    /// <summary>The merged module's settings file: one JSON object per section.</summary>
    public sealed class SectionFile
    {
        public Dictionary<string, JsonElement> Sections { get; set; } = [];
    }

    private sealed class SectionSettings(IModuleSettings file, string section, string formerFile) : IModuleSettings
    {
        /// <summary>The same shape the host writes: camelCase, enums as text, read case-insensitively.</summary>
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        public T Load<T>() where T : class, new()
        {
            try
            {
                var all = file.Load<SectionFile>();
                if (all.Sections.TryGetValue(section, out var stored))
                    return stored.Deserialize<T>(Options) ?? new T();

                // Never saved since the merge: what the section had as a module of its own.
                if (File.Exists(formerFile))
                {
                    using var stream = File.OpenRead(formerFile);
                    return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
                }
            }
            catch (Exception)
            {
                // Unreadable settings cost the settings, not the module; the same rule as the host's.
            }

            return new T();
        }

        public void Save<T>(T value) where T : class
        {
            var all = file.Load<SectionFile>();
            all.Sections[section] = JsonSerializer.SerializeToElement(value, Options);
            file.Save(all);
        }
    }
}
