using System.Runtime.InteropServices;

namespace MultiTool.Host.Services;

/// <summary>Takes over shortcuts Windows owns itself: Win+Shift+S (Snipping Tool) and Win+V (clipboard history).</summary>
/// <remarks>
/// <para>
/// <b>A low-level hook, not <c>RegisterHotKey</c>:</b> Explorer already owns these combinations, and a second
/// registration is refused. The hook sees the key before hotkeys are processed; swallowing it keeps Windows
/// from opening its own tool. PowerToys remaps combinations the same way. Everything else goes through
/// <see cref="Hotkeys"/>, which never sees a key it didn't ask for.
/// </para>
/// <para>
/// <b>One hook for all of them, on its own thread with its own message loop</b>, like the soundboard's hook:
/// Windows waits for the hook on every key press system-wide. The callback only compares key codes against
/// a small array; it runs while at least one shortcut is set.
/// </para>
/// <para>
/// <b>The Win key is masked</b> with an unassigned key (0xE8, as AutoHotkey does), so releasing Win after the
/// swallowed key doesn't open the Start menu.
/// </para>
/// </remarks>
internal static unsafe partial class WinKeyShortcuts
{
    public const int KeyS = 0x53;
    public const int KeyV = 0x56;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmQuit = 0x0012;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const ushort VkMask = 0xE8;

    private sealed record Entry(int Key, bool Shift, Action Pressed);

    private static readonly Lock Gate = new();
    private static Entry[] _entries = [];
    private static Thread? _thread;
    private static uint _threadId;
    private static int _swallowing;

    /// <summary>Win (+Shift) + <paramref name="key"/> calls <paramref name="pressed"/> instead of Windows' own action.</summary>
    /// <param name="pressed">Called on a pool thread; switch to the UI thread yourself.</param>
    public static void Set(int key, bool shift, Action pressed)
    {
        lock (Gate)
        {
            _entries = [.. _entries.Where(e => e.Key != key), new Entry(key, shift, pressed)];
            EnsureHook();
        }
    }

    /// <summary>Gives the combination back to Windows. The hook ends with the last one.</summary>
    public static void Remove(int key)
    {
        lock (Gate)
        {
            _entries = [.. _entries.Where(e => e.Key != key)];
            if (_entries.Length == 0) StopHook();
        }
    }

    private static void EnsureHook()
    {
        if (_thread is not null) return;

        // Not with using: if the wait times out first, the thread would set a disposed event.
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var hook = SetWindowsHookExW(WhKeyboardLl, &Callback, GetModuleHandleW(null), 0);
            ready.Set();
            if (hook == 0) return;

            try
            {
                while (GetMessageW(out _, 0, 0, 0) > 0) { }
            }
            finally
            {
                UnhookWindowsHookEx(hook);
            }
        })
        {
            IsBackground = true,
            Name = "WinTo Windows shortcuts",
            Priority = ThreadPriority.Highest,
        };

        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        _thread = thread;
    }

    private static void StopHook()
    {
        if (_thread is null) return;

        PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
    }

    /// <summary>Runs for every key press system-wide; must not throw.</summary>
    [UnmanagedCallersOnly]
    private static nint Callback(int code, nint message, nint data)
    {
        try
        {
            if (Handle(code, message, data)) return 1;
        }
        catch (Exception)
        {
            // A failure here must never cost the user a key press.
        }

        return CallNextHookEx(0, code, message, data);
    }

    /// <returns><c>true</c> to swallow the key.</returns>
    private static bool Handle(int code, nint message, nint data)
    {
        if (code < 0) return false;

        // Injected presses count too: macro pads send these combinations that way. The mask key is never set.
        var info = (KeyboardInfo*)data;
        var key = (int)info->VirtualKey;
        var down = message is WmKeyDown or WmSysKeyDown;

        // Auto-repeat and the matching key-up belong to the swallowed press.
        if (_swallowing != 0)
        {
            if (key != _swallowing) return false;
            if (!down) _swallowing = 0;
            return true;
        }

        if (!down) return false;

        foreach (var entry in _entries)
        {
            if (entry.Key != key) continue;
            if (!(Down(VkLWin) || Down(VkRWin)) || Down(VkControl) || Down(VkMenu) || Down(VkShift) != entry.Shift) continue;

            _swallowing = key;
            MaskWinKey();
            ThreadPool.QueueUserWorkItem(_ => entry.Pressed());
            return true;
        }

        return false;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>Down and up of an unassigned key, so the Win release counts as part of a combination.</summary>
    private static void MaskWinKey()
    {
        var inputs = stackalloc Input[2];
        inputs[0] = new Input { Type = 1, VirtualKey = VkMask };
        inputs[1] = new Input { Type = 1, VirtualKey = VkMask, Flags = 0x2 /* KEYEVENTF_KEYUP */ };
        _ = SendInput(2, inputs, sizeof(Input));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInfo
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>INPUT with KEYBDINPUT: 40 bytes on x64 (4 type, 4 padding, 32 union).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
