using System.Diagnostics;
using System.Text;

namespace MultiTool.Abstractions;

/// <summary>What a finished process left behind.</summary>
/// <param name="ExitCode">-1 if the process was cancelled.</param>
public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>First non-empty line of stderr, otherwise the exit code.</summary>
    public string Problem =>
        Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is [var first, ..] ? first : $"Exit code {ExitCode}";
}

/// <summary>
/// Starts an external program and listens to it.
/// </summary>
/// <remarks>
/// <para>
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined string,
/// so file names with spaces or quotes stay one argument.
/// </para>
/// <para>
/// yt-dlp and ffmpeg redraw progress with <c>\r</c> and send no <c>\n</c> for minutes.
/// <see cref="PumpAsync"/> therefore splits on both.
/// </para>
/// </remarks>
public static class ExternalProcess
{
    /// <summary>Starts, waits, returns both output streams. For short queries like <c>ffprobe</c>.</summary>
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        var code = await StreamAsync(
            exe, arguments,
            line => output.AppendLine(line),
            line => error.AppendLine(line),
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        return new ProcessResult(code, output.ToString(), error.ToString());
    }

    /// <summary>Starts and reports every line as it arrives.</summary>
    /// <param name="onOutput">Called on a background thread, not the UI thread.</param>
    /// <returns>The exit code, or -1 after cancellation.</returns>
    public static Task<int> StreamAsync(
        string exe,
        IEnumerable<string> arguments,
        Action<string>? onOutput = null,
        Action<string>? onError = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) =>
        StreamAsync(exe, arguments, tieToApplication: false, onOutput, onError, workingDirectory, cancellationToken);

    /// <summary>Same as above, optionally tied to the app's lifetime. Since 0.9.0.</summary>
    /// <param name="tieToApplication">
    /// See <see cref="TieToApplication"/>. For things that run until stopped (a receiver, a
    /// recording), not for installers: aborting those halfway is worse than finishing.
    /// </param>
    public static async Task<int> StreamAsync(
        string exe,
        IEnumerable<string> arguments,
        bool tieToApplication,
        Action<string>? onOutput = null,
        Action<string>? onError = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Otherwise .NET decodes with the console code page and mangles non-ASCII file names.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;

        // Process.Start is synchronous and can take hundreds of ms on first launch (antivirus).
        // Without Task.Run it would run on the caller's thread, often the UI thread.
        using var process = await Task.Run(() => Process.Start(info), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"\"{exe}\" could not be started.");

        if (tieToApplication) TieToApplication(process);

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, onOutput),
            PumpAsync(process.StandardError, onError));

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The whole tree: yt-dlp starts ffmpeg, which would otherwise keep the file open.
            TryKill(process);
            await pumps.ConfigureAwait(false);
            return -1;
        }

        // The last buffer can arrive after exit, and it usually says what went wrong.
        await pumps.ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Starts a program and lets it go; it outlives the app.</summary>
    public static Task<Process?> StartDetachedAsync(string exe, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;

        return Task.Run(() => Process.Start(info));
    }

    /// <summary>Kills a process and its children, ignoring one that already exited.</summary>
    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // It may have exited between HasExited and Kill.
        }
    }

    /// <summary>
    /// Ties a started process to the app's lifetime. Since 0.9.0.
    /// </summary>
    /// <remarks>
    /// Covers crashes: Windows does not link parent and child, so a recording ffmpeg would keep
    /// writing after the app died. A job object with <c>KILL_ON_JOB_CLOSE</c> ends everything
    /// in it when the app's handle closes. If assigning fails, the process just runs untied.
    /// </remarks>
    public static void TieToApplication(Process process) => ApplicationJob.Assign(process);

    /// <summary>
    /// Calls the line callback without letting its exceptions stop reading. Otherwise the pipe
    /// fills up, the child blocks on write and never exits.
    /// </summary>
    private static void Deliver(Action<string> onLine, string line)
    {
        try
        {
            onLine(line);
        }
        catch (Exception)
        {
            // Line lost, stream keeps draining.
        }
    }

    /// <summary>Reads a stream and reports a line at <c>\n</c> and at <c>\r</c>.</summary>
    private static async Task PumpAsync(StreamReader reader, Action<string>? onLine)
    {
        if (onLine is null)
        {
            await reader.ReadToEndAsync().ConfigureAwait(false);
            return;
        }

        var buffer = new char[4096];
        var line = new StringBuilder(256);

        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0) break;

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (c is '\n' or '\r')
                {
                    // An empty line is just the second half of \r\n.
                    if (line.Length > 0)
                    {
                        Deliver(onLine, line.ToString());
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0) Deliver(onLine, line.ToString());
    }
}

/// <summary>The app's single job object. See <see cref="ExternalProcess.TieToApplication"/>.</summary>
internal static partial class ApplicationJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    /// <summary>Never closed on purpose: Windows closes it at process exit, which kills the children.</summary>
    private static readonly Lazy<nint> Handle = new(Create);

    public static void Assign(Process process)
    {
        var job = Handle.Value;
        if (job == 0) return;

        try
        {
            AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception)
        {
            // Already exited, or inside a job that forbids nesting.
        }
    }

    private static nint Create()
    {
        try
        {
            var job = CreateJobObjectW(0, 0);
            if (job == 0) return 0;

            var info = new ExtendedLimitInformation
            {
                Basic = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
            };

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                CloseHandle(job);
                return 0;
            }

            return job;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // Blittable JOBOBJECT_BASIC/EXTENDED_LIMIT_INFORMATION; padding follows nint alignment as in C.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nint MinimumWorkingSetSize;
        public nint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nint ProcessMemoryLimit;
        public nint JobMemoryLimit;
        public nint PeakProcessMemoryUsed;
        public nint PeakJobMemoryUsed;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, nint name);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
