using System.Diagnostics;
using System.Runtime.Loader;
using Avalonia.Threading;

namespace MultiTool.Host.Services;

/// <summary>Catches exceptions on the UI thread before they end the app.</summary>
/// <remarks>
/// <para>
/// <c>AppDomain.UnhandledException</c> can only log. Without this, one bad command or
/// <c>async void</c> handler in any module killed the whole app, including running recordings.
/// </para>
/// <para>
/// The stack tells which load context threw. A module's error goes to its log and the footer;
/// everything else keeps running.
/// </para>
/// <para>
/// Emergency brake: more than <see cref="BurstLimit"/> errors within <see cref="BurstWindow"/>
/// (e.g. a broken layout throwing every frame) end the app as before.
/// </para>
/// </remarks>
internal static class CrashGuard
{
    private const int BurstLimit = 15;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);

    private static readonly Queue<long> Recent = new();

    public static void Install(Action<string> notify) =>
        Dispatcher.UIThread.UnhandledException += (_, e) => Handle(e, notify);

    private static void Handle(DispatcherUnhandledExceptionEventArgs e, Action<string> notify)
    {
        var exception = e.Exception;
        Program.WriteCrashLog(exception, "UI");

        // Not recoverable.
        if (exception is OutOfMemoryException or InsufficientExecutionStackException) return;

        var now = Stopwatch.GetTimestamp();
        Recent.Enqueue(now);
        while (Recent.Count > 0 && Stopwatch.GetElapsedTime(Recent.Peek(), now) > BurstWindow) Recent.Dequeue();
        if (Recent.Count > BurstLimit) return;

        var moduleId = ModuleOf(exception);

        if (moduleId is not null)
        {
            new ModuleLog(moduleId).Error("Unhandled error on the UI thread.", exception);
            notify($"Error in {NameOf(moduleId)}: {FirstLine(exception.Message)}");
        }
        else
        {
            new ModuleLog("shell").Error("Unhandled error on the UI thread.", exception);
            notify($"Error: {FirstLine(exception.Message)}");
        }

        e.Handled = true;
    }

    /// <summary>Finds the first stack frame from a module load context, including inner exceptions.</summary>
    internal static string? ModuleOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            foreach (var frame in new StackTrace(current, fNeedFileInfo: false).GetFrames())
            {
                var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
                if (assembly is null) continue;

                if (AssemblyLoadContext.GetLoadContext(assembly) is ModuleLoadContext context)
                    return context.ModuleId;
            }

            if (current is AggregateException { InnerExceptions.Count: > 0 } aggregate)
                return aggregate.InnerExceptions.Select(ModuleOf).FirstOrDefault(id => id is not null);
        }

        return null;
    }

    private static string NameOf(string moduleId) =>
        App.Shell?.Catalog.Find(moduleId)?.Name ?? moduleId;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 160 ? line[..160] + " …" : line;
    }
}
