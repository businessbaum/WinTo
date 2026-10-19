using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Rgb.Sources;

/// <summary>Everything OpenRGB can reach: motherboards, RAM, graphics cards, coolers, strips, most peripherals.</summary>
/// <remarks>
/// <para>
/// <b>Through OpenRGB's SDK server</b> (TCP 6742 on this computer), the way its own clients talk to it. OpenRGB
/// supports hundreds of devices from every maker without their software, which no single vendor SDK does.
/// Motherboard and RAM lighting need OpenRGB running as administrator (SMBus access).
/// </para>
/// <para>
/// <b>Protocol version 3 at most</b> is asked for. Newer servers (OpenRGB 1.0) still describe devices in that
/// layout when the client asks for it, and version 3's layout is the one known field by field here; version 4 adds
/// zone segments that a lighting tool doesn't need.
/// </para>
/// <para>
/// Colors set through OpenRGB stay on the hardware after the connection closes, unlike Dynamic Lighting.
/// OpenRGB fighting a maker's own software (iCUE, G HUB, Synapse) over the same device is known; the remedy is
/// to quit one of them.
/// </para>
/// </remarks>
internal sealed class OpenRgbSource(IModuleContext context) : IRgbSource
{
    public const int Port = 6742;
    public const string WingetId = "OpenRGB.OpenRGB";

    private OpenRgbClient? _client;

    public string Name => "OpenRGB";

    public event Action? Changed;

    public async Task<SourceResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        Close();

        var client = await OpenRgbClient.ConnectAsync("127.0.0.1", Port, TimeSpan.FromMilliseconds(400), cancellationToken)
            .ConfigureAwait(false);

        if (client is null)
        {
            return new SourceResult([], SourceHealth.Missing,
                FindExe(context) is null ? "Not installed" : "Not running");
        }

        try
        {
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            var count = await client.CountAsync(cancellationToken).ConfigureAwait(false);

            var devices = new List<RgbDevice>();
            var seen = new Dictionary<string, int>();
            for (uint i = 0; i < count; i++)
            {
                var data = await client.ControllerAsync(i, cancellationToken).ConfigureAwait(false);
                if (OpenRgbController.Parse(data, client.Protocol) is not { } controller || controller.LedCount == 0) continue;

                var baseKey = $"openrgb:{controller.Name}|{controller.Location}";
                var n = seen.GetValueOrDefault(baseKey);
                seen[baseKey] = n + 1;
                devices.Add(new OpenRgbDevice(client, i, controller, n == 0 ? baseKey : $"{baseKey}#{n}"));
            }

            client.DeviceListUpdated += OnChanged;
            client.Disconnected += OnChanged;
            _client = client;

            return new SourceResult(devices, SourceHealth.Ready,
                devices.Count == 0 ? "Running, no device found yet" : devices.Count == 1 ? "1 device" : $"{devices.Count} devices");
        }
        catch (Exception ex)
        {
            client.Dispose();
            context.Log.Warn($"OpenRGB: {ex.Message}");
            return new SourceResult([], SourceHealth.Problem, ex.Message);
        }
    }

    private void OnChanged() => Changed?.Invoke();

    /// <summary>Where OpenRGB is installed: its installer, winget, or anywhere on the PATH.</summary>
    public static string? FindExe(IModuleContext context)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(local, "Programs", "OpenRGB", "OpenRGB.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "OpenRGB.exe"),
        ];

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        // winget's portable packages: Packages\OpenRGB.OpenRGB_<source>\<folder>\OpenRGB.exe.
        try
        {
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var folder in Directory.EnumerateDirectories(packages, WingetId + "*"))
                {
                    var exe = Directory.EnumerateFiles(folder, "OpenRGB.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 })
                        .FirstOrDefault();
                    if (exe is not null) return exe;
                }
            }
        }
        catch (Exception)
        {
            // An unreadable folder is just not where it is.
        }

        return context.Tools.Find("OpenRGB");
    }

    /// <summary>Starts OpenRGB with its server, minimized to its own tray icon, where it can also be quit.</summary>
    /// <param name="elevated">For motherboard and RAM lighting; Windows asks first.</param>
    public static Task StartAsync(string exe, bool elevated) => Task.Run(() =>
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            Arguments = "--server --gui --startminimized",
            Verb = elevated ? "runas" : "",
        };

        Process.Start(info)?.Dispose();
    });

    private void Close()
    {
        if (_client is not { } client) return;

        _client = null;
        client.DeviceListUpdated -= OnChanged;
        client.Disconnected -= OnChanged;
        client.Dispose();
    }

    public void Dispose() => Close();

    private sealed class OpenRgbDevice : RgbDevice
    {
        private readonly OpenRgbClient _client;
        private readonly uint _index;
        private bool _direct;

        public OpenRgbDevice(OpenRgbClient client, uint index, OpenRgbController controller, string key)
            : base("OpenRGB", key, controller.Name, controller.Kind, controller.LedCount)
        {
            _client = client;
            _index = index;
        }

        public override bool Available => _client.Connected;

        public override string? Note => _client.Connected ? null : "OpenRGB closed the connection";

        public override void Write(RgbColor[] colors)
        {
            // Direct mode first, once: in a hardware effect the device ignores single LEDs.
            if (!_direct)
            {
                _client.Send(_index, OpenRgbClient.SetCustomMode, []);
                _direct = true;
            }

            _client.Send(_index, OpenRgbClient.UpdateLeds, OpenRgbClient.LedPacket(colors));
        }
    }
}

