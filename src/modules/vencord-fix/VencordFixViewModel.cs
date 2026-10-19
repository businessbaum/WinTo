using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.VencordFix;

/// <summary>A Discord installation in the list.</summary>
public sealed partial class InstallRow(DiscordInstall install, PatchState state) : ObservableObject
{
    public DiscordInstall Install { get; } = install;

    public PatchState State { get; } = state;

    public string Label => Install.Label;

    public string Path => Install.AppDirectory;

    public bool IsNewest => Install.IsNewest;

    public string StateText => State switch
    {
        PatchState.Patched => "patched",
        PatchState.WrongPath => "invalid reference",
        PatchState.Orphaned => "_app.asar missing",
        PatchState.Vanilla => "unpatched",
        PatchState.NoAsar => "no app.asar",
        _ => "",
    };

    public bool IsGood => State == PatchState.Patched;

    public bool IsBad => State is PatchState.Orphaned or PatchState.NoAsar;

    /// <summary>Whether something needs doing here. Colors the row.</summary>
    public bool NeedsWork => State is PatchState.Vanilla or PatchState.WrongPath;
}

public sealed partial class VencordFixViewModel : ObservableObject
{
    private readonly IModuleContext _context;

    public VencordFixViewModel(IModuleContext context) => _context = context;

