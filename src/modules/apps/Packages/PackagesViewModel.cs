using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Apps.Packages;

/// <summary>A row in one of the three lists.</summary>
public sealed partial class PackageRow(Package package) : ObservableObject
{
    public Package Package { get; } = package;

    public string Name => Package.Name;
    public string Id => Package.Id;
    public string VersionText => Package.VersionText;
    public string Source => Package.Source;
    public bool IsManaged => Package.IsManaged;
    public bool HasUpdate => Package.HasUpdate;

    /// <summary>Something is running for this package right now.</summary>
    [ObservableProperty] public partial bool IsWorking { get; set; }

    /// <summary>What happened last, shown after the row.</summary>
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    [ObservableProperty] public partial bool StatusIsError { get; set; }

    public bool HasStatus => StatusText.Length > 0;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));
}

public sealed partial class PackagesViewModel : ObservableObject
{
    private readonly IModuleContext _context;
    private readonly List<PackageRow> _installed = [];

    /// <summary>"48.2 MB of 220.3 MB", shown next to the bar.</summary>
    [ObservableProperty] public partial string ProgressText { get; set; } = "";

    /// <summary>The last reported state, for the stall message.</summary>
    private DownloadProgress? _lastProgress;

    /// <summary>Shown in the bar once winget went silent. Empty means all normal.</summary>
    [ObservableProperty] public partial string StallText { get; set; } = "";

    /// <summary>Whether winget still downloads via Delivery Optimization.</summary>
    /// <remarks>Shown as a notice with a button; offered, not set secretly. The file belongs to winget.</remarks>
    [ObservableProperty] public partial bool ShowDownloaderHint { get; set; }

    private CancellationTokenSource? _cancellation;
    private string? _winget;

