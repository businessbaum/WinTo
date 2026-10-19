using System.Collections.Concurrent;

namespace MultiTool.Modules.Soundboard.Services;

/// <summary>
/// A dedicated thread that processes tasks strictly in order.
///
/// Purpose: key presses must be processed neither in the keyboard hook nor on the UI thread. Windows
/// forcibly unhooks after about 300 ms, and the UI thread must not block. Order matters too: the same key
/// pressed twice quickly means on, then off.
/// </summary>
public sealed class SerialWorkQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;

    public SerialWorkQueue(string name)
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Post(Action work)
    {
        try { _queue.Add(work); }
        catch (Exception) { /* already shut down */ }
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception) { /* a broken task must not kill the queue */ }
        }
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch (Exception) { }
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
