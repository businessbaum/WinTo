using System.Numerics;
using System.Runtime.InteropServices;

namespace MultiTool.Modules.Rgb.Sources;

/// <summary>Windows Dynamic Lighting (Windows 11): every HID LampArray device, through <c>Windows.Devices.Lights</c>.</summary>
/// <remarks>
/// <para>
/// <b>No software of the maker needed</b> for devices that speak the HID LampArray standard; newer ones from
/// Logitech, Razer, Corsair, ASUS and Microsoft do, some through their maker's service (G HUB's "Logitech LampArray
/// Service", for example).
/// </para>
/// <para>
/// <b>Windows decides who controls the lights</b>: the app in front, or one allowed under Settings → Personalization
/// → Dynamic Lighting → background light control. Elsewhere writes are ignored without an error; <c>IsAvailable</c>
/// says which, and the device's note says so. Releasing the objects gives the lights back to Windows' own effect.
/// </para>
/// </remarks>
internal sealed class DynamicLightingSource : IRgbSource
{
    // From Windows.Devices.winmd.
    private static readonly Guid IidLampArrayStatics = new("7bb8c98d-5fc1-452d-bb1f-4ad410d398ff");
    private static readonly Guid IidDeviceInformationStatics = new("c17f100e-3a46-4a78-8013-769dc9b97390");
    private static readonly Guid IidLampArray2 = new("050c181f-60a8-4711-a1af-1b1b4c658ea2");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly Timer _poll;
    private List<LampDevice> _devices = [];
    private HashSet<string> _ids = [];
    private int _polling;
    private volatile bool _disposed;

    /// <remarks>
    /// Every ten seconds the device list is compared, without opening anything: a wireless mouse that wakes up
    /// or a keyboard plugged in appears, one that sleeps or is unplugged goes. Listing takes milliseconds;
    /// a watcher would need a COM callback object for little more.
    /// </remarks>
    public DynamicLightingSource() => _poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    public string Name => "Windows Dynamic Lighting";

    public event Action? Changed;

