using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MultiTool.Modules.Video;

/// <summary>Windows' own media player (MFPlay) in a native child window: every codec Windows has, decoded by the GPU.</summary>
/// <remarks>
/// <para>
/// <b>Why not draw frames ourselves:</b> decoding with ffmpeg into bitmaps and keeping sound in sync is a player
/// of its own. MFPlay plays what the Films &amp; TV app plays (H.264, HEVC and AV1 with the Store extensions,
/// VP9, MP4, MKV, MOV, AVI, WMV) with hardware decoding, into a window handle.
/// </para>
/// <para>
/// <b>COM through the vtable</b>, no interop assembly: the player lives in a module's load context, where
/// <c>[ComImport]</c> classes don't activate cleanly. All calls on the UI thread, which created it.
/// </para>
/// <para>
/// <b>The window goes with the view.</b> Avalonia destroys a native child when the view leaves the tree (a tab
/// switch); the player is shut down with it and the view model keeps the position for the next one.
/// </para>
/// </remarks>
internal sealed unsafe partial class MediaPlayer : IDisposable
{
    private nint _player;

    private MediaPlayer(nint player) => _player = player;

    /// <summary>Opens a file paused into <paramref name="window"/>. <c>null</c> with the reason if Windows can't play it.</summary>
    public static (MediaPlayer? Player, string? Problem) Open(nint window, string path)
    {
        _ = MFStartup(0x00020070, 0);

        if (MFPCreateMediaPlayer(null, 0, 0, 0, window, out var player) != 0 || player == 0)
            return (null, "Windows' media player is not available.");

        nint item = 0;
        int result;
        fixed (char* url = path)
        {
            // CreateMediaItemFromURL, synchronous: an unsupported file fails here, not later in silence.
            result = ((delegate* unmanaged[Stdcall]<nint, char*, int, nint, nint*, int>)Vtable(player)[14])(player, url, 1, 0, &item);
        }

        if (result != 0 || item == 0)
        {
            Call(player, 38);   // Shutdown
            Release(player);
            return (null, result == unchecked((int)0xC00D36C4)
                ? "Windows has no codec for this video. The HEVC or AV1 extension from the Microsoft Store may be missing."
                : $"Windows could not open the video (0x{result:X8}).");
        }

        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtable(player)[16])(player, item);   // SetMediaItem
        Release(item);

        // The page background around the picture instead of black bars.
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtable(player)[33])(player, 0x00181110);

        return (new MediaPlayer(player), null);
    }

    public void Play() => Call(_player, 3);

    public void Pause() => Call(_player, 4);

    /// <summary>0 empty, 1 stopped, 2 playing, 3 paused, 4 shut down.</summary>
    public int State
    {
        get
        {
            if (_player == 0) return 4;
            int state;
            return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vtable(_player)[13])(_player, &state) == 0 ? state : 0;
        }
    }

    public bool IsPlaying => State == 2;

    /// <summary>Known once the file is set, a moment after opening.</summary>
    public TimeSpan? Duration => Read(9);

    public TimeSpan Position
    {
        get => Read(8) ?? TimeSpan.Zero;
        set
        {
            if (_player == 0) return;

            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();
            *(ushort*)variant = 20;                      // VT_I8
            *(long*)(variant + 8) = value.Ticks;         // 100-ns units, as TimeSpan
            var position = Guid.Empty;                   // MFP_POSITIONTYPE_100NS
            ((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, int>)Vtable(_player)[7])(_player, &position, variant);
        }
    }

    public float Volume
    {
        set
        {
            if (_player != 0) ((delegate* unmanaged[Stdcall]<nint, float, int>)Vtable(_player)[20])(_player, Math.Clamp(value, 0, 1));
        }
    }

    public bool Muted
    {
        set
        {
            if (_player != 0) ((delegate* unmanaged[Stdcall]<nint, int, int>)Vtable(_player)[24])(_player, value ? 1 : 0);
        }
    }

    public float Rate
    {
        set
        {
            if (_player != 0) ((delegate* unmanaged[Stdcall]<nint, float, int>)Vtable(_player)[10])(_player, value);
        }
    }

    /// <summary>After the window changed size: the picture follows.</summary>
    public void UpdateVideo() => Call(_player, 32);

    private TimeSpan? Read(int slot)
    {
        if (_player == 0) return null;

        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        var position = Guid.Empty;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, int>)Vtable(_player)[slot])(_player, &position, variant) != 0) return null;

        var ticks = *(long*)(variant + 8);
        PropVariantClear(variant);
        return ticks > 0 || slot == 8 ? TimeSpan.FromTicks(ticks) : null;
    }

    public void Dispose()
    {
        if (_player == 0) return;

        Call(_player, 38);   // Shutdown
        Release(_player);
        _player = 0;
    }

    private static nint* Vtable(nint instance) => *(nint**)instance;

    private static void Call(nint instance, int slot)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(instance)[slot])(instance);
    }

    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(instance)[2])(instance);
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplay.dll")]
    private static partial int MFPCreateMediaPlayer(char* url, int startPlayback, uint options, nint callback, nint window, out nint player);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(byte* variant);
}

/// <summary>Hosts the player's window in the Avalonia layout.</summary>
/// <remarks>The native window covers its area: nothing of Avalonia can be drawn on top of the video.</remarks>
public sealed partial class VideoSurface : NativeControlHost
{
    private nint _window;

    /// <summary>The window exists: the view model opens the player into it.</summary>
    public event Action<nint>? WindowCreated;

    /// <summary>The window is about to go: the view model closes the player and remembers the position.</summary>
    public event Action? WindowDestroying;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // A black static child: SS_BLACKRECT paints it while nothing plays.
        _window = CreateWindowExW(0, "STATIC", "", 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000 | 0x4,
            0, 0, 1, 1, parent.Handle, 0, GetModuleHandleW(null), 0);

        var window = _window;
        Dispatcher.UIThread.Post(() => WindowCreated?.Invoke(window), DispatcherPriority.Background);
        return new PlatformHandle(_window, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        WindowDestroying?.Invoke();
        DestroyWindow(control.Handle);
        _window = 0;
    }

    /// <summary>Raised after the window was resized, so the player can follow.</summary>
    public event Action? Resized;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Dispatcher.UIThread.Post(() => Resized?.Invoke(), DispatcherPriority.Background);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