    public ObservableCollection<InstallRow> Installs { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] public partial string SummaryText { get; set; } = "checking…";
    [ObservableProperty] public partial string PatcherText { get; set; } = "";
    [ObservableProperty] public partial string VencordText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool AnythingToFix { get; set; }
    [ObservableProperty] public partial bool HasInstalls { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    // ------------------------------------------------------------------ read

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            // On a background thread: enumerating app folders and reading every app.asar is disk work,
            // and right after logon none of it is cached.
            var rows = await Task.Run(() =>
                Discord.Find().Select(i => new InstallRow(i, Discord.StateOf(i))).ToList());

            Installs.Clear();
            foreach (var row in rows) Installs.Add(row);

            HasInstalls = rows.Count > 0;

            // Only the newest version per branch counts; Discord no longer uses the older folders.
            var relevant = rows.Where(r => r.IsNewest).ToList();
            var broken = relevant.Count(r => r.NeedsWork || r.IsBad);

            AnythingToFix = relevant.Any(r => r.NeedsWork);

            SummaryText = rows.Count == 0
                ? "No Discord installation found."
                : broken == 0
                    ? $"{relevant.Count} installation(s) patched."
                    : $"{broken} of {relevant.Count} installation(s) need a patch.";

            PatcherText = Discord.PatcherPath
                + (File.Exists(Discord.PatcherPath) ? "" : "   — missing")
                + (Discord.IsCustomBuild ? "   (custom build)" : "");

            VencordText = Vencord.InstalledAt is { } when
                ? $"Vencord files from {when:d} ({Humanize.Ago(when)})"
                : "Vencord files are missing.";

            PublishStatus(rows);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Discord could not be checked.", ex);
            Show(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PublishStatus(IReadOnlyList<InstallRow> rows)
    {
        var relevant = rows.Where(r => r.IsNewest).ToList();

        if (relevant.Count == 0)
        {
            _context.PublishStatus(new ModuleStatus(
                StatusLevel.Neutral, "no Discord", DateTimeOffset.Now));
            return;
        }

        var bad = relevant.Count(r => !r.IsGood);

        _context.PublishStatus(new ModuleStatus(
            bad == 0 ? StatusLevel.Good : StatusLevel.Warn,
            bad == 0 ? "patched" : $"{bad}× unpatched",
            DateTimeOffset.Now,
            string.Join("  ·  ", relevant.Select(r => $"{r.Label}: {r.StateText}"))));
    }

    // ------------------------------------------------------------------ act

    [RelayCommand]
    private async Task FixAsync()
    {
        if (!File.Exists(Discord.PatcherPath))
        {
            var confirmed = await _context.Shell.ConfirmAsync(
                "Vencord missing",
                $"No patcher.js at {Discord.PatcherPath}. Vencord will be downloaded from GitHub.",
                "Download");

            if (!confirmed) return;
            if (!await FetchVencordAsync()) return;
        }

        await RunAsync("Patch", Discord.Patch);
    }

    /// <summary>After a Discord update: patch the newest versions that lost Vencord, and say so on a card.</summary>
    /// <remarks>
    /// Only Vanilla and a broken reference are repaired here. A missing <c>_app.asar</c> needs a reinstall and
    /// is left to the tool's view, as is a missing <c>patcher.js</c>: downloading needs a confirmation.
    /// </remarks>
    public async Task AutoRepairAsync()
    {
        if (!File.Exists(Discord.PatcherPath))
        {
            _context.Log.Warn($"Automatic repair skipped: {Discord.PatcherPath} missing.");
            return;
        }

        var results = await Task.Run(() => Discord.Find()
            .Where(i => i.IsNewest && Discord.StateOf(i) is PatchState.Vanilla or PatchState.WrongPath)
            .Select(i => (Install: i, Result: Discord.Patch(i)))
            .ToList());

        if (results.Count == 0) return;

        foreach (var (_, result) in results) _context.Log.Info(result.Message);

        var patched = results.Where(r => r.Result.Changed).Select(r => r.Install.Label).ToList();
        var failed = results.Where(r => !r.Result.Changed).Select(r => r.Install.Label).ToList();

        _context.Shell.Announce(patched.Count > 0
            ? new Announcement("Vencord kept after the Discord update", string.Join(", ", patched))
            {
                OpenModuleId = _context.Manifest.Id,
            }
            : new Announcement("Discord update removed Vencord", $"{string.Join(", ", failed)} · repair in WinTo")
            {
                OpenModuleId = _context.Manifest.Id,
                Level = StatusLevel.Warn,
            });
    }

    [RelayCommand]
    private async Task UnpatchAsync()
    {
        var confirmed = await _context.Shell.ConfirmAsync(
            "Remove Vencord",
            "Discord then starts without Vencord. "
            + "Plugin settings under %APPDATA%\\Vencord are kept.",
            "Remove", danger: true);

        if (!confirmed) return;

        await RunAsync("Remove", Discord.Unpatch);
    }

    /// <summary>Runs an action over all newest installations.</summary>
    private async Task RunAsync(string what, Func<DiscordInstall, (bool Changed, string Message)> action)
    {
        IsBusy = true;
        Log.Clear();

        try
        {
            var targets = Installs.Where(r => r.IsNewest).Select(r => r.Install).ToList();

            var results = await Task.Run(() => targets.Select(action).ToList());

            foreach (var (_, message) in results) Log.Add(message);

            var changed = results.Count(r => r.Changed);
            Show($"{what}: {changed} of {targets.Count} changed.", error: false);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UpdateVencordAsync()
    {
        if (Discord.IsCustomBuild)
        {
            Show("The patch points to a self-built Vencord and is not overwritten. "
                + "After \"pnpm build\" a Discord restart is enough.", error: true);
            return;
        }

        var running = Discord.Find().Where(Discord.IsRunning).ToList();

        var confirmed = await _context.Shell.ConfirmAsync(
            "Update Vencord",
            "The four Vencord files are downloaded again from GitHub. "
            + (running.Count > 0
                ? "Discord is running; the new version applies after a restart."
                : "Discord is not running."),
            "Download");

        if (!confirmed) return;

        await FetchVencordAsync();
        await RefreshAsync();
    }

    private async Task<bool> FetchVencordAsync()
    {
        IsBusy = true;
        Log.Clear();

        try
        {
            var (ok, lines) = await Vencord.FetchAsync(_context.Shutdown);

            foreach (var line in lines) Log.Add(line);

            Show(ok ? "Vencord files are up to date." : "Not everything could be downloaded.", error: !ok);
            return ok;
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        // Patch first, then start. That is the whole point: per the Squirrel log, hours pass between
        // "Discord created a new folder" and "Discord uses it", and the patch belongs in that window.
        await RunAsync("Patch", Discord.Patch);

        var newest = Installs.FirstOrDefault(r => r.IsNewest && r.Install.Branch.Name == "Stable")
            ?? Installs.FirstOrDefault(r => r.IsNewest);

        if (newest is null)
        {
            Show("No Discord installation found.", error: true);
            return;
        }

        try
        {
            Show(Discord.Launch(newest.Install));
        }
        catch (Exception ex)
        {
            Show($"Discord could not be started: {ex.Message}", error: true);
        }
    }

    [RelayCommand]
    private void OpenVencordFolder()
    {
        Directory.CreateDirectory(Vencord.DistDirectory);
        _context.Shell.RevealInExplorer(Vencord.DistDirectory);
    }

    [RelayCommand]
    private void Reveal(InstallRow? row)
    {
        if (row is not null) _context.Shell.RevealInExplorer(row.Install.Resources);
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }
}
