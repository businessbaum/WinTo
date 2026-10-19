using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MultiTool.Host.Services;

/// <summary>Exactly one running app per app folder and session.</summary>
/// <remarks>
/// <para>
/// A second instance is not harmless: a second keyboard hook for the soundboard, the clip key
/// registered twice, the same audio devices, the same settings files.
/// </para>
/// <para>
/// The second instance forwards its command line through a named pipe and exits;
/// <c>--open &lt;id&gt;</c> opens the module there, a bare start brings the window forward.
/// </para>
/// <para>
/// Per app folder, not global: the installed and the development build are two programs.
/// </para>
/// <para>
/// The pipe gets its own ACL: owned by an elevated first instance, "Everyone" could only read
/// and an unelevated second instance could not hand over. The ACL grants write access to the
/// current user account, with or without rights.
/// </para>
/// </remarks>
internal static partial class SingleInstance
{
    private static Mutex? _mutex;
    private static readonly Lock Gate = new();
    private static readonly Queue<string[]> Early = new();
    private static Action<string[]>? _handler;

    private static string Key
    {
        get
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.AppDirectory.ToLowerInvariant()));
            return Convert.ToHexString(bytes, 0, 8);
        }
    }

    private static string PipeName => $"WinTo-{Key}-{Process.GetCurrentProcess().SessionId}";

    /// <summary>Tries to become the first instance.</summary>
    /// <returns><c>false</c> if one is already running.</returns>
    public static bool TryAcquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, $@"Local\WinTo-{Key}", out var createdNew);
            if (!createdNew)
            {
                // Existing is not necessarily taken: a crashed instance leaves an abandoned mutex that may
                // be taken over.
                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        mutex.Dispose();
                        return false;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // Taken over.
                }
            }

            _mutex = mutex;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex belongs to an elevated instance and can't even be opened unelevated. Same answer:
            // one is running.
            return false;
        }
        catch (Exception)
        {
            // Without a mutex the app still starts; two instances beat none.
            return true;
        }
    }

    /// <summary>Releases the mutex. Before an elevated restart and at exit.</summary>
    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (Exception)
        {
            // Not owned or already released.
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>Forwards the command line to the running instance.</summary>
    /// <returns><c>false</c> if it doesn't answer.</returns>
    public static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);

            // Allow the first instance to come to the foreground. Windows only hands the foreground to
            // whoever has it, which is this instance because it was just clicked.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var serverPid))
                AllowSetForegroundWindow(serverPid);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Accepts handovers until the app exits. Starts right away; anything arriving before
    /// <see cref="SetHandler"/> is kept.
    /// </summary>
    public static void Listen(CancellationToken cancellationToken)
    {
        var thread = new Thread(() => Loop(cancellationToken))
        {
            IsBackground = true,
            Name = "WinTo single instance",
        };
        thread.Start();
    }

    /// <summary>From now on handovers go to <paramref name="handler"/>, on a background thread.</summary>
    public static void SetHandler(Action<string[]> handler)
    {
        string[][] early;
        lock (Gate)
        {
            _handler = handler;
            early = [.. Early];
            Early.Clear();
        }

        foreach (var args in early) handler(args);
    }

    private static void Loop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                server.WaitForConnectionAsync(cancellationToken).GetAwaiter().GetResult();

                using var buffer = new MemoryStream();
                server.CopyTo(buffer);

                var args = Encoding.UTF8.GetString(buffer.ToArray())
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                Action<string[]>? handler;
                lock (Gate)
                {
                    handler = _handler;
                    if (handler is null) Early.Enqueue(args);
                }

                handler?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A broken connection is no reason to stop listening. Short pause so a permanently failing
                // setup doesn't spin at full load.
                Thread.Sleep(200);
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();

        if (WindowsIdentity.GetCurrent().User is { } user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, security);
    }

    /// <summary>Waits until a given process has exited.</summary>
    /// <remarks>
    /// For the elevated restart: the new instance starts before the old one exits and would
    /// otherwise hand over to it.
    /// </remarks>
    public static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (Exception)
        {
            // Already exited.
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
