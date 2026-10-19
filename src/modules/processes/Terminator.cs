using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiTool.Modules.Processes;

/// <param name="Name">The rung's name in the output.</param>
/// <param name="Ok">Whether it could be <i>executed</i>, not whether the process was gone afterwards.</param>
public sealed record KillStep(string Name, bool Ok, string Detail);

/// <param name="CameBack">The process was gone and came back: not a failure but a different diagnosis, something restarts it.</param>
public sealed record KillResult(
    bool Killed,
    IReadOnlyList<KillStep> Steps,
    string Summary,
    string? Advice,
    bool CameBack = false);

/// <summary>Ends a process, and if that isn't enough, one stage harder.</summary>
/// <remarks>
/// <para><b>Why "End task" sometimes does nothing.</b> Five reasons, each needing a different answer:</para>
/// <list type="number">
///   <item><b>Insufficient rights.</b> A SYSTEM service or another user's process can't be opened for
///         terminate without <c>SeDebugPrivilege</c>. Answer: rung 2.</item>
///   <item><b>Something restarts it</b> (watchdog, service recovery, launcher). It vanishes and is back a
///         second later. Answer: freeze first, then the whole tree, and check afterwards.</item>
///   <item><b>It is protected</b> (PPL/PP): antivirus, anticheat, LSA. Can't be ended from user mode, not
///         even as administrator. Answer: say so instead of trying silently.</item>
///   <item><b>It hangs in the kernel.</b> <c>TerminateProcess</c> reports success, but a thread is stuck in
///         a driver call (hung network drives, USB devices). Nothing in user mode helps. Answer: detect
///         and name it.</item>
///   <item><b>Windows refuses on purpose.</b> Ending a critical process causes a bluescreen. Answer: don't
///         even offer it.</item>
/// </list>
/// <para>
/// The ladder goes through these in order and records which rung worked. <b>What it can't do, it
/// says</b>: "doesn't work, and here's why" is an answer.
/// </para>
/// </remarks>
internal static class Terminator
{
    private const uint ExitCode = 1;

    // ------------------------------------------------------------------ polite

    /// <summary>Asks the process to end itself: <c>WM_CLOSE</c> to its main window.</summary>
    /// <remarks>
    /// What Task Manager does for an app, and the only way unsaved work has a chance. A separate button,
    /// not the first rung: whoever presses "Force" usually tried this already. Via
    /// <see cref="Process.CloseMainWindow"/>, which does exactly that.
    /// </remarks>
    public static async Task<KillResult> CloseAsync(ProcessInfo target, CancellationToken cancellationToken)
    {
        var steps = new List<KillStep>();

        try
        {
            using var process = Process.GetProcessById(target.Id);

            if (!process.CloseMainWindow())
            {
                steps.Add(new KillStep("Close requested", false,
                    "The process has no window."));

                return new KillResult(false, steps,
                    "Closing isn't possible here.",
                    "Without a window only \"Force\" remains. Unsaved work is lost.");
            }

            steps.Add(new KillStep("Close requested", true, "WM_CLOSE sent to the main window."));

            // Five seconds: long enough for a save dialog, short enough that nobody thinks nothing happened.
            var gone = await WaitGoneAsync(target.Id, TimeSpan.FromSeconds(5), cancellationToken);

            return gone
                ? new KillResult(true, steps, $"\"{target.DisplayName}\" has exited.", null)
                : new KillResult(false, steps,
                    "The process received the request and keeps running.",
                    "Usually a dialog is open. Otherwise \"Force\" helps.");
        }
        catch (ArgumentException)
        {
            return new KillResult(true, steps, "The process no longer exists.", null);
        }
        catch (Exception ex)
        {
            steps.Add(new KillStep("Close requested", false, ex.Message));
            return new KillResult(false, steps, "The request could not be sent.", ex.Message);
        }
    }

    // ------------------------------------------------------------------ the ladder

