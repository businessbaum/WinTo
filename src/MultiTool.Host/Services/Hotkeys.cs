using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MultiTool.Host.Services;

/// <summary>A key combination as offered in a feature's options.</summary>
/// <param name="Modifiers">Combination of <c>MOD_*</c>.</param>
/// <param name="VirtualKey">The key code from <c>VK_*</c>.</param>
public sealed record HotkeyChoice(string Label, uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;

    /// <summary>A combination Windows owns itself (Win+V): taken over through <see cref="WinKeyShortcuts"/>.</summary>
    public bool TakesOverWindows { get; init; }

    /// <summary>Win (+Shift) + <paramref name="key"/>, taken from Windows.</summary>
    public static HotkeyChoice FromWindows(string label, uint key, bool shift = false) =>
        new(label, Win | (shift ? Shift : 0), key) { TakesOverWindows = true };

    public override string ToString() => Label;
}

/// <summary>System-wide shortcuts for the app's features, all on one thread.</summary>
/// <remarks>
/// <para>
/// <b><c>RegisterHotKey</c>, not a keyboard hook:</b> each feature registers exactly its combination and this
/// code never sees any other key. Windows hands a combination out only once; a taken one is reported back,
/// so the feature can say so instead of staying silent.
/// </para>
/// <para>
/// <b>One thread with its own message loop</b> owns every registration: with <c>hWnd = 0</c> a hotkey belongs
/// to the registering thread and <c>WM_HOTKEY</c> arrives in its queue. Registering and unregistering are
/// handed to that thread and waited for. Handlers run on it too; they post to the UI thread themselves.
/// </para>
/// <para><b><c>MOD_NOREPEAT</c></b> on every combination: a held key would fire many times a second.</para>
/// </remarks>
internal static partial class Hotkeys
{
    private const int WmHotkey = 0x0312;
    private const int WmApp = 0x8000;
    private const uint ModNoRepeat = 0x4000;

    private static readonly ConcurrentQueue<Action> Work = new();
    private static readonly Dictionary<int, Action> Handlers = [];
    private static readonly object Gate = new();

    private static Thread? _thread;
    private static uint _threadId;
    private static int _nextId = 0x4D00;

    /// <summary>Registers a combination. Returns its id, or <c>null</c> if another program holds it.</summary>
    public static int? Register(HotkeyChoice choice, Action pressed)
    {
        EnsureThread();

        int? result = null;
        Run(() =>
        {
            var id = _nextId++;
            if (!RegisterHotKey(0, id, choice.Modifiers | ModNoRepeat, choice.VirtualKey)) return;

            Handlers[id] = pressed;
            result = id;
        });

        return result;
    }

    public static void Unregister(int? id)
    {
        if (id is not { } value || _thread is null) return;

        Run(() =>
        {
            UnregisterHotKey(0, value);
            Handlers.Remove(value);
        });
    }

    /// <summary>Runs work on the hotkey thread and waits for it; registrations must come from there.</summary>
    private static void Run(Action action)
    {
        var done = new ManualResetEventSlim(false);
        Work.Enqueue(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.Set();
            }
        });

        PostThreadMessage(_threadId, WmApp, 0, 0);
        done.Wait(TimeSpan.FromSeconds(2));
    }

    private static void EnsureThread()
    {
        lock (Gate)
        {
            if (_thread is not null) return;

            // Not disposed: if the wait timed out, the thread would set a disposed event and crash the process.
            var ready = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();

                // Creates the message queue before anyone posts to it.
                PeekMessage(out _, 0, 0, 0, 0);
                ready.Set();

                // GetMessage returns -1 on error; a while (> 0) loop also ends there instead of spinning.
                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    if (message.message == WmHotkey && Handlers.TryGetValue((int)message.wParam, out var handler))
                    {
                        try
                        {
                            handler();
                        }
                        catch (Exception ex)
                        {
                            new ModuleLog("features").Warn($"Hotkey handler failed: {ex.Message}");
                        }
                    }

                    while (Work.TryDequeue(out var work)) work();
                }
            })
            {
                IsBackground = true,
                Name = "WinTo hotkeys",
            };

            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg message, nint window, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out Msg message, nint window, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
