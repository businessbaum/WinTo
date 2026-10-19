using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiTool.Modules.Rgb.Sources;

/// <summary>SteelSeries keyboards, mice, headsets and mousepads, through SteelSeries GG's GameSense server.</summary>
/// <remarks>
/// <para>
/// <b>GameSense is made for games</b>: an app registers, binds an event to color handlers per device type and
/// zone, then sends that event. A handler in <c>context-color</c> mode takes its color from the event's data, so
/// one event carries any color. GG lists no devices; every SteelSeries device that has one of the bound zones
/// takes the color, and they show here as one entry.
/// </para>
/// <para>
/// <b>GG takes the lights back</b> 15 seconds after the last event, so a still color is resent as a heartbeat.
/// "stop_game" on close hands them back at once. Its address is in <c>%PROGRAMDATA%\SteelSeries\SteelSeries
/// Engine 3\coreProps.json</c>; the file stays when GG isn't running, so only an answer counts.
/// </para>
/// </remarks>
internal sealed class SteelSeriesSource : IRgbSource
{
    private const string Game = "WINTO";
    private const string Event = "LIGHT";

    private static readonly string PropsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries Engine 3", "coreProps.json");

    /// <summary>All zones of the standard device types; GG ignores types no connected device has.</summary>
    private static readonly (string Type, string[] Zones)[] FullSet =
    [
        ("rgb-per-key-zones", ["all"]),
        ("mouse", ["wheel", "logo", "base"]),
        ("headset", ["earcups"]),
        ("rgb-1-zone", ["one"]),
        ("rgb-2-zone", ["one", "two"]),
        ("rgb-3-zone", ["one", "two", "three"]),
        ("rgb-4-zone", ["one", "two", "three", "four"]),
        ("rgb-5-zone", ["one", "two", "three", "four", "five"]),
        ("rgb-8-zone", ["one", "two", "three", "four", "five", "six", "seven", "eight"]),
        ("rgb-12-zone", ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"]),
    ];

    /// <summary>The documented minimum, should an older GG refuse a type of the full set.</summary>
    private static readonly (string Type, string[] Zones)[] SmallSet =
    [
        ("rgb-per-key-zones", ["all"]),
        ("mouse", ["wheel", "logo"]),
        ("headset", ["earcups"]),
        ("rgb-1-zone", ["one"]),
        ("rgb-2-zone", ["one", "two"]),
    ];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private GameSenseDevice? _device;

    public string Name => "SteelSeries GG";

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public async Task<SourceResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        Close();

        if (ReadAddress() is not { } address)
            return new SourceResult([], SourceHealth.Missing, "Not installed");

        var server = new Uri($"http://{address}/");

        try
        {
            await PostAsync(server, "game_metadata", new JsonObject
            {
                ["game"] = Game,
                ["game_display_name"] = "WinTo",
                ["developer"] = "WinTo",
                ["deinitialize_timer_length_ms"] = 60000,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SourceResult([], SourceHealth.Missing, "Not running");
        }

        var refused = await BindAsync(server, FullSet, cancellationToken).ConfigureAwait(false);
        if (refused is not null) refused = await BindAsync(server, SmallSet, cancellationToken).ConfigureAwait(false);
        if (refused is not null) return new SourceResult([], SourceHealth.Problem, $"GG refused: {refused}");

        _device = new GameSenseDevice(_http, server);
        return new SourceResult([_device], SourceHealth.Ready, "Connected");
    }

    /// <returns><c>null</c> when bound, otherwise GG's reason.</returns>
    private async Task<string?> BindAsync(Uri server, (string Type, string[] Zones)[] set, CancellationToken cancellationToken)
    {
        var handlers = new JsonArray();
        foreach (var (type, zones) in set)
        {
            foreach (var zone in zones)
            {
                handlers.Add(new JsonObject
                {
                    ["device-type"] = type,
                    ["zone"] = zone,
                    ["mode"] = "context-color",
                    ["context-frame-key"] = "zone-color",
                });
            }
        }

        try
        {
            await PostAsync(server, "bind_game_event", new JsonObject
            {
                ["game"] = Game,
                ["event"] = Event,
                ["min_value"] = 0,
                ["max_value"] = 100,
                ["icon_id"] = 0,
                ["value_optional"] = true,
                ["handlers"] = handlers,
            }, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task PostAsync(Uri server, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(server, path), body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(ErrorOf(text) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static string? ErrorOf(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadAddress()
    {
        try
        {
            if (!File.Exists(PropsFile)) return null;
            return JsonNode.Parse(File.ReadAllText(PropsFile))?["address"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Close()
    {
        _device?.Stop();
        _device = null;
    }

    public void Dispose()
    {
        Close();
        _http.Dispose();
    }

    /// <summary>All SteelSeries devices as one: the color goes out as one event, at most ten times a second.</summary>
    private sealed class GameSenseDevice : RgbDevice
    {
        private readonly HttpClient _http;
        private readonly Uri _server;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _pending = new(0, 1);
        private int _color;
        private int _value;
        private volatile bool _failed;

        public GameSenseDevice(HttpClient http, Uri server) : base("SteelSeries GG", "steelseries:gamesense", "SteelSeries devices", RgbKind.Other, 1)
        {
            _http = http;
            _server = server;
            _ = Task.Run(SendLoopAsync);
        }

        public override TimeSpan FrameInterval => TimeSpan.FromMilliseconds(100);

        public override bool Available => !_failed;

        public override string? Note => _failed ? "SteelSeries GG stopped answering" : "Every SteelSeries device takes the same color";

        public override void Write(RgbColor[] colors)
        {
            // Only the newest color matters; the loop sends it when it is free.
            Volatile.Write(ref _color, colors[0].R << 16 | colors[0].G << 8 | colors[0].B);
            if (_pending.CurrentCount == 0)
            {
                try
                {
                    _pending.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Already signaled.
                }
            }
        }

        private async Task SendLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    // A heartbeat at the latest every ten seconds keeps GG from taking the lights back.
                    _ = await _pending.WaitAsync(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
                    var packed = Volatile.Read(ref _color);
                    var color = new RgbColor((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);

                    // The value changes with every event: GG skips an event whose value didn't change.
                    _value = _value % 100 + 1;
                    var body = new JsonObject
                    {
                        ["game"] = Game,
                        ["event"] = Event,
                        ["data"] = new JsonObject
                        {
                            ["value"] = _value,
                            ["frame"] = new JsonObject
                            {
                                ["zone-color"] = new JsonObject { ["red"] = color.R, ["green"] = color.G, ["blue"] = color.B },
                            },
                        },
                    };

                    using var response = await _http.PostAsJsonAsync(new Uri(_server, "game_event"), body, _stop.Token)
                        .ConfigureAwait(false);
                    _failed = !response.IsSuccessStatusCode;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    _failed = true;
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Hands the lights back to GG at once instead of after its timeout.</summary>
        public void Stop()
        {
            _stop.Cancel();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_server, "stop_game"))
                {
                    Content = JsonContent.Create(new JsonObject { ["game"] = Game }),
                };
                _http.Send(request).Dispose();
            }
            catch (Exception)
            {
                // GG gone: nothing to hand back.
            }
        }
    }
}