    /// <summary>Ends a process with increasing force until it is gone.</summary>
    /// <param name="withTree">Descendants too; needed against watchdogs.</param>
    /// <remarks>
    /// <para>
    /// <b>The tree comes first, not as a last resort.</b> As the second-to-last rung it never ran: a
    /// <c>cmd</c> with a running <c>ping</c> died on rung 1, the ladder ended, and the <c>ping</c> kept running
    /// as an orphan. "With all child processes" means all, not "all if the first try fails".
    /// </para>
    /// <para>
    /// Freezing <b>before</b> enumerating matters: a running process can start a new child between
    /// enumerating and killing, which is exactly what a watchdog does.
    /// </para>
    /// </remarks>
    public static async Task<KillResult> ForceAsync(
        ProcessInfo target,
        IReadOnlyList<ProcessInfo> all,
        bool withTree,
        bool elevated,
        CancellationToken cancellationToken)
    {
        var frozen = new List<nint>();

        try
        {
            return await LadderAsync(target, all, withTree, elevated, frozen, cancellationToken);
        }
        finally
        {
            // Whatever was frozen and still lives must resume; a frozen process left behind is worse than
            // one not ended. Unconditionally, not only on failure: Killed only says whether the TARGET is
            // gone, and a child that couldn't be ended would stay suspended forever. And in finally: a
            // cancel mid-ladder (module released, app quitting) used to leave everything frozen.
            foreach (var handle in frozen) Resume(handle);
        }
    }

    private static async Task<KillResult> LadderAsync(
        ProcessInfo target,
        IReadOnlyList<ProcessInfo> all,
        bool withTree,
        bool elevated,
        List<nint> frozen,
        CancellationToken cancellationToken)
    {
        var steps = new List<KillStep>();

        // The moment from which a process of the same program counts as "came back".
        var since = DateTime.Now;

        // ---------- first: the two cases where trying would be wrong

        if (target.IsCritical)
        {
            return new KillResult(false, steps,
                $"\"{target.Name}\" is marked critical.",
                "Windows crashes when a process with BreakOnTermination is ended. "
                + "The attempt is therefore not made.");
        }

        if (target.IsProtected)
        {
            steps.Add(new KillStep("Protection checked", false, target.ProtectionText));
        }

        // ---------- rung 0: freeze and remove the descendants

        if (withTree)
        {
            // Suspend the target first, then enumerate: it can't start new children from here on.
            Suspend(target.Id, frozen);

            // Read fresh; the caller's list is up to two seconds old, enough for several new children.
            var fresh = await Task.Run(ProcessList.Read, cancellationToken);
            var current = fresh.FirstOrDefault(p => p.Id == target.Id) ?? target;

            // WinTo itself is never part of the family. Started from the Start menu it is a child of
            // explorer.exe, and "end Explorer with all children" is a common reason to open this module;
            // until 0.9.0 the ladder froze itself and left Explorer suspended forever. Its own children
            // (a running ffmpeg recording, an AirPlay receiver) belong to modules that end them.
            var self = fresh.FirstOrDefault(p => p.Id == Environment.ProcessId);
            var ours = new HashSet<int> { Environment.ProcessId };
            if (self is not null)
                foreach (var child in ProcessList.Descendants(self, fresh)) ours.Add(child.Id);

            var family = ProcessList.Descendants(current, fresh)
                .Where(member => !ours.Contains(member.Id))
                .ToList();

            foreach (var member in family) Suspend(member.Id, frozen);

            steps.Add(new KillStep("Tree frozen", true,
                family.Count == 0
                    ? "The process has no child processes."
                    : $"{frozen.Count} of {family.Count + 1} processes suspended."));

            if (family.Count > 0)
            {
                var killed = family.Count(member => TryTerminate(member.Id).Terminated);

                steps.Add(new KillStep("Descendants ended", killed == family.Count,
                    $"{killed} of {family.Count}: " + string.Join(", ", family.Select(m => m.Name))));
            }
        }

        // ---------- rungs 1 and 2: end, with debug privilege if needed

        var (terminated, denied, detail) = TryTerminate(target.Id);
        steps.Add(new KillStep("End", terminated, detail));

        if (await GoneAsync(target, steps, since, withTree, cancellationToken) is { } done1) return done1;

        if (denied)
        {
            var held = Native.EnsureDebugPrivilege();

            steps.Add(new KillStep("Debug privilege", held,
                held
                    ? "SeDebugPrivilege is active. Any process can be opened with full access."
                    : elevated
                        ? "SeDebugPrivilege could not be enabled. A policy prevents it."
                        : "SeDebugPrivilege is only available with administrator rights."));

            if (held)
            {
                var (again, _, againDetail) = TryTerminate(target.Id);
                steps.Add(new KillStep("End, second try", again, againDetail));

                if (await GoneAsync(target, steps, since, withTree, cancellationToken) is { } done2) return done2;
            }
        }

        // ---------- rung 3: job object

        steps.Add(TryJob(target.Id));
        if (await GoneAsync(target, steps, since, withTree, cancellationToken) is { } done3) return done3;

        // ---------- rung 4: thread by thread

        steps.Add(TryThreads(target.Id));
        if (await GoneAsync(target, steps, since, withTree, cancellationToken) is { } done4) return done4;

        // ---------- everything tried, still there

        return new KillResult(false, steps, $"\"{target.DisplayName}\" keeps running.",
            Diagnose(target, terminated, denied, elevated));
    }

