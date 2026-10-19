using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.Http;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.AirPlay;

/// <summary>Controls an external AirPlay receiver so a Mac's or iPhone's screen arrives on this PC.</summary>
/// <remarks>
/// <para>
/// A receiver means Bonjour announcement, Apple's FairPlay handshake and H.264 plus audio decoding;
/// mature projects do that. This module finds, starts, watches and stops the receiver, the same
/// role the YouTube module plays for yt-dlp.
/// </para>
/// <para>
/// Only this direction: PC to Mac needs an AirPlay <i>sender</i> with Apple's FairPlay keys. The view
/// points to Moonlight and Sunshine instead.
/// </para>
/// </remarks>
public sealed partial class AirPlayViewModel : ObservableObject, IDisposable
{
    /// <summary>Names a receiver may have on the search path.</summary>
    private static readonly string[] KnownReceivers =
        ["airplay-windows", "airplay_receiver", "AirPlayReceiver", "uxplay", "winplay"];

    // A self-contained Windows receiver (12.5 MB, FFmpeg and SDL2 included). Not UxPlay: there is
    // no Windows binary for it, it only builds via MSYS2. The cost is named in the prompt.
    private const string ReceiverUrl =
        "https://github.com/moieric11/AirPlay-Windows/releases/download/v0.1.0/airplay-windows-v0.1.0-x64.zip";
    private const string ReceiverVersion = "airplay-windows v0.1.0";

    /// <summary>SHA-256 of the archive behind <see cref="ReceiverUrl"/>, recorded on 2026-10-14.</summary>
    /// <remarks>
    /// The URL points at a fixed version, but a repository owner can replace the file behind it and
    /// HTTPS won't notice. Trust on first use: the checksum says the file is unchanged since then. A new
    /// version needs a new line here.
    /// </remarks>
    private const string ReceiverSha256 = "e9350ca262ceb3967bda817d09a8d28b45327ec020fb6049cf6453097cfd8bab";
    private const string ReleasesPage = "https://github.com/moieric11/AirPlay-Windows/releases";
    private const string MoonlightUrl = "https://moonlight-stream.org/";

    private readonly IModuleContext _context;

    private CancellationTokenSource? _cts;
    private Task? _run;
    private bool _loading = true;

    public AirPlayViewModel(IModuleContext context)
    {
        _context = context;

        var settings = context.Settings.Load<AirPlaySettings>();
        _receiverPath = settings.ReceiverPath;
        ReceiverName = string.IsNullOrWhiteSpace(settings.ReceiverName)
            ? Environment.MachineName
            : settings.ReceiverName;

        _loading = false;
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    /// <summary>The receiver's last output lines, newest at the bottom.</summary>
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] public partial bool HasLog { get; set; }

    /// <summary>The name the PC appears under in Screen Mirroring.</summary>
    [ObservableProperty] public partial string ReceiverName { get; set; } = "";

    partial void OnReceiverNameChanged(string value) => Save();

    private string? _receiverPath;

    /// <summary>Path to the receiver, or <c>null</c> if none was found.</summary>
    public string? ReceiverPath
    {
        get => _receiverPath;
        private set
        {
            if (SetProperty(ref _receiverPath, value))
            {
                OnPropertyChanged(nameof(ReceiverFound));
                OnPropertyChanged(nameof(ReceiverPathText));
            }
        }
    }

    public bool ReceiverFound => _receiverPath is { Length: > 0 };

    public string ReceiverPathText => _receiverPath ?? "No receiver found.";

    [ObservableProperty] public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";

    [ObservableProperty] public partial bool IsInstalling { get; set; }

    /// <summary>Looks for the receiver in the data folder, module, app and search path.</summary>
    public void Locate()
    {
        if (_receiverPath is { Length: > 0 } && File.Exists(_receiverPath)) return;

        // Data folder first: "Get receiver" unpacks there, the version the user chose.
        if (FindInFolder(_context.DataDirectory) is { } installed)
        {
            ReceiverPath = installed;
            Save();
            return;
        }

        foreach (var name in KnownReceivers)
        {
            if (_context.Tools.Find(name) is { } found)
            {
                ReceiverPath = found;
                Save();
                return;
            }
        }

        StatusText = "No AirPlay receiver. \"Get receiver\" downloads one (12.5 MB), or choose a program.";
    }

    /// <summary>Searches an unpacked package recursively for a receiver EXE.</summary>
    private static string? FindInFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;

