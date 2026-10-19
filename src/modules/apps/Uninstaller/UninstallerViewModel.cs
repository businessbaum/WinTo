using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Apps.Uninstaller;

public sealed partial class LeftoverRow(Leftover leftover) : ObservableObject
{
    public Leftover Leftover { get; } = leftover;

    public string Path => Leftover.Path;

    public string Reason => Leftover.Reason;

    public string KindText => Leftover.KindText;

    public string SizeText => Leftover.SizeText;

    public bool IsHigh => Leftover.IsHigh;

    /// <summary>Only certain matches are preselected.</summary>
    /// <remarks>
    /// A folder named exactly like the program is certain; one that only contains the name is a guess,
    /// and checked guesses get deleted without anyone looking.
    /// </remarks>
    [ObservableProperty] public partial bool Selected { get; set; } = leftover.IsHigh;
}

public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly IModuleContext _context;
    private List<InstalledProgram> _all = [];
    private CancellationTokenSource? _cancellation;

    public UninstallerViewModel(IModuleContext context)
    {
        _context = context;
        Leftovers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLeftovers));
    }

    public ObservableCollection<InstalledProgram> Programs { get; } = [];

    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];

    public bool HasLeftovers => Leftovers.Count > 0;

    [ObservableProperty] public partial InstalledProgram? Selected { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }
    [ObservableProperty] public partial string? ScannedFor { get; set; }

    public bool CanElevate => !_context.Shell.IsElevated;

    partial void OnSearchChanged(string value) => Filter();

    partial void OnSelectedChanged(InstalledProgram? value)
    {
        // A leftover list belonging to another program is worse than none: its checks would sit on
        // paths nobody is looking at.
        Leftovers.Clear();
        ScannedFor = null;
    }

    // ------------------------------------------------------------------ read

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            // Three registry branches with hundreds of subkeys; on a background thread like all disk work here.
            _all = await Task.Run(SystemPrograms.Read);

            Filter();

            var total = _all.Sum(p => p.SizeBytes);
            SummaryText = $"{_all.Count} programs · {Humanize.Bytes(total)} total";

            _context.PublishStatus(new ModuleStatus(
                StatusLevel.Neutral, $"{_all.Count} programs", DateTimeOffset.Now, SummaryText));
        }
        catch (Exception ex)
        {
            _context.Log.Error("The program list could not be read.", ex);
            Show(ex.Message, error: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Filter()
    {
        var needle = Search.Trim();

        var matching = needle.Length == 0
            ? _all
            : [.. _all.Where(p =>
                p.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || p.Publisher?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) == true)];

        Programs.Clear();
        foreach (var program in matching) Programs.Add(program);
    }

    // ------------------------------------------------------------------ remove

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (Selected is not { } program) return;

        if (!program.CanUninstall)
        {
            Show("No uninstall command registered.", error: true);
            return;
        }

        var confirmed = await _context.Shell.ConfirmAsync(
            "Uninstall",
            $"\"{program.Name}\" is removed by its own uninstaller. "
            + "It may request administrator rights.",
            "Uninstall", danger: true);

        if (!confirmed) return;

        try
        {
            // Task.Run: ShellExecute resolves the file type, maybe shows the elevation prompt and starts the
            // process; on the UI thread the window would freeze meanwhile.
            var process = await Task.Run(() => SystemPrograms.StartUninstall(program));

            if (process is null)
            {
                Show("The uninstaller could not be started.", error: true);
                return;
            }

            Show($"Uninstaller for \"{program.Name}\" running. Waiting for it to finish.");

            await process.WaitForExitAsync(_context.Shutdown);

            // Reload the list but keep the selection: the leftover search comes next and needs name,
            // publisher and install location.
            var wanted = program;
            await RefreshAsync();

            var stillThere = _all.Any(p => p.Key == wanted.Key && p.Name == wanted.Name);

            Selected = null;
            Show(stillThere
                ? $"\"{wanted.Name}\" is still in the list. Uninstall cancelled or incomplete."
                : $"\"{wanted.Name}\" is removed.");

            await ScanAsync(wanted);
        }
        catch (OperationCanceledException)
        {
            // The app is quitting. The uninstaller keeps running, as it should; it isn't ours.
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task ScanSelectedAsync()
    {
        if (Selected is { } program) await ScanAsync(program);
    }

    private async Task ScanAsync(InstalledProgram program)
    {
        if (IsScanning) return;

        IsScanning = true;
        Leftovers.Clear();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);

        try
        {
            var found = await Task.Run(
                () => LeftoverScan.Scan(program, _cancellation.Token), _cancellation.Token);

            foreach (var leftover in found) Leftovers.Add(new LeftoverRow(leftover));

            ScannedFor = program.Name;

            Show(found.Count == 0
                ? $"No leftovers found for \"{program.Name}\"."
                : $"{found.Count} possible leftovers for \"{program.Name}\". Only certain matches are checked.");
        }
        catch (OperationCanceledException)
        {
            Show("Leftover search cancelled.");
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
        finally
        {
            IsScanning = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task RemoveLeftoversAsync()
    {
        var selected = Leftovers.Where(l => l.Selected).Select(l => l.Leftover).ToList();
        if (selected.Count == 0) return;

        var folders = selected.Count(l => l.Kind == LeftoverKind.Folder);
        var keys = selected.Count - folders;
        var bytes = selected.Sum(l => l.SizeBytes);

        var confirmed = await _context.Shell.ConfirmAsync(
            "Remove leftovers",
            $"{folders} folders ({Humanize.Bytes(bytes)}) are moved to the recycle bin.\n"
            + $"{keys} registry keys are deleted, backed up first as a .reg file "
            + "in the data folder.",
            "Remove", danger: true);

        if (!confirmed) return;

        try
        {
            var backupFile = Path.Combine(_context.DataDirectory,
                $"backup-{DateTime.Now:yyyy-MM-dd-HHmmss}.reg");

            var backup = await LeftoverScan.BackupRegistryAsync(
                selected, backupFile, _context.Shutdown);

            // Only delete what is backed up: folders always (the recycle bin is their backup), registry keys
            // only after a successful export.
            var deletable = selected
                .Where(l => l.Kind != LeftoverKind.RegistryKey || !backup.Failed.Contains(l))
                .ToList();

            var (removed, problems) = await Task.Run(() => LeftoverScan.Remove(deletable));

            if (backup.Failed.Count > 0)
                problems.Insert(0,
                    $"{backup.Failed.Count} keys left untouched: the backup failed.");

            // Only remove rows whose leftover is really gone; skipped keys and stuck folders stay visible.
            foreach (var row in Leftovers
                .Where(l => l.Selected && !LeftoverScan.Exists(l.Leftover)).ToList())
            {
                Leftovers.Remove(row);
            }

            var backupNote = backup.File is null ? "" : $" Backup: {Path.GetFileName(backup.File)}.";

            Show(problems.Count == 0
                ? $"{removed} leftovers removed.{backupNote}"
                : $"{removed} removed, {problems.Count} not: {problems[0]}{backupNote}",
                error: problems.Count > 0);
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void OpenBackups()
    {
        Directory.CreateDirectory(_context.DataDirectory);
        _context.Shell.RevealInExplorer(_context.DataDirectory);
    }

    [RelayCommand]
    private async Task ElevateAsync()
    {
        if (!await _context.Shell.RunElevatedAsync(_context.Manifest.Id))
            Show("Elevated start cancelled.", error: true);
    }

    [RelayCommand]
    private void Reveal(InstalledProgram? program)
    {
        if (program?.InstallLocation is { } location && Directory.Exists(location))
            _context.Shell.RevealInExplorer(location);
        else
            Show("No install location registered.", error: true);
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }
}