    /// <summary>Why it didn't work: this module's actual answer.</summary>
    private static string Diagnose(ProcessInfo target, bool terminated, bool denied, bool elevated)
    {
        if (target.IsProtected)
        {
            return $"{target.ProtectionText} and can't be ended from user mode, not even "
                 + "as administrator. The protection ends with its service or driver: "
                 + "antivirus via its own UI, anticheat with the game, otherwise in "
                 + "safe mode.";
        }

        if (terminated)
        {
            return "The termination was accepted, but the process keeps running. Usually a "
                 + "thread hangs in a driver call; it only ends when the device is disconnected or "
                 + "on restart.";
        }

        if (denied && !elevated)
        {
            return "Access denied; the app runs without administrator rights. "
                 + "\"As administrator\" at the top right restarts elevated and provides "
                 + "SeDebugPrivilege.";
        }

        if (denied)
        {
            return "Access denied despite administrator rights. A driver protects the "
                 + "process with a kernel callback, common for anticheat and antivirus.";
        }

        return "The reason couldn't be determined. The list above shows what was tried.";
    }

    // ------------------------------------------------------------------ the individual grips

    /// <summary><c>TerminateProcess</c>, and if that fails, <c>NtTerminateProcess</c>.</summary>
    /// <remarks>
    /// One floor lower: kernel32's <c>TerminateProcess</c> just calls it. It makes a difference when
    /// something hooked the kernel32 entry point in this process (antivirus and anticheat libraries do).
    /// No help against a kernel callback; against a user-mode hook it is.
    /// </remarks>
    private static (bool Terminated, bool Denied, string Detail) TryTerminate(int id)
    {
        var handle = Native.OpenProcess(
            Native.ProcessTerminate | Native.Synchronize | Native.ProcessQueryLimitedInformation,
            false, (uint)id);

        if (handle == nint.Zero)
        {
            var error = Marshal.GetLastPInvokeError();

            // 87 here means "this id no longer exists", i.e. the process is already gone.
            if (error == 87) return (true, false, "The process had already exited.");

            var denied = error is 5;
            return (false, denied,
                denied ? "Access denied: no handle with terminate access."
                       : $"Opening failed with error {error}.");
        }

        try
        {
            if (Native.TerminateProcess(handle, ExitCode))
                return (true, false, "TerminateProcess was accepted.");

            var error = Marshal.GetLastPInvokeError();

            try
            {
                if (Native.NtTerminateProcess(handle, (int)ExitCode) == 0)
                    return (true, false, "Ended via NtTerminateProcess; kernel32 was hooked.");
            }
            catch (Exception)
            {
                // Without the function the result above stands.
            }

            return (false, error == 5, $"TerminateProcess failed with error {error}.");
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Suspends a process and keeps the handle.</summary>
    /// <remarks>
    /// <para>
    /// <b>The handle stays open until resume</b>: Windows doesn't reuse a process id while a handle to the
    /// old process is open. Resuming by id used to hit an unrelated process when a frozen one ended
    /// and its number was reused.
    /// </para>
    /// <para>
    /// <b>Never the own process:</b> <c>NtSuspendProcess</c> on itself stops the calling thread too; the app
    /// would hang forever.
    /// </para>
    /// </remarks>
    private static bool Suspend(int id, List<nint> frozen)
    {
        if (id == Environment.ProcessId) return false;

        var handle = Native.OpenProcess(Native.ProcessSuspendResume, false, (uint)id);
        if (handle == nint.Zero) return false;

        try
        {
            if (Native.NtSuspendProcess(handle) == 0)
            {
                frozen.Add(handle);
                return true;
            }
        }
        catch (Exception)
        {
        }

        Native.CloseHandle(handle);
        return false;
    }

    /// <summary>Resumes a frozen process and releases its handle.</summary>
    private static void Resume(nint handle)
    {
        try { Native.NtResumeProcess(handle); }
        catch (Exception) { }
        finally { Native.CloseHandle(handle); }
    }

    /// <summary>Puts the process into a job object and terminates the job.</summary>
    /// <remarks>
    /// <para>
    /// The kill happens in the kernel and takes <b>the process and everything it still starts</b> at once;
    /// the cleanest answer to a watchdog that restarts faster than one can kill. No gap between "child
    /// dead" and "child new".
    /// </para>
    /// <para>Assigning a process already in a job only works since Windows 8 (nested jobs).</para>
    /// </remarks>
    private static KillStep TryJob(int id)
    {
        var job = nint.Zero;
        var process = nint.Zero;

        try
        {
            process = Native.OpenProcess(
                Native.ProcessTerminate | Native.ProcessSetQuota | Native.ProcessQueryLimitedInformation,
                false, (uint)id);

            if (process == nint.Zero)
                return new KillStep("Job object", false, "The process couldn't be opened with quota access.");

            job = Native.CreateJobObject(nint.Zero, nint.Zero);
            if (job == nint.Zero)
                return new KillStep("Job object", false, "The job object couldn't be created.");

            if (!Native.AssignProcessToJobObject(job, process))
                return new KillStep("Job object", false,
                    $"Assignment failed with error {Marshal.GetLastPInvokeError()}.");

            return Native.TerminateJobObject(job, ExitCode)
                ? new KillStep("Job object", true, "Process ended including newly started children.")
                : new KillStep("Job object", false,
                    $"TerminateJobObject failed with error {Marshal.GetLastPInvokeError()}.");
        }
        catch (Exception ex)
        {
            return new KillStep("Job object", false, ex.Message);
        }
        finally
        {
            if (process != nint.Zero) Native.CloseHandle(process);
            if (job != nint.Zero) Native.CloseHandle(job);
        }
    }

    /// <summary>Ends every thread individually.</summary>
    /// <remarks>
    /// The last and dirtiest rung: a process without threads never gets to release anything. It works
    /// when the process can't be opened for terminate but its threads can; an ACL can treat both
    /// separately, and some protection layers only guard the process.
    /// </remarks>
    private static KillStep TryThreads(int id)
    {
        var snapshot = nint.Zero;

        try
        {
            snapshot = Native.CreateToolhelp32Snapshot(Native.Th32SnapThread, 0);
            if (snapshot == nint.Zero || snapshot == -1)
                return new KillStep("Threads one by one", false, "The thread list couldn't be read.");

            var entry = new Native.ThreadEntry { Size = (uint)Marshal.SizeOf<Native.ThreadEntry>() };
            if (!Native.Thread32First(snapshot, ref entry))
                return new KillStep("Threads one by one", false, "The thread list was empty.");

            int found = 0, killed = 0;

            do
            {
                if (entry.OwnerProcessId != (uint)id) continue;
                found++;

                var thread = Native.OpenThread(Native.ThreadTerminate, false, entry.ThreadId);
                if (thread == nint.Zero) continue;

                try
                {
                    if (Native.NtTerminateThread(thread, (int)ExitCode) == 0) killed++;
                }
                catch (Exception)
                {
                }
                finally
                {
                    Native.CloseHandle(thread);
                }
            }
            while (Native.Thread32Next(snapshot, ref entry));

            return new KillStep("Threads one by one", killed > 0, $"{killed} of {found} threads ended.");
        }
        catch (Exception ex)
        {
            return new KillStep("Threads one by one", false, ex.Message);
        }
        finally
        {
            if (snapshot != nint.Zero && snapshot != -1) Native.CloseHandle(snapshot);
        }
    }

    // ------------------------------------------------------------------ checking

    /// <summary>Is the process gone? And if so, does it stay gone?</summary>
    /// <remarks>Returns <c>null</c> while it still runs; then the ladder continues.</remarks>
    private static async Task<KillResult?> GoneAsync(
        ProcessInfo target, List<KillStep> steps, DateTime since, bool withTree,
        CancellationToken cancellationToken)
    {
        if (!await WaitGoneAsync(target.Id, TimeSpan.FromMilliseconds(1200), cancellationToken)) return null;

        // Wait one and a half seconds and look again: the second most common reason for "can't be
        // ended" is that it was ended and something restarted it at once. Without this check the
        // module would report success while the process stays in the list.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);

        if (CameBack(target, since))
        {
            steps.Add(new KillStep("Recheck", false, "A new process of the same program started."));

            return new KillResult(false, steps,
                $"\"{target.DisplayName}\" was ended and running again 1.5 seconds later.",
                withTree
                    ? "The restart comes from outside the tree: a service with "
                      + "recovery, a scheduled task or a second watchdog. "
                      + "Only stopping the service or disabling the task helps."
                    : "A watchdog, a launcher or a service restarts it. "
                      + "\"With all child processes\" helps against the first two.",
                CameBack: true);
        }

        steps.Add(new KillStep("Recheck", true, "The process is ended and was not restarted."));
        return new KillResult(true, steps, $"\"{target.DisplayName}\" was ended.", null);
    }

    /// <summary>Has a new process of the same program appeared?</summary>
    /// <remarks>
    /// <para>
    /// <b>The start time decides, not the name.</b> Comparing name and path reported "came back" when
    /// ending a <c>cmd.exe</c>, because two other <c>cmd.exe</c> had been running for hours. Only what started
    /// <b>after</b> the kill attempt counts.
    /// </para>
    /// <para>A process without readable start time doesn't count; a false "comes back" sends the user hunting a watchdog that doesn't exist.</para>
    /// </remarks>
    private static bool CameBack(ProcessInfo target, DateTime since)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(target.Name))
            {
                using (process)
                {
                    if (process.Id == target.Id) continue;

                    DateTime started;
                    try { started = process.StartTime; }
                    catch (Exception) { continue; }

                    if (started < since) continue;

                    if (target.Path is null) return true;

                    var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)process.Id);
                    if (handle == nint.Zero) continue;

                    try
                    {
                        if (string.Equals(Native.ImagePath(handle), target.Path, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    finally
                    {
                        Native.CloseHandle(handle);
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>Waits until the process has really exited.</summary>
    /// <remarks>
    /// <b>The return value of <c>TerminateProcess</c> isn't enough:</b> it only says the kill was accepted.
    /// That is the "hangs in a driver" case: success reported, process stays, <c>GetExitCodeProcess</c> keeps
    /// answering <c>STILL_ACTIVE</c>.
    /// </remarks>
    private static async Task<bool> WaitGoneAsync(int id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)id);

            if (handle == nint.Zero)
            {
                // Can't be opened means gone, unless access is denied: then it runs and we just can't look.
                if (Marshal.GetLastPInvokeError() != 5) return true;
            }
            else
            {
                try
                {
                    if (Native.GetExitCodeProcess(handle, out var code) && code != Native.StillActive) return true;
                }
                finally
                {
                    Native.CloseHandle(handle);
                }
            }

            if (DateTime.UtcNow >= deadline) return false;

            await Task.Delay(100, cancellationToken);
        }
    }
}