/// <summary>What OpenRGB says about one device; only what a lighting tool needs.</summary>
internal sealed record OpenRgbController(string Name, string Location, RgbKind Kind, int LedCount)
{
    /// <summary>A controller description in protocol 0 to 3. <c>null</c> if it doesn't parse.</summary>
    public static OpenRgbController? Parse(byte[] data, uint protocol)
    {
        try
        {
            var r = new Reader(data);
            r.UInt32();                                 // data size
            var type = r.Int32();
            var name = r.String();
            if (protocol >= 1) r.String();              // vendor
            r.String();                                 // description
            r.String();                                 // version
            r.String();                                 // serial
            var location = r.String();

            var modes = r.UInt16();
            r.Int32();                                  // active mode
            for (var i = 0; i < modes; i++)
            {
                r.String();                             // name
                r.Skip(4 * 4);                          // value, flags, speed min, speed max
                if (protocol >= 3) r.Skip(2 * 4);       // brightness min, max
                r.Skip(3 * 4);                          // colors min, colors max, speed
                if (protocol >= 3) r.Skip(4);           // brightness
                r.Skip(2 * 4);                          // direction, color mode
                r.Skip(r.UInt16() * 4);                 // mode colors
            }

            var zones = r.UInt16();
            for (var i = 0; i < zones; i++)
            {
                r.String();                             // name
                r.Skip(4 * 4);                          // type, leds min, leds max, leds count
                r.Skip(r.UInt16());                     // matrix: its length in bytes, then height, width, map
            }

            var leds = r.UInt16();
            return new OpenRgbController(name, location, KindOf(type), leds);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>OpenRGB's device_type.</summary>
    private static RgbKind KindOf(int type) => type switch
    {
        0 => RgbKind.Motherboard,
        1 => RgbKind.Memory,
        2 => RgbKind.Graphics,
        3 => RgbKind.Cooler,
        4 => RgbKind.Strip,
        5 or 18 => RgbKind.Keyboard,
        6 => RgbKind.Mouse,
        7 => RgbKind.Mousepad,
        8 => RgbKind.Headset,
        10 => RgbKind.Controller,
        12 => RgbKind.Speaker,
        15 => RgbKind.Case,
        16 => RgbKind.Microphone,
        _ => RgbKind.Other,
    };

    private sealed class Reader(byte[] data)
    {
        private int _offset;

        public uint UInt32()
        {
            Need(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_offset));
            _offset += 4;
            return value;
        }

        public int Int32() => unchecked((int)UInt32());

        public ushort UInt16()
        {
            Need(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_offset));
            _offset += 2;
            return value;
        }

        /// <summary>A length (with the terminating zero) and the bytes.</summary>
        public string String()
        {
            var length = UInt16();
            Need(length);
            var text = Encoding.UTF8.GetString(data, _offset, length).TrimEnd('\0');
            _offset += length;
            return text;
        }

        public void Skip(int bytes)
        {
            Need(bytes);
            _offset += bytes;
        }

        private void Need(int bytes)
        {
            if (bytes < 0 || _offset + bytes > data.Length) throw new InvalidDataException("Controller description ends early.");
        }
    }
}