    public PackagesViewModel(IModuleContext context)
    {
        _context = context;

        Log.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLog));
        Updates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(UpdateCountText));
        };
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<PackageRow> Updates { get; } = [];

    public ObservableCollection<PackageRow> Installed { get; } = [];

    public ObservableCollection<PackageRow> Results { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public bool HasLog => Log.Count > 0;

    public bool HasUpdates => Updates.Count > 0;

    public string UpdateCountText => Updates.Count switch
    {
        0 => "Updates",
        1 => "Updates · 1",
        _ => $"Updates · {Updates.Count}",
    };

    // --------------------------------------------------------------- Sections

    /// <summary>Which section is in front. Starts at <b>Search</b>.</summary>
    /// <remarks>
    /// Search first: it's why one opens a package manager, and typing works right away while the lists
    /// load in the background. The update count is on the tab next to it.
    /// </remarks>
    [ObservableProperty] public partial int Section { get; set; }

    public bool ShowSearch => Section == 0;
    public bool ShowUpdates => Section == 1;
    public bool ShowInstalled => Section == 2;

    partial void OnSectionChanged(int value)
    {
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(ShowUpdates));
        OnPropertyChanged(nameof(ShowInstalled));
    }

    [RelayCommand]
    private void SelectSection(string index)
    {
        if (int.TryParse(index, out var value)) Section = value;
    }

    // --------------------------------------------------------------- State

    [ObservableProperty] public partial string ToolText { get; set; } = "checking…";
    [ObservableProperty] public partial bool IsReady { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial string RunningText { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool ProgressIsKnown { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string SearchHint { get; set; } = "";

    public bool CanElevate => !_context.Shell.IsElevated;

    partial void OnFilterChanged(string value) => ApplyFilter();

    // --------------------------------------------------------------- load

    /// <summary>Finds winget and reads the lists.</summary>
    /// <remarks>After the first frame: <c>winget list</c> enumerates registry, Store packages and sources and takes seconds.</remarks>
    public async Task PrepareAsync()
    {
        _winget = Winget.Find(_context);

        if (_winget is null)
        {
            IsReady = false;
            ToolText = "winget not found. Part of \"App Installer\" from the Microsoft Store.";
            return;
        }

        var version = await Winget.VersionAsync(_winget, _context.Shutdown);

        IsReady = true;
        ToolText = version is null ? "winget found" : $"winget {version}";

        // Task.Run: the file lives under a Store package path, which can stall on first access.
        ShowDownloaderHint = !await Task.Run(Downloader.IsDirect, _context.Shutdown);

        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_winget is null || IsLoading) return;

        IsLoading = true;
        Message = null;

        // Reading is work too (seconds); the tile shows the spinning border meanwhile, even with the
        // view in the background.
        ActivityChanged?.Invoke(this, new ModuleActivity(true, "Reading package list…"));

        try
        {
            // One after another: winget locks its package index, so two calls only wait for each other.
            var upgradable = await Winget.UpgradableAsync(_winget, _context.Shutdown);
            var installed = await Winget.ListAsync(_winget, _context.Shutdown);

            Replace(Updates, upgradable.Select(p => new PackageRow(p)));

            _installed.Clear();
            _installed.AddRange(installed.Select(p => new PackageRow(p)));
            ApplyFilter();

            PublishStatus();
        }
        catch (OperationCanceledException)
        {
            // The app is quitting.
        }
        catch (Exception ex)
        {
            _context.Log.Error("winget could not be queried.", ex);
            Show(ex.Message, error: true);
        }
        finally
        {
            IsLoading = false;

            // Don't report idle while installing; RefreshAsync runs at the end of each install, whose
            // display belongs to it.
            if (!IsWorking) ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    private void ApplyFilter()
    {
        var needle = Filter.Trim();

        var matching = needle.Length == 0
            ? _installed
            : [.. _installed.Where(r =>
                r.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || r.Id.Contains(needle, StringComparison.CurrentCultureIgnoreCase))];

        Replace(Installed, matching);
    }

    private void PublishStatus()
    {
        var count = Updates.Count;

        _context.PublishStatus(new ModuleStatus(
            count == 0 ? StatusLevel.Good : StatusLevel.Warn,
            count == 0 ? "up to date" : count == 1 ? "1 Update" : $"{count} Updates",
            DateTimeOffset.Now,
            count == 0
                ? $"{_installed.Count} programs installed"
                : string.Join(", ", Updates.Take(5).Select(u => u.Name))));
    }

    // --------------------------------------------------------------- search

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_winget is null) return;

        var query = Query.Trim();
        if (query.Length < 2)
        {
            SearchHint = "At least two characters.";
            return;
        }

        Section = 0;
        SearchHint = "Searching…";
        Results.Clear();

        try
        {
            var found = await Winget.SearchAsync(_winget, query, _context.Shutdown);

            Replace(Results, found.Select(p => new PackageRow(p)));

            SearchHint = found.Count == 0
                ? $"Nothing found for \"{query}\"."
                : $"{found.Count} results for \"{query}\".";
        }
        catch (Exception ex)
        {
            SearchHint = ex.Message;
        }
    }

    // --------------------------------------------------------------- act

    [RelayCommand]
    private async Task InstallAsync(PackageRow? row)
    {
        if (row is null) return;
        await RunAsync(row, $"Install {row.Name}", Winget.InstallArguments(row.Id));
    }

    [RelayCommand]
    private async Task UpgradeAsync(PackageRow? row)
    {
        if (row is null) return;
        await RunAsync(row, $"Update {row.Name}", Winget.UpgradeArguments(row.Id));
    }

    [RelayCommand]
    private async Task UpgradeAllAsync()
    {
        if (Updates.Count == 0) return;

        var confirmed = await _context.Shell.ConfirmAsync(
            "Update all",
            $"{Updates.Count} packages are updated one after another. Some "
            + "installers ask for administrator rights themselves.",
            "Update");

        if (!confirmed) return;

        await RunAsync(null, $"Update {Updates.Count} packages", Winget.UpgradeAllArguments());
    }

    [RelayCommand]
    private async Task UninstallAsync(PackageRow? row)
    {
        if (row is null) return;

        var confirmed = await _context.Shell.ConfirmAsync(
            "Uninstall",
            $"\"{row.Name}\" is removed via winget. The uninstaller may ask its own "
            + "questions.",
            "Uninstall", danger: true);

        if (!confirmed) return;

        await RunAsync(row, $"Remove {row.Name}", Winget.UninstallArguments(row.Id));
    }

    /// <summary>Cancels the running winget call.</summary>
    /// <remarks>
    /// <c>StreamAsync</c> kills the whole process tree: the download runs in a separate process, and a
    /// leftover child would keep the half-written file open.
    /// </remarks>
    [RelayCommand]
    private void Cancel()
    {
        if (_cancellation is not { } cancellation) return;

        Append("Cancel requested.");

        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>When winget last said anything.</summary>
    private DateTimeOffset _lastLine;

    /// <summary>Whether the stall was already reported.</summary>
    private bool _stallReported;

    /// <summary>Silence after which a call counts as hanging.</summary>
    /// <remarks>
    /// <b>No timeout on the total duration:</b> a package of hundreds of MB may take long, and a fixed limit
    /// would hit slow connections as much as real hangs. What is measured is <i>silence</i>: winget
    /// prints progress continuously, and two minutes of nothing means it isn't downloading anymore.
    /// </remarks>
    private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(2);

    /// <param name="row">The row the progress belongs to; <c>null</c> for "update all".</param>
    private async Task RunAsync(PackageRow? row, string title, IReadOnlyList<string> arguments)
    {
        if (_winget is null || IsWorking) return;

        IsWorking = true;
        RunningText = title;
        ProgressIsKnown = false;
        Progress = 0;
        ProgressText = "";
        _lastProgress = null;
        Message = null;
        Log.Clear();

        if (row is not null)
        {
            row.IsWorking = true;
            row.StatusText = "";
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        ActivityChanged?.Invoke(this, new ModuleActivity(true, title));

        Append($"winget {string.Join(" ", arguments)}");

        _lastLine = DateTimeOffset.UtcNow;
        _stallReported = false;

        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        watchdog.Tick += (_, _) => CheckStall();
        watchdog.Start();

        try
        {
            var code = await ExternalProcess.StreamAsync(_winget, arguments,
                onOutput: line => Dispatcher.UIThread.Post(() => { NoteLife(); OnLine(line, title); }),
                onError: line => Dispatcher.UIThread.Post(() => { NoteLife(); Append(line); }),
                cancellationToken: _cancellation.Token);

            var ok = code == 0;

            if (row is not null)
            {
                row.StatusIsError = !ok;
                row.StatusText = ok ? "done" : WingetCode(code);
            }

            Show(ok ? $"{title}: done." : $"{title}: {WingetCode(code)}", error: !ok);

            if (ok) await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            if (row is not null) row.StatusText = "cancelled";
            Show($"{title}: cancelled.");
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
        finally
        {
            watchdog.Stop();

            if (row is not null) row.IsWorking = false;

            IsWorking = false;
            RunningText = "";
            ProgressText = "";
            StallText = "";
            _cancellation?.Dispose();
            _cancellation = null;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    /// <summary>winget said something; a visible stall message is outdated now.</summary>
    private void NoteLife()
    {
        _lastLine = DateTimeOffset.UtcNow;

        if (!_stallReported) return;
        _stallReported = false;
        StallText = "";
    }

    private void CheckStall()
    {
        if (!IsWorking || _stallReported) return;

        var still = DateTimeOffset.UtcNow - _lastLine;
        if (still < StallAfter) return;

        _stallReported = true;

        var wo = _lastProgress is { } p ? $" at {p.Text}" : "";

        StallText = $"No response from winget for {(int)still.TotalMinutes} minutes{wo}. "
                    + "The download is probably stuck.";

        Append(StallText);
    }

    /// <summary>Translates exit codes one would otherwise look up.</summary>
    /// <remarks>
    /// winget returns signed 32-bit codes that mean nothing as decimals; the common ones in plain words,
    /// the rest at least in hex so they can be searched.
    /// </remarks>
    private static string WingetCode(int code) => unchecked((uint)code) switch
    {
        0x8A150011 => "No matching package found.",
        0x8A150014 => "Latest version already installed.",
        0x8A15002B => "No update available via winget.",
        0x8A150109 => "The installer needs administrator rights.",
        0x80070005 => "Access denied. Administrator rights required.",
        _ => $"winget failed (0x{unchecked((uint)code):X8}).",
    };

    private void OnLine(string line, string title)
    {
        if (Winget.ReadProgress(line) is { } progress)
        {
            ProgressIsKnown = true;
            Progress = progress.Fraction;
            ProgressText = progress.Text;
            _lastProgress = progress;

            ActivityChanged?.Invoke(this, new ModuleActivity(
                true, $"{title} · {progress.Text}", progress.Fraction));

            return;
        }

        // The bar line consists of block characters and says nothing in text.
        if (line.Contains('█') || line.Trim().All(c => c is '-' or '\\' or '|' or '/')) return;

        Append(line);
    }

    private void Append(string line)
    {
        if (line.Trim().Length == 0) return;

        Log.Add(line);
        while (Log.Count > 200) Log.RemoveAt(0);
    }

    /// <summary>Switches winget to direct downloading.</summary>
    /// <remarks>Applies from the next winget call; the setting is read at start.</remarks>
    [RelayCommand]
    private async Task UseDirectDownloadAsync()
    {
        var problem = await Task.Run(Downloader.MakeDirect, _context.Shutdown);

        if (problem is not null)
        {
            Show($"Setting could not be written: {problem}", error: true);
            return;
        }

        ShowDownloaderHint = false;
        Show("winget downloads directly instead of via Delivery Optimization from the next call on.");
    }

    // --------------------------------------------------------------- Helpers

    [RelayCommand]
    private async Task ElevateAsync()
    {
        if (!await _context.Shell.RunElevatedAsync(_context.Manifest.Id))
            Show("Elevated start cancelled.", error: true);
    }

    [RelayCommand]
    private void OpenStore() =>
        _context.Shell.OpenUrl("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1");

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
