using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>An app whose audio can be sent.</summary>
/// <param name="Key">
/// The process name. Not the process id: that changes on every app restart, the name stays. So the name is
/// stored and resolved only when the tunnel opens.
/// </param>
/// <param name="ProcessId">
/// The process id of the app's <b>topmost</b> process. Only valid for the moment. The capture includes the
/// whole tree below.
/// </param>
/// <param name="HasAudioSession">
/// Has the app already opened the output device? Only a hint, not a requirement; capture works for an app
/// that is still silent.
/// </param>
public sealed record AudioApp(string Key, string Name, int ProcessId, bool HasAudioSession)
{
    /// <summary>Special case "everything except WinTo".</summary>
    public const string WholeDesktopKey = "";

    public static AudioApp WholeDesktop { get; } = new(WholeDesktopKey, "Whole desktop", 0, true);

    public bool IsWholeDesktop => Key.Length == 0;

    /// <summary>Only a placeholder for a remembered app that isn't running.</summary>
    public bool IsOffline => !IsWholeDesktop && ProcessId == 0;
}

/// <summary>Finds the apps whose audio can be sent.</summary>
/// <remarks>
/// <para>
/// WASAPI sessions would be the obvious source, but they're the <b>wrong signal</b>: apps like Spotify open the
/// device only on first playback. Before the first sound they wouldn't be listed, exactly when you want to
/// pick them.
/// </para>
/// <para>
/// So the base is running apps <i>with a visible window</i>, plus anything holding an audio session (that also
/// covers windowless background audio). The capture only needs a valid process id.
/// </para>
/// </remarks>
public static class RunningApps
{
    /// <summary>
    /// Windows shell window hosts. They have a window but are never a useful audio source. If one does play
    /// audio it's still captured; the list only applies when silent.
    /// </summary>
    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "TextInputHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "LockApp",
    };

    /// <summary>All selectable apps, sorted by name. WinTo itself is excluded.</summary>
    public static List<AudioApp> List()
    {
        var self = Environment.ProcessId;
        var withAudio = SessionProcessIds(self);
        var groups = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // PID 0 and 4 are idle and kernel, not apps.
                if (process.Id <= 4 || process.Id == self) continue;

                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var hasSession = withAudio.Contains(process.Id);
                var hasWindow = process.MainWindowHandle != IntPtr.Zero &&
                                !string.IsNullOrWhiteSpace(process.MainWindowTitle);

                // Anything without window and audio would only be noise: services, drivers, background processes.
                if (!hasSession && !hasWindow) continue;
                if (!hasSession && ShellHosts.Contains(name)) continue;

                var candidate = new Candidate(process.Id, hasWindow, StartTime(process), hasSession);

                if (!groups.TryGetValue(name, out var existing)) groups[name] = candidate;
                else groups[name] = Merge(existing, candidate);
            }
            catch (Exception)
            {
                // Process exited meanwhile or not readable; skip.
            }
            finally
            {
                process.Dispose();
            }
        }

        var result = groups
            .Select(pair => new AudioApp(
                pair.Key,
                Describe(pair.Value.ProcessId, pair.Key),
                pair.Value.ProcessId,
                pair.Value.HasSession))
            .ToList();

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>
    /// Merges two processes of the same app. The whole process tree is sent later, so the <b>topmost</b>
    /// process must win; otherwise a browser's sibling processes would be left out.
    ///
    /// A window is the best sign of the main process; without one the earliest started wins, since a parent
    /// starts before its children.
    /// </summary>
    private static Candidate Merge(Candidate a, Candidate b)
    {
        var winner = a.HasWindow != b.HasWindow
            ? (a.HasWindow ? a : b)
            : (a.Started <= b.Started ? a : b);

        // A session anywhere in the tree counts for the whole app.
        return winner with { HasSession = a.HasSession || b.HasSession };
    }

    private static DateTime StartTime(Process process)
    {
        try { return process.StartTime; }
        catch (Exception) { return DateTime.MaxValue; }
    }

    /// <summary>Process ids currently holding an audio session on any device.</summary>
    private static HashSet<int> SessionProcessIds(int self)
    {
        var result = new HashSet<int>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    Collect(device, self, result);
                }
            }
        }
        catch (Exception)
        {
            // Without this extra information the list is only slightly less precise.
        }

        return result;
    }

    private static void Collect(MMDevice device, int self, HashSet<int> result)
    {
        SessionCollection sessions;
        try
        {
            sessions = device.AudioSessionManager.Sessions;
        }
        catch (Exception)
        {
            return; // device not responding; next
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            try
            {
                var session = sessions[i];
                if (session.IsSystemSoundsSession) continue;
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                var pid = (int)session.GetProcessID;
                if (pid > 4 && pid != self) result.Add(pid);
            }
            catch (Exception)
            {
                // Session not readable; skip.
            }
        }
    }

    /// <summary>
    /// The display name. The process name is often cryptic ("msedgewebview2"), the file description readable.
    /// It isn't available for other users' or elevated processes; then the process name stays.
    /// </summary>
    private static string Describe(int processId, string fallback)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception)
        {
            // No access to the module; not a problem.
        }

        return fallback;
    }

    private readonly record struct Candidate(int ProcessId, bool HasWindow, DateTime Started, bool HasSession);
}