            var exes = Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories).ToList();

            // Only names that look like a receiver. "Any EXE in the package" used to start whatever came
            // first, e.g. a bundled ffmpeg.exe.
            return exes.FirstOrDefault(f => KnownReceivers.Any(n =>
                Path.GetFileNameWithoutExtension(f).Contains(n, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task InstallReceiverAsync()
    {
        if (IsInstalling || IsRunning) return;

        // The cost up front: third-party GPL software containing Apple FairPlay responses. Downloading
        // is the user's decision, hence the prompt, and nothing of it in the installer.
        var ok = await _context.Shell.ConfirmAsync(
            "Get AirPlay receiver",
            $"Downloads {ReceiverVersion} (about 12.5 MB) from GitHub into the module's data folder "
            + "and unpacks it there.\n\n"
            + "It is third-party GPL software containing Apple FairPlay material for the AirPlay "
            + "handshake. MultiTool only starts it; nobody here has reviewed it.",
            "Download", danger: false);

        if (!ok) return;

        IsInstalling = true;
        StatusText = "Downloading receiver…";

        try
        {
            var target = await Task.Run(DownloadAndExtractAsync);
            if (target is { Length: > 0 })
            {
                ReceiverPath = target;
                Save();
                StatusText = "Receiver ready.";
            }
            else
            {
                StatusText = "No EXE found in the downloaded package.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
            _context.Log.Error("AirPlay receiver could not be fetched.", ex);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async Task<string?> DownloadAndExtractAsync()
    {
        var root = Directory.CreateDirectory(Path.Combine(_context.DataDirectory, "receiver")).FullName;
        var zip = Path.Combine(root, "receiver.zip");
        var temp = zip + ".part";

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        using (var stream = await http.GetStreamAsync(ReceiverUrl, _context.Shutdown))
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            await stream.CopyToAsync(file, _context.Shutdown);
        }

        if (new FileInfo(temp).Length == 0)
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw new IOException("The response was empty.");
        }

        // Before unpacking: whatever fails here is never unpacked or started.
        string actual;
        await using (var check = File.OpenRead(temp))
        {
            actual = Convert.ToHexString(
                await System.Security.Cryptography.SHA256.HashDataAsync(check, _context.Shutdown));
        }

        if (!string.Equals(actual, ReceiverSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw new IOException("Receiver checksum mismatch. File discarded.");
        }

        // Unpack only after a complete download; an aborted one would count as present next time.
        File.Move(temp, zip, overwrite: true);
        ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
        try { File.Delete(zip); } catch (Exception) { }

        return FindInFolder(root);
    }

    [RelayCommand]
    private async Task ChooseReceiverAsync()
    {
        var picked = await _context.Shell.PickFilesAsync(
            "Choose AirPlay receiver", multiple: false,
            new FilePattern("Program", "exe"));

        if (picked is [var path, ..])
        {
            ReceiverPath = path;
            Save();
            StatusText = "Receiver selected.";
        }
    }

    [RelayCommand]
    private void OpenReleasesPage() => _context.Shell.OpenUrl(ReleasesPage);

    [RelayCommand]
    private void OpenMoonlight() => _context.Shell.OpenUrl(MoonlightUrl);

    [RelayCommand]
    private void Start()
    {
        if (IsRunning || _receiverPath is not { Length: > 0 } exe) return;

        if (!File.Exists(exe))
        {
            StatusText = "The selected file no longer exists.";
            ReceiverPath = null;
            return;
        }

        Log.Clear();
        HasLog = false;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        IsRunning = true;
        StatusText = $"Receiver \"{ReceiverName}\" running. Choose it under Screen Mirroring on the Mac.";
        ActivityChanged?.Invoke(this, new ModuleActivity(true, $"AirPlay receiver \"{ReceiverName}\""));

        var args = new List<string> { "-n", ReceiverName };

        // Tied to the app: after a crash the receiver would keep its ports and announce itself as
        // ready, with no tab and no stop button.
        _run = ExternalProcess.StreamAsync(
            exe, args, tieToApplication: true,
            onOutput: line => Dispatcher.UIThread.Post(() => Append(line)),
            onError: line => Dispatcher.UIThread.Post(() => Append(line)),
            cancellationToken: _cts.Token);

        // If the receiver ends by itself (port conflict, missing runtime), return to idle without
        // losing the exception in an unobserved task.
        _ = _run.ContinueWith(t =>
        {
            var note = t.IsFaulted
                ? $"Receiver could not be started: {t.Exception?.GetBaseException().Message}"
                : "Receiver stopped.";
            Dispatcher.UIThread.Post(() => Stopped(note));
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        StatusText = "Stopping…";
    }

    private void Stopped(string note)
    {
        IsRunning = false;
        StatusText = note;
        ActivityChanged?.Invoke(this, ModuleActivity.Idle);

        _cts?.Dispose();
        _cts = null;
        _run = null;
    }

    private void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        Log.Add(line);
        while (Log.Count > 200) Log.RemoveAt(0);
        HasLog = true;
    }

    private void Save()
    {
        if (_loading) return;

        _context.Settings.Save(new AirPlaySettings
        {
            ReceiverPath = _receiverPath,
            ReceiverName = ReceiverName,
        });
    }

    public void Dispose()
    {
        // The receiver holds the Bonjour name and a port; both go back. Cancel ends the process
        // tree, StreamAsync cleans up.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _run = null;
    }
}

/// <summary>What this module stores in its <c>settings.json</c>.</summary>
public sealed class AirPlaySettings
{
    public string? ReceiverPath { get; set; }

    public string ReceiverName { get; set; } = "";
}
