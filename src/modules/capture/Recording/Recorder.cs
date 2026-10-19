using System.Diagnostics;
using System.Text;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Capture.Recording;

/// <summary>A running ffmpeg process that can be ended cleanly.</summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="ExternalProcess"/>.</b> That helper runs a program to completion, the wrong model
/// for something that runs until someone says stop. It also doesn't redirect stdin, which is the point here.
/// </para>
/// <para>
/// <b>Why "q" and not <c>Kill</c>.</b> On a <c>q</c> on stdin ffmpeg finishes the file properly: trailer,
/// indexes, duration. <c>Kill</c> skips all that. Matroska usually stays playable, but "usually" isn't good
/// enough for a recording that can't be repeated. Only after five seconds without reaction is it killed.
/// </para>
/// </remarks>
internal sealed class Recorder : IDisposable
{
    private Process? _process;
    private readonly Lock _gate = new();

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action<string>? Line;

    /// <summary>Raised on a background thread when ffmpeg ends on its own.</summary>
    public event Action<int>? Exited;

    /// <summary>The process whose end was explicitly requested; <see cref="Exited"/> doesn't fire for it.</summary>
    /// <remarks>
    /// Before, the UI had to guess whether an end was intended, and its <c>_stopping</c> guard was already reset
    /// when saving from the ring. A clean stop then looked like a crash. The distinction belongs here, where
    /// the stop is actually triggered.
    /// </remarks>
    private Process? _deliberateStop;

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Set once <see cref="Dispose"/> has run.</summary>
    /// <remarks>
    /// <b>Without it an ffmpeg could be left without an owner.</b> The start runs via <c>Task.Run</c> because
    /// <c>Process.Start</c> can take hundreds of milliseconds the first time (antivirus). If the module is
    /// disposed in exactly that window, <c>Dispose</c> finds no process, and the start then stores its process
    /// in a dead object. ffmpeg would keep recording until someone opens Task Manager.
    /// </remarks>
    private bool _disposed;

    public void Start(string ffmpeg, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        lock (_gate)
        {
            if (_disposed)
                throw new InvalidOperationException("The recorder has already been disposed.");

            if (_process is { HasExited: false })
                throw new InvalidOperationException("A recording is already running.");

            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;

            var process = Process.Start(info)
                ?? throw new InvalidOperationException("ffmpeg could not be started.");

            // ffmpeg's start time passes between entering the lock and here; Dispose may have run
            // meanwhile. Then the new process is ended right away instead of being orphaned.
            if (_disposed)
            {
                _deliberateStop = process;
                ExternalProcess.TryKill(process);
                process.Dispose();
                throw new InvalidOperationException("The recorder was disposed during start.");
            }

            // Tied to the app: if it dies, ffmpeg dies with it. Otherwise a crash left ffmpeg recording
            // until the disk was full; the ring is a folder under %APPDATA% nobody looks at.
            ExternalProcess.TieToApplication(process);

            _process = process;
            StartedAt = DateTimeOffset.Now;

            // ffmpeg writes everything (errors and progress) to stderr. The progress line overwrites
            // itself via \r, so reading is char by char and splits on both.
            _ = Task.Run(() => PumpAsync(process.StandardError));

            // With a safety net: StopAsync and Dispose also dispose the process, so this wait and that
            // disposal run side by side, and ExitCode on a disposed Process throws. Harmless when caught;
            // otherwise an exception on a thread pool thread kills the process.
            _ = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync();

                    bool deliberate;
                    lock (_gate) deliberate = ReferenceEquals(_deliberateStop, process);

                    if (!deliberate) Exited?.Invoke(process.ExitCode);
                }
                catch (Exception)
                {
                }
            });
        }
    }

    /// <summary>Asks ffmpeg to end without waiting.</summary>
    /// <remarks>
    /// For disposal from the UI thread: the request costs nothing and is out even if the app ends right after.
    /// Waiting and the hard rest run on a background thread in <see cref="Dispose"/>.
    /// </remarks>
    public void RequestStop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _deliberateStop = process;
        }

        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Write('q');
                process.StandardInput.Flush();
            }
        }
        catch (Exception)
        {
            // stdin already closed or the process gone; Dispose cleans up.
        }
    }

    /// <summary>Asks ffmpeg to end and waits for the finished file.</summary>
    public async Task<int> StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;

            // This end is intended; Exited has nothing to report.
            _deliberateStop = process;
        }

        if (process is null || process.HasExited) return 0;

        try
        {
            await process.StandardInput.WriteAsync('q');
            await process.StandardInput.FlushAsync();
        }
        catch (Exception)
        {
            // stdin already closed. Only the hard way below remains.
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ExternalProcess.TryKill(process);
            try { await process.WaitForExitAsync(); } catch (Exception) { }
        }

        // Only reset if it's still the same process: if the next run starts during the wait, an
        // unconditional null would forget it, and nobody could end it anymore.
        lock (_gate)
        {
            if (ReferenceEquals(_process, process)) _process = null;
        }

        var code = process.ExitCode;
        process.Dispose();
        return code;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        var line = new StringBuilder(256);

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                return;
            }

            if (read == 0) break;

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (c is '\n' or '\r')
                {
                    if (line.Length > 0)
                    {
                        Line?.Invoke(line.ToString());
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0) Line?.Invoke(line.ToString());
    }

    /// <summary>The emergency exit: asks briefly, then kills.</summary>
    /// <remarks>
    /// <para>
    /// <b>1.5 seconds, not five and not zero.</b> It used to kill immediately because a window that takes five
    /// seconds to close looks like a crash. But the close prompt promised the file would be finished properly,
    /// which wasn't true then.
    /// </para>
    /// <para>
    /// A <c>q</c> and a short deadline solve both: ffmpeg needs milliseconds for the trailer, and past the
    /// deadline Matroska is still playable because it writes continuously. That's why it's the format here.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            // Inside the lock, even when nothing runs: a start still in progress finds the flag and ends
            // its own process.
            _disposed = true;

            process = _process;
            _process = null;

            // This end is intended too.
            _deliberateStop = process;
        }

        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Write('q');
                process.StandardInput.Flush();
                process.WaitForExit(1500);
            }
        }
        catch (Exception)
        {
            // stdin already closed or the process gone. Both lead to the same result below.
        }

        ExternalProcess.TryKill(process);
        process.Dispose();
    }
}
