using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MultiTool.Modules.Rgb.Sources;

/// <summary>Razer devices through Razer Synapse's Chroma SDK server (REST on <c>localhost:54235</c>).</summary>
/// <remarks>
/// <para>
/// An app opens a session (Synapse answers with the session's own address), keeps it alive with a heartbeat
/// at least every 15 seconds, and sets a static color per device class: keyboard, mouse, mousepad, headset,
/// keypad and Chroma Link. Deleting the session hands the lights back.
/// </para>
/// <para>
/// Synapse doesn't list devices through this interface; every Razer device of those classes takes the color,
/// shown as one entry. Newer Razer devices also speak Windows Dynamic Lighting and appear there by name.
/// </para>
/// </remarks>
internal sealed class RazerSource : IRgbSource
{
    private static readonly Uri Server = new("http://localhost:54235/razer/chromasdk");

    private static readonly string[] Classes = ["keyboard", "mouse", "mousepad", "headset", "keypad", "chromalink"];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private ChromaDevice? _device;

    public string Name => "Razer Chroma";

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public async Task<SourceResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        Close();

        Uri session;
        try
        {
            using var response = await _http.PostAsJsonAsync(Server, new JsonObject
            {
                ["title"] = "WinTo",
                ["description"] = "RGB lighting from WinTo",
                ["author"] = new JsonObject { ["name"] = "WinTo", ["contact"] = "https://github.com" },
                ["device_supported"] = new JsonArray([.. Classes.Select(c => (JsonNode)c)]),
                ["category"] = "application",
            }, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new SourceResult([], SourceHealth.Problem, $"Synapse answered {(int)response.StatusCode}");

            var answer = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
            if (answer?["uri"]?.GetValue<string>() is not { } uri)
                return new SourceResult([], SourceHealth.Problem, "Synapse gave no session");

            session = new Uri(uri.TrimEnd('/') + "/");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SourceResult([], SourceHealth.Missing, "Synapse's Chroma SDK not running");
        }

        _device = new ChromaDevice(_http, session);
        return new SourceResult([_device], SourceHealth.Ready, "Connected");
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

    /// <summary>All Razer device classes as one; at most five colors a second, every class a request.</summary>
    private sealed class ChromaDevice : RgbDevice
    {
        private readonly HttpClient _http;
        private readonly Uri _session;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _pending = new(0, 1);
        private int _color;
        private volatile bool _failed;

        public ChromaDevice(HttpClient http, Uri session) : base("Razer Chroma", "razer:chroma", "Razer devices", RgbKind.Other, 1)
        {
            _http = http;
            _session = session;
            _ = Task.Run(SendLoopAsync);
        }

        public override TimeSpan FrameInterval => TimeSpan.FromMilliseconds(200);

        public override bool Available => !_failed;

        public override string? Note => _failed ? "Synapse stopped answering" : "Every Razer device takes the same color";

        public override void Write(RgbColor[] colors)
        {
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
                    var changed = await _pending.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
                    if (!changed)
                    {
                        using var beat = await _http.PutAsync(new Uri(_session, "heartbeat"), null, _stop.Token).ConfigureAwait(false);
                        _failed = !beat.IsSuccessStatusCode;
                        continue;
                    }

                    // Chroma's colors are BGR in one integer.
                    var packed = Volatile.Read(ref _color);
                    var color = new RgbColor((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
                    var bgr = color.B << 16 | color.G << 8 | color.R;
                    var ok = true;
                    foreach (var kind in Classes)
                    {
                        using var response = await _http.PutAsJsonAsync(new Uri(_session, kind), new JsonObject
                        {
                            ["effect"] = "CHROMA_STATIC",
                            ["param"] = new JsonObject { ["color"] = bgr },
                        }, _stop.Token).ConfigureAwait(false);
                        ok &= response.IsSuccessStatusCode;
                    }

                    _failed = !ok;
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

        /// <summary>Ends the session; Synapse takes the lights back.</summary>
        public void Stop()
        {
            _stop.Cancel();
            try
            {
                _http.Send(new HttpRequestMessage(HttpMethod.Delete, _session)).Dispose();
            }
            catch (Exception)
            {
                // Synapse gone: nothing to end.
            }
        }
    }
}
