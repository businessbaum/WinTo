using System.Runtime.InteropServices;

namespace MultiTool.Host.Services;

/// <summary>Keeps the computer awake while the app runs.</summary>
/// <remarks>
/// <para>
/// Only <c>ES_SYSTEM_REQUIRED</c>, not <c>ES_DISPLAY_REQUIRED</c>: downloads and recordings need
/// a running machine, not a lit screen. Screen saver and lock behave as configured.
/// </para>
/// <para>
/// <b>The request belongs to the calling thread</b> and is dropped when that thread ends, so it
/// is only ever set from the UI thread, which lives as long as the app.
/// </para>
/// </remarks>
internal static partial class StayAwake
{
    [Flags]
    private enum ExecutionState : uint
    {
        /// <summary>Applies until revoked, not just for this one call.</summary>
        Continuous = 0x80000000,

        /// <summary>The computer does not go to sleep.</summary>
        SystemRequired = 0x00000001,
    }

    private static bool _active;

    /// <summary>Whether the request is currently set.</summary>
    public static bool IsActive => _active;

    /// <summary>Sets or clears the request. UI thread only.</summary>
    /// <returns><c>false</c> if Windows did not accept it.</returns>
    public static bool Set(bool keepAwake)
    {
        var state = keepAwake
            ? ExecutionState.Continuous | ExecutionState.SystemRequired
            : ExecutionState.Continuous;

        // 0 means not accepted. Rare, but a setting that silently does nothing is worse than none.
        var accepted = SetThreadExecutionState(state) != 0;

        _active = keepAwake && accepted;
        return accepted;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(ExecutionState state);
}
