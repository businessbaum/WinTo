using System.Runtime.InteropServices;
using MultiTool.Modules.Soundboard.Models;

namespace MultiTool.Modules.Soundboard.Services;

/// <summary>Global hotkeys via a low-level keyboard hook.</summary>
/// <remarks>
/// <para>
/// Why a hook and not <c>RegisterHotKey</c>: RegisterHotKey takes the key exclusively system-wide. Choosing
/// "F1" or even "B" as a hotkey would make that key unusable everywhere else. The hook only listens and
/// passes the key through by default; blocking is a deliberate setting.
/// </para>
/// <para>
/// <b>The hook lives on its own thread since 0.9.0.</b> On the UI thread it was dangerous: Windows delivers
/// <b>every</b> key press system-wide, including in games, only after this thread answers. Every UI stutter
/// became delayed input elsewhere, and if the thread hangs longer than <c>LowLevelHooksTimeout</c>, Windows
/// silently removes the hook. The soundboard's keys were then dead without any notice.
/// </para>
/// <para>
/// <b>Modifiers are queried, not counted.</b> Until 0.9.0 the hook tracked pressed Ctrl, Alt, Shift and Win
/// itself. A release it didn't see (Win+L, Ctrl+Alt+Del, an elevated window in front) left Ctrl "stuck".
/// <c>GetAsyncKeyState</c> knows the real state.
/// </para>
/// <para>
/// Limitation: if the target app runs elevated (a game as administrator), its key presses don't reach us.
/// Then WinTo must run as administrator too.
/// </para>
/// </remarks>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    private const int VK_ESCAPE = 0x1B;

    private readonly HashSet<int> _pressed = [];
    private readonly Dictionary<Hotkey, Action> _bindings = new();
    private readonly object _gate = new();

    // The delegate must stay alive, otherwise the GC collects the callback and Windows calls
    // into freed memory.
    private readonly LowLevelKeyboardProc _proc;

    private Thread? _thread;
    private uint _threadId;
    private volatile bool _installed;
    private nint _hook;
    private bool _capturing;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _proc = HookCallback;
    }

    /// <summary>Active globally? Keeps listening while a new hotkey is captured.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Swallow bound keys for other programs.</summary>
    public bool SuppressMatched { get; set; }

    public bool IsInstalled => _installed;

    /// <summary>The combination pressed during capture. Null = cancelled.</summary>
    /// <remarks>Comes from the hook thread. Anyone displaying it switches to the UI thread themselves.</remarks>
    public event Action<Hotkey?>? CaptureCompleted;

    /// <summary>Starts the hook thread and installs the hook there. Callable from any thread.</summary>
    public bool Install()
    {
        if (_thread is not null) return _installed;

        // Not with using: if the wait below times out before the thread sets the event, it would
        // hit a disposed object, an exception on a background thread that kills the process.
        var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();

            // PeekMessage creates the message queue before anyone can post WM_QUIT. Without it a very
            // early Uninstall would go nowhere.
            PeekMessage(out _, 0, 0, 0, 0);

            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _installed = _hook != 0;
            ready.Set();

            if (!_installed) return;

            try
            {
                // 0 on WM_QUIT, -1 on error; both end the loop.
                while (GetMessage(out _, 0, 0, 0) > 0)
                {
                }
            }
            finally
            {
                UnhookWindowsHookEx(_hook);
                _hook = 0;
                _installed = false;
            }
        })
        {
            IsBackground = true,
            Name = "Soundboard keyboard hook",

            // Highest priority: Windows waits for this thread on every key press system-wide. It does
            // almost nothing, but must do it immediately, even when a game uses all cores.
            Priority = ThreadPriority.Highest,
        };

        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        return _installed;
    }

    public void Uninstall()
    {
        if (_thread is not { } thread) return;

        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));

        _thread = null;
        _threadId = 0;

        lock (_gate) _pressed.Clear();
    }

    /// <summary>Replaces all bindings at once.</summary>
    public void SetBindings(IEnumerable<(Hotkey Hotkey, Action Action)> bindings)
    {
        lock (_gate)
        {
            _bindings.Clear();
            foreach (var (hotkey, action) in bindings)
            {
                if (hotkey.IsEmpty) continue;
                _bindings[hotkey.Clone()] = action; // later duplicates win
            }
        }
    }

    /// <summary>Whether a hotkey is already bound.</summary>
    public bool IsBound(Hotkey hotkey)
    {
        lock (_gate) return !hotkey.IsEmpty && _bindings.ContainsKey(hotkey);
    }

    public void BeginCapture()
    {
        lock (_gate) _capturing = true;
    }

    public void CancelCapture()
    {
        bool was;
        lock (_gate)
        {
            was = _capturing;
            _capturing = false;
        }

        if (was) CaptureCompleted?.Invoke(null);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode != HC_ACTION) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var message = (int)wParam;
        var vk = Marshal.ReadInt32(lParam);

        var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (!isDown && !isUp) return CallNextHookEx(_hook, nCode, wParam, lParam);

        bool isRepeat;
        lock (_gate)
        {
            if (isUp)
            {
                _pressed.Remove(vk);
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // Repeat only if the key is provably down already. If it's in the list but Windows says
            // it's up, a release was missed; then this is a new press, not an echo of the old one.
            isRepeat = !_pressed.Add(vk) && IsDown(vk);
        }

        if (isRepeat) return CallNextHookEx(_hook, nCode, wParam, lParam);
        if (IsModifier(vk)) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var hotkey = new Hotkey { VirtualKey = vk, Modifiers = CurrentModifiers() };

        // --- capture mode --------------------------------------------------
        bool capturing;
        lock (_gate)
        {
            capturing = _capturing;
            _capturing = false;
        }

        if (capturing)
        {
            var result = vk == VK_ESCAPE ? null : hotkey;

            // Only notify; the actual work happens outside the hook.
            CaptureCompleted?.Invoke(result);
            return 1; // This key is ours, it must not slip through.
        }

        // --- normal triggering ---------------------------------------------
        if (!Enabled) return CallNextHookEx(_hook, nCode, wParam, lParam);

        Action? action;
        lock (_gate) _bindings.TryGetValue(hotkey, out action);

        if (action is null) return CallNextHookEx(_hook, nCode, wParam, lParam);

        // action() only enqueues and returns immediately (see SerialWorkQueue).
        action();

        return SuppressMatched ? 1 : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>The modifiers as Windows currently sees them.</summary>
    /// <remarks>
    /// In a low-level hook <c>GetAsyncKeyState</c> shows the state <b>before</b> the reported event, exactly right for
    /// a modifier pressed earlier.
    /// </remarks>
    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;

        if (IsDown(0x11)) modifiers |= HotkeyModifiers.Control;
        if (IsDown(0x12)) modifiers |= HotkeyModifiers.Alt;
        if (IsDown(0x10)) modifiers |= HotkeyModifiers.Shift;
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= HotkeyModifiers.Win;

        return modifiers;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifier(int vk) => vk is
        0x10 or 0x11 or 0x12 or
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or
        0x5B or 0x5C;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }

    // ================= Win32 =================

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg message, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