    private async void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) == 1) return;

        try
        {
            var ids = await WinRt.RunAsync(ListIds).ConfigureAwait(false);
            if (!_disposed && !ids.SetEquals(_ids)) Changed?.Invoke();
        }
        catch (Exception)
        {
            // Asked again in ten seconds.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public async Task<SourceResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        Release();

        try
        {
            var (devices, ids) = await WinRt.RunAsync(Find).ConfigureAwait(false);
            _devices = devices;
            _ids = ids;
            return new SourceResult([.. devices], SourceHealth.Ready,
                devices.Count == 0 ? "No device found" : devices.Count == 1 ? "1 device" : $"{devices.Count} devices");
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or EntryPointNotFoundException)
        {
            // Before Windows 11 the class doesn't exist.
            return new SourceResult([], SourceHealth.Missing, "Needs Windows 11");
        }
        catch (Exception ex)
        {
            return new SourceResult([], SourceHealth.Problem, ex.Message);
        }
    }

    /// <summary>Opens every LampArray. Also returns all listed ids, those that couldn't be opened included.</summary>
    private static (List<LampDevice> Devices, HashSet<string> Ids) Find()
    {
        var statics = WinRt.Factory("Windows.Devices.Lights.LampArray", IidLampArrayStatics);
        var found = new List<LampDevice>();
        var seen = new Dictionary<string, int>();

        try
        {
            var listed = List(statics);
            foreach (var (id, name) in listed)
            {
                // ILampArrayStatics.FromIdAsync, slot 7. Null when the device went away meanwhile.
                var lamp = WinRt.AwaitWithString(statics, 7, id, Timeout);
                if (lamp == 0) continue;

                var device = LampDevice.Open(lamp, name, seen);
                if (device is null) WinRt.Release(lamp);
                else found.Add(device);
            }

            return (found, [.. listed.Select(d => d.Id)]);
        }
        catch
        {
            foreach (var device in found) device.Close();
            throw;
        }
        finally
        {
            WinRt.Release(statics);
        }
    }

    private static HashSet<string> ListIds()
    {
        var statics = WinRt.Factory("Windows.Devices.Lights.LampArray", IidLampArrayStatics);
        try
        {
            return [.. List(statics).Select(d => d.Id)];
        }
        finally
        {
            WinRt.Release(statics);
        }
    }

    /// <summary>Id and name of every LampArray Windows knows now, without opening one.</summary>
    private static unsafe List<(string Id, string Name)> List(nint statics)
    {
        var infoStatics = WinRt.Factory("Windows.Devices.Enumeration.DeviceInformation", IidDeviceInformationStatics);
        nint collection = 0;

        try
        {
            // ILampArrayStatics.GetDeviceSelector, slot 6.
            var selector = WinRt.ReadString(statics, 6);

            // IDeviceInformationStatics.FindAllAsync(string aqsFilter), slot 10.
            collection = WinRt.AwaitWithString(infoStatics, 10, selector, Timeout);

            // IVectorView<DeviceInformation>: GetAt 6, get_Size 7.
            uint size;
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)WinRt.Slot(collection, 7))(collection, &size));

            var listed = new List<(string, string)>((int)size);
            for (uint i = 0; i < size; i++)
            {
                nint info;
                WinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)WinRt.Slot(collection, 6))(collection, i, &info));

                try
                {
                    // IDeviceInformation: get_Id 6, get_Name 7.
                    listed.Add((WinRt.ReadString(info, 6), WinRt.ReadString(info, 7)));
                }
                finally
                {
                    WinRt.Release(info);
                }
            }

            return listed;
        }
        finally
        {
            WinRt.Release(collection);
            WinRt.Release(infoStatics);
        }
    }

    private void Release()
    {
        var devices = _devices;
        _devices = [];
        if (devices.Count == 0) return;

        // Released on an MTA thread like they were made; Windows' own effect takes the lights back.
        WinRt.RunAsync(() =>
        {
            foreach (var device in devices) device.Close();
            return true;
        }).Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Dispose();
        Release();
    }

    /// <summary>One LampArray; the COM object is held until <see cref="Close"/>.</summary>
    private sealed unsafe class LampDevice : RgbDevice
    {
        private readonly nint _lamp;
        private readonly nint _lamp2;
        private readonly int[] _indices;
        private readonly WinColor[] _colors;
        private readonly TimeSpan _interval;
        private readonly Lock _gate = new();
        private volatile bool _closed;

        private LampDevice(nint lamp, nint lamp2, string key, string name, RgbKind kind, int count, float[] positions, TimeSpan interval)
            : base("Windows Dynamic Lighting", key, name, kind, count)
        {
            _lamp = lamp;
            _lamp2 = lamp2;
            _indices = [.. Enumerable.Range(0, count)];
            _colors = new WinColor[count];
            _interval = interval;
            Positions = positions;
        }

        public override TimeSpan FrameInterval => _interval;

        /// <summary>ILampArray2.get_IsAvailable, slot 6: false while another app has the lights.</summary>
        public override bool Available
        {
            get
            {
                lock (_gate)
                {
                    if (_closed || _lamp2 == 0) return !_closed;

                    byte available;
                    return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)WinRt.Slot(_lamp2, 6))(_lamp2, &available) >= 0
                           && available != 0;
                }
            }
        }

        public override string? Note => Available ? null : "Windows gives the lights to WinTo while it is in front";

        public static LampDevice? Open(nint lamp, string name, Dictionary<string, int> seen)
        {
            // ILampArray: vendor 7, product 8, kind 10, count 11, min update interval 12, bounding box 13.
            ushort vendor, product;
            int kind, count;
            long interval;
            Vector3 box;
            ((delegate* unmanaged[Stdcall]<nint, ushort*, int>)WinRt.Slot(lamp, 7))(lamp, &vendor);
            ((delegate* unmanaged[Stdcall]<nint, ushort*, int>)WinRt.Slot(lamp, 8))(lamp, &product);
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)WinRt.Slot(lamp, 10))(lamp, &kind));
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)WinRt.Slot(lamp, 11))(lamp, &count));
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, long*, int>)WinRt.Slot(lamp, 12))(lamp, &interval));
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, Vector3*, int>)WinRt.Slot(lamp, 13))(lamp, &box));
            if (count <= 0) return null;

            // Lamp positions for the wave: ILampArray.GetLampInfo 20, ILampInfo.get_Position 8.
            var positions = new float[count];
            var width = box.X > 0 ? box.X : 1;
            for (var i = 0; i < count; i++)
            {
                nint info;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)WinRt.Slot(lamp, 20))(lamp, i, &info) < 0 || info == 0)
                {
                    positions[i] = count == 1 ? 0.5f : i / (float)(count - 1);
                    continue;
                }

                Vector3 position;
                var hr = ((delegate* unmanaged[Stdcall]<nint, Vector3*, int>)WinRt.Slot(info, 8))(info, &position);
                positions[i] = hr >= 0 && box.X > 0 ? Math.Clamp(position.X / width, 0, 1) : count == 1 ? 0.5f : i / (float)(count - 1);
                WinRt.Release(info);
            }

            // Two identical mice get numbered keys; the settings tell them apart by order.
            var baseKey = $"lamparray:{vendor:X4}:{product:X4}:{name}";
            var n = seen.GetValueOrDefault(baseKey);
            seen[baseKey] = n + 1;

            // The minimum interval is a floor from the device; at least 30 ms keeps the USB bus calm.
            var frame = TimeSpan.FromTicks(Math.Max(interval, TimeSpan.FromMilliseconds(30).Ticks));

            return new LampDevice(lamp, WinRt.Query(lamp, IidLampArray2), n == 0 ? baseKey : $"{baseKey}#{n}", name,
                KindOf(kind), count, positions, frame);
        }

        /// <summary>LampArrayKind from the metadata.</summary>
        private static RgbKind KindOf(int kind) => kind switch
        {
            1 => RgbKind.Keyboard,
            2 => RgbKind.Mouse,
            3 => RgbKind.Controller,
            7 => RgbKind.Case,
            11 => RgbKind.Headset,
            12 => RgbKind.Microphone,
            13 => RgbKind.Speaker,
            _ => RgbKind.Other,
        };

        // Under the gate: Close from a refresh must not release the object while the engine writes to it.
        public override void Write(RgbColor[] colors)
        {
            lock (_gate) WriteLocked(colors);
        }

        private void WriteLocked(RgbColor[] colors)
        {
            if (_closed) return;

            var same = true;
            for (var i = 0; i < colors.Length; i++)
            {
                _colors[i] = new WinColor(colors[i]);
                same &= colors[i] == colors[0];
            }

            if (same)
            {
                // ILampArray.SetColor, slot 23: Windows.UI.Color by value, four bytes A, R, G, B in one register.
                var packed = 0xFFu | (uint)colors[0].R << 8 | (uint)colors[0].G << 16 | (uint)colors[0].B << 24;
                WinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)WinRt.Slot(_lamp, 23))(_lamp, packed));
                return;
            }

            // ILampArray.SetColorsForIndices, slot 26: (length, Color*, length, int*).
            fixed (WinColor* c = _colors)
            fixed (int* indices = _indices)
            {
                WinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, WinColor*, uint, int*, int>)WinRt.Slot(_lamp, 26))(
                    _lamp, (uint)_colors.Length, c, (uint)_indices.Length, indices));
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                WinRt.Release(_lamp2);
                WinRt.Release(_lamp);
            }
        }

        /// <summary>Windows.UI.Color: A, R, G, B.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private readonly struct WinColor(RgbColor color)
        {
            public readonly byte A = 255;
            public readonly byte R = color.R;
            public readonly byte G = color.G;
            public readonly byte B = color.B;
        }
    }
}
