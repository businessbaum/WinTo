using System.Runtime.InteropServices;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>A key combination as listed in the picker.</summary>
/// <param name="Modifiers">Combination of <c>MOD_*</c>.</param>
/// <param name="VirtualKey">The key code from <c>VK_*</c>.</param>
public sealed record HotkeyChoice(string Label, uint Modifiers, uint VirtualKey)
{
    /// <summary>Stored in the settings file; the label itself is the key.</summary>
    public string Id => Label;
}

/// <summary>A system-wide hotkey receiver.</summary>
/// <remarks>
/// <para>
/// <b>A copy of the recorder's</b>, with its own key list. <c>IHotkeys</c> is still not in the contract;
/// with two identical receivers now, a shared contract starts to pay off.
/// </para>
/// <para>
/// <b><c>RegisterHotKey</c>, not a keyboard hook.</b> A <c>WH_KEYBOARD_LL</c> hook sees <i>every</i> key press
/// system-wide, runs inside every game and password field, delays input when slow, and is what
/// antivirus and anticheat flag. <c>RegisterHotKey</c> registers one combination; this code never sees
/// anything else.
/// </para>
/// <para>
/// <b>Own thread:</b> with <c>hWnd = 0</c> the hotkey belongs to the calling thread and <c>WM_HOTKEY</c> lands
/// in its message queue. Avalonia's UI loop can't be hooked into; a thread with its own
/// <c>GetMessage</c> loop costs nothing and stays inside this module.
/// </para>
/// <para><b>MOD_NOREPEAT is mandatory:</b> a held key would fire dozens of times a second.</para>
/// </remarks>
internal sealed partial class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const int HotkeyId = 0xC12C;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>What can be chosen.</summary>
    /// <remarks>
    /// <para>
    /// Around Print Screen, where people look for a screenshot key. The bare key is left out on purpose:
    /// Windows 11 can put the Snipping Tool on it, and taking it away breaks a user setting. Alt+PrtScn
    /// too, the built-in active-window shot.
    /// </para>
    /// <para>No Ctrl+Shift+S: "Save as" in almost every program, and RegisterHotKey would take it from all of them.</para>
    /// </remarks>
    public static IReadOnlyList<HotkeyChoice> Choices { get; } =
    [
        new("Ctrl+PrtScn", ModControl, 0x2C),
        new("Shift+PrtScn", ModShift, 0x2C),
        new("Ctrl+Shift+PrtScn", ModControl | ModShift, 0x2C),
        new("Ctrl+Alt+S", ModControl | ModAlt, 0x53),
        new("Ctrl+Alt+F12", ModControl | ModAlt, 0x7B),
    ];

    public static HotkeyChoice Default => Choices[0];

    public static HotkeyChoice ById(string? id) =>
        Choices.FirstOrDefault(c => c.Id == id) ?? Default;

    private Thread? _thread;
    private uint _threadId;
    private volatile bool _stopping;

    /// <summary>Raised from a background thread.</summary>
    public event Action? Pressed;

    /// <summary><c>false</c> if the combination is already taken.</summary>
    /// <remarks>Windows hands out a combination only once; a silent key would be the worst outcome, so it's reported.</remarks>
    public bool IsRegistered { get; private set; }

    public HotkeyChoice? Current { get; private set; }

    /// <summary>Registers the combination, unregistering a previous one first.</summary>
    /// <returns><c>false</c> if Windows didn't grant it.</returns>
    public bool Start(HotkeyChoice choice)
    {
        Stop();

        _stopping = false;
        Current = choice;

        // Not with using: if the wait below times out before the thread sets the event, it would hit
        // a disposed object, an exception on a background thread that ends the whole process.
        var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            IsRegistered = RegisterHotKey(IntPtr.Zero, HotkeyId, choice.Modifiers | ModNoRepeat, choice.VirtualKey);
            ready.Set();

            if (!IsRegistered) return;

            try
            {
                // GetMessage returns 0 on WM_QUIT and -1 on error; both end the loop. A -1 in a while(> 0)
                // loop would spin at full load forever.
                while (!_stopping && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.message == WmHotkey && !_stopping) Pressed?.Invoke();
                }
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
            }
        })
        {
            IsBackground = true,
            Name = "WinTo screenshot key",
        };

        _thread.Start();

        // Wait for the registration result so the caller can report a taken combination. Microseconds.
        ready.Wait(TimeSpan.FromSeconds(2));

        return IsRegistered;
    }

    public void Stop()
    {
        if (_thread is not { } thread) return;

        _stopping = true;

        // The loop sits in GetMessage and doesn't wake without a message.
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        thread.Join(TimeSpan.FromSeconds(2));

        _thread = null;
        _threadId = 0;
        IsRegistered = false;
        Current = null;
    }

    public void Dispose()
    {
        Pressed = null;
        Stop();
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
