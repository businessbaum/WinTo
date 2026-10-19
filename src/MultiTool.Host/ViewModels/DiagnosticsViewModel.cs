using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Host.Services;

namespace MultiTool.Host.ViewModels;

/// <summary>What is loaded in the process right now.</summary>
/// <remarks>
/// Backs the "modules are not preloaded" claim, and also shows what does <b>not</b> happen:
/// code of a module opened once stays in the process until exit.
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public DiagnosticsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Refresh();
    }

    public ObservableCollection<ContextRow> Contexts { get; } = [];

    public ObservableCollection<ModuleRow> Modules { get; } = [];

    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string StartupText { get; set; } = "";
    [ObservableProperty] public partial string MemoryText { get; set; } = "";

    public bool NoModuleTouched => Modules.Count == 0;

    [RelayCommand]
    public void Refresh()
    {
        var seconds = Program.Uptime.Elapsed.TotalSeconds;
        StartupText = seconds < 120
            ? $"Since start: {seconds:0.0} s"
            : $"Since start: {seconds / 60:0} min";

        Contexts.Clear();
        foreach (var info in LoadedAssemblies.Contexts())
            Contexts.Add(new ContextRow(
                info.Name, info.Assemblies.Count, string.Join("\n", info.Assemblies)));

        Modules.Clear();
        foreach (var history in _shell.Modules.History.OrderBy(h => h.Name))
        {
            var session = _shell.Modules.Find(history.ModuleId);
            Modules.Add(new ModuleRow(
                history.Name,
                history.ModuleId,
                history.Live,
                session?.View is not null,
                session?.Module.Activity.IsBusy == true,
                Describe(history)));
        }

        OnPropertyChanged(nameof(NoModuleTouched));

        var installed = _shell.Catalog.Modules.Count;
        var touched = _shell.Modules.History.Count;
        var live = _shell.Modules.Loaded.Count;

        Summary = $"{LoadedAssemblies.Count} assemblies in {Plural(Contexts.Count, "load context", "load contexts")} · "
            + $"{touched} of {installed} modules touched · {live} with a live instance";

        MemoryText = $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB managed memory · "
            + $"{Environment.WorkingSet / (1024 * 1024)} MB working set";
    }

    private static string Describe(ModuleHistory h)
    {
        var opens = h.Opens == 1 ? "opened once" : $"opened {h.Opens}×";
        var first = $"first load {h.FirstLoadTime.TotalMilliseconds:0} ms";

        // The second open costs almost nothing because the load context stays.
        var again = h.Opens > 1 ? $" · last {h.LastLoadTime.TotalMilliseconds:0} ms" : "";

        return $"{h.ModuleId} · {opens} · {first}{again}";
    }

    private static string Plural(int n, string one, string many) =>
        n == 1 ? $"1 {one}" : $"{n} {many}";
}

public sealed record ContextRow(string Name, int AssemblyCount, string Assemblies)
{
    public string Subtitle => AssemblyCount == 1 ? "1 assembly" : $"{AssemblyCount} assemblies";
}

/// <param name="Live">Whether a module instance exists.</param>
/// <param name="ViewOpen">Whether the view is open.</param>
public sealed record ModuleRow(
    string Name, string Id, bool Live, bool ViewOpen, bool Busy, string Detail)
{
    public string State => (Live, ViewOpen, Busy) switch
    {
        (_, _, true) => "working",
        (true, true, _) => "open",
        (true, false, _) => "loaded, view closed",
        _ => "released",
    };

    public bool IsLive => Live;
}
