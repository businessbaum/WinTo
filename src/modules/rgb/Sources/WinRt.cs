using System.Runtime.InteropServices;

namespace MultiTool.Modules.Rgb.Sources;

/// <summary>The little WinRT Dynamic Lighting needs, by hand over COM.</summary>
/// <remarks>
/// <para>
/// The Windows SDK projection would weigh about 25 MB for a handful of interfaces. Every WinRT interface starts
/// with the six <c>IUnknown</c>/<c>IInspectable</c> methods; its own follow from slot 6 in metadata order. IIDs
/// and order were read from <c>System32\WinMetadata\Windows.Devices.winmd</c> (see the slot comments). The host's
/// control center has the same helper; a module can't reach into the host.
/// </para>
/// <para>
/// Calls run on MTA threads: <see cref="RunAsync{T}"/> for discovery, the engine's thread for writes.
/// </para>
/// </remarks>
internal static unsafe partial class WinRt
{
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    /// <summary>Runs <paramref name="work"/> on a fresh MTA thread; waiting there freezes nothing.</summary>
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var init = RoInitialize(1);
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            finally
            {
                if (init >= 0) RoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "WinTo RGB WinRT",
        };

        thread.Start();
        return done.Task;
    }

    public static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static nint Query(nint instance, Guid iid)
    {
        nint result;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result);
        return hr < 0 ? 0 : result;
    }

    public static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    public static nint Factory(string className, Guid iid)
    {
        var name = CreateString(className);
        try
        {
            nint factory;
            Check(RoGetActivationFactory(name, &iid, &factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    public static nint CreateString(string value)
    {
        nint handle;
        fixed (char* chars = value)
            Check(WindowsCreateString(chars, (uint)value.Length, &handle));
        return handle;
    }

    /// <summary>Reads an HSTRING property at <paramref name="slot"/>.</summary>
    public static string ReadString(nint instance, int slot)
    {
        nint value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &value));

        try
        {
            uint length;
            var chars = WindowsGetStringRawBuffer(value, &length);
            return chars == null ? "" : new string(chars, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(value);
        }
    }

    /// <summary>Calls a method taking one string and returning an <c>IAsyncOperation</c>, waits, returns the result.</summary>
    public static nint AwaitWithString(nint instance, int slot, string argument, TimeSpan timeout)
    {
        var text = CreateString(argument);
        nint operation = 0;

        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(instance, slot))(instance, text, &operation));
            WaitFor(operation, timeout);

            // IAsyncOperation<T>.GetResults, slot 8.
            nint result;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, 8))(operation, &result));
            return result;
        }
        finally
        {
            Release(operation);
            WindowsDeleteString(text);
        }
    }

    /// <summary>Waits for an operation via <c>IAsyncInfo</c>, without building a callback.</summary>
    public static void WaitFor(nint operation, TimeSpan timeout)
    {
        var info = Query(operation, IidAsyncInfo);
        if (info == 0) throw new InvalidOperationException("No IAsyncInfo.");
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        try
        {
            while (true)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));

                if (status == 1) return;
                if (status == 2) throw new OperationCanceledException();
                if (status == 3)
                {
                    int error;
                    ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 8))(info, &error);
                    Marshal.ThrowExceptionForHR(error);
                }

                if (Environment.TickCount64 > deadline)
                {
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, 9))(info);
                    throw new TimeoutException("Windows did not answer in time.");
                }

                Thread.Sleep(5);
            }
        }
        finally
        {
            Release(info);
        }
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int type);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* source, uint length, nint* result);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    private static partial char* WindowsGetStringRawBuffer(nint value, uint* length);
}
