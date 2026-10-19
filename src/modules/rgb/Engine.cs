using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Rgb;

/// <summary>Sends every controlled device its colors: frames for moving effects, a color now and then for still ones.</summary>
/// <remarks>
/// <para>
/// <b>One thread, in the multithreaded apartment</b> (<c>RoInitialize</c>): Dynamic Lighting's objects live
/// there, and a write from the UI thread (single-threaded) would go through a proxy or fail.
/// </para>
/// <para>
/// <b>A still color is sent again every two seconds.</b> Dynamic Lighting ignores writes while another app has
/// the lights and takes them as soon as WinTo is allowed again; a device that was unplugged and plugged back
/// starts dark. Resending costs a few bytes and covers both.
/// </para>
/// <para>
/// A device that fails three times in a row is left alone and reported; the others keep running.
/// </para>
/// </remarks>
internal sealed partial class RgbEngine : IDisposable
{
    private static readonly TimeSpan StillRefresh = TimeSpan.FromSeconds(2);

    private readonly IModuleLog _log;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Target[] _targets = [];
    private volatile bool _stopping;

    public RgbEngine(IModuleLog log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "WinTo RGB", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>A device stopped answering. Raised on the engine's thread.</summary>
    public event Action<RgbDevice, string>? Failed;

    /// <summary>Replaces what is controlled. Devices already running keep their timing; changes show at once.</summary>
    public void Set(IEnumerable<(RgbDevice Device, Lighting Lighting)> targets)
    {
        var previous = Volatile.Read(ref _targets).ToDictionary(t => t.Device);
        var next = targets.Select(t =>
        {
            var target = previous.GetValueOrDefault(t.Device) ?? new Target(t.Device);
            target.Lighting = t.Lighting.Clone();
            target.NextDue = TimeSpan.Zero;
            target.LastStill = null;
            return target;
        }).ToArray();

        Volatile.Write(ref _targets, next);
        _wake.Set();
    }

    private void Run()
    {
        var apartment = RoInitialize(1);

        try
        {
            while (!_stopping)
            {
                var now = _clock.Elapsed;
                var targets = Volatile.Read(ref _targets);
                var wait = StillRefresh;

                foreach (var target in targets)
                {
                    if (target.Failed) continue;

                    if (now >= target.NextDue) Tick(target, now);
                    var until = target.NextDue - _clock.Elapsed;
                    if (until < wait) wait = until;
                }

                var milliseconds = (int)Math.Clamp(wait.TotalMilliseconds, 4, StillRefresh.TotalMilliseconds);
                _wake.WaitOne(milliseconds);
            }
        }
        finally
        {
            if (apartment >= 0) RoUninitialize();
        }
    }

    private void Tick(Target target, TimeSpan now)
    {
        var lighting = target.Lighting;
        var device = target.Device;

        // A still color: sent when it changed, otherwise only as the periodic refresh.
        var available = device.Available;
        if (!lighting.Moves)
        {
            var signature = $"{lighting.Effect}|{lighting.Color}|{lighting.Brightness}";
            var regained = available && !target.WasAvailable;
            target.WasAvailable = available;

            if (signature == target.LastStill && !regained && now - target.LastSent < StillRefresh)
            {
                target.NextDue = target.LastSent + StillRefresh;
                return;
            }

            target.LastStill = signature;
        }
        else
        {
            target.WasAvailable = available;
        }

        Effects.Render(lighting, device.Positions, now.TotalSeconds, target.Buffer);

        try
        {
            device.Write(target.Buffer);
            target.Errors = 0;
        }
        catch (Exception ex)
        {
            if (++target.Errors >= 3)
            {
                target.Failed = true;
                _log.Warn($"{device.Source} · {device.Name}: {ex.Message}");
                Failed?.Invoke(device, ex.Message);
            }
        }

        target.LastSent = now;
        target.NextDue = now + (lighting.Moves ? device.FrameInterval : StillRefresh);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }

    private sealed class Target(RgbDevice device)
    {
        public RgbDevice Device { get; } = device;
        public RgbColor[] Buffer { get; } = new RgbColor[device.LedCount];
        public Lighting Lighting { get; set; } = new();
        public TimeSpan NextDue { get; set; }
        public TimeSpan LastSent { get; set; } = TimeSpan.MinValue / 2;
        public string? LastStill { get; set; }
        public bool WasAvailable { get; set; }
        public int Errors { get; set; }
        public bool Failed { get; set; }
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int type);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();
}