/// <summary>OpenRGB's SDK protocol: a 16-byte header ("ORGB", device index, packet id, size), then the data.</summary>
/// <remarks>
/// Requests are answered with the same packet id; a reader task hands each answer to whoever waits for it.
/// The server also sends "device list updated" on its own when it found more devices.
/// </remarks>
internal sealed class OpenRgbClient : IDisposable
{
    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint DeviceListUpdatedPacket = 100;
    public const uint UpdateLeds = 1050;
    public const uint SetCustomMode = 1100;

    private const uint MaxProtocol = 3;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Lock _write = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<(uint Id, uint Device), TaskCompletionSource<byte[]>> _waiting = new();

    private OpenRgbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _ = Task.Run(ReadLoopAsync);
    }

    public uint Protocol { get; private set; }

    public bool Connected => !_stop.IsCancellationRequested && _tcp.Connected;

    public event Action? DeviceListUpdated;

    public event Action? Disconnected;

    public static async Task<OpenRgbClient?> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            return new OpenRgbClient(tcp);
        }
        catch (Exception)
        {
            tcp.Dispose();
            return null;
        }
    }

    /// <summary>Names the client in OpenRGB's list and agrees on a protocol version; old servers don't answer the second.</summary>
    public async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        Send(0, SetClientName, Encoding.UTF8.GetBytes("WinTo\0"));

        try
        {
            var answer = await RequestAsync(0, RequestProtocolVersion, BitConverter.GetBytes(MaxProtocol),
                TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            Protocol = Math.Min(MaxProtocol, BitConverter.ToUInt32(answer));
        }
        catch (TimeoutException)
        {
            Protocol = 0;
        }
    }

    public async Task<uint> CountAsync(CancellationToken cancellationToken) =>
        BitConverter.ToUInt32(await RequestAsync(0, RequestControllerCount, [], TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false));

    public Task<byte[]> ControllerAsync(uint index, CancellationToken cancellationToken) =>
        RequestAsync(index, RequestControllerData, Protocol > 0 ? BitConverter.GetBytes(Protocol) : [],
            TimeSpan.FromSeconds(5), cancellationToken);

    /// <summary>Data size, color count, then each color as R, G, B and a zero byte.</summary>
    public static byte[] LedPacket(RgbColor[] colors)
    {
        var packet = new byte[4 + 2 + colors.Length * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)colors.Length);
        for (var i = 0; i < colors.Length; i++)
        {
            packet[6 + i * 4] = colors[i].R;
            packet[7 + i * 4] = colors[i].G;
            packet[8 + i * 4] = colors[i].B;
        }

        return packet;
    }

    private async Task<byte[]> RequestAsync(uint device, uint id, byte[] data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiting = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[(id, device)] = waiting;

        try
        {
            Send(device, id, data);
            return await waiting.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _waiting.TryRemove((id, device), out _);
        }
    }

    /// <summary>Sends one packet. Thread-safe: the engine and discovery may send at the same time.</summary>
    public void Send(uint device, uint id, ReadOnlySpan<byte> data)
    {
        var packet = new byte[16 + data.Length];
        "ORGB"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)data.Length);
        data.CopyTo(packet.AsSpan(16));

        lock (_write)
        {
            if (_stop.IsCancellationRequested) throw new IOException("Not connected to OpenRGB.");
            _stream.Write(packet);
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[16];

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new InvalidDataException("Not an OpenRGB packet.");

                var device = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                if (size > 64 * 1024 * 1024) throw new InvalidDataException("OpenRGB packet too large.");

                var data = new byte[size];
                await _stream.ReadExactlyAsync(data, _stop.Token).ConfigureAwait(false);

                if (id == DeviceListUpdatedPacket) DeviceListUpdated?.Invoke();
                else if (_waiting.TryRemove((id, device), out var waiting)) waiting.TrySetResult(data);
            }
        }
        catch (Exception)
        {
            // Closed by either side; whoever waits learns it below.
        }

        var wasConnected = !_stop.IsCancellationRequested;
        _stop.Cancel();
        foreach (var waiting in _waiting.Values) waiting.TrySetException(new IOException("OpenRGB closed the connection."));
        if (wasConnected) Disconnected?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _tcp.Dispose();
    }
}
