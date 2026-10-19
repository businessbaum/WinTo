using System.Runtime.InteropServices;

namespace MultiTool.Host.ControlCenter;

/// <summary>The little WinRT the control center needs, by hand over COM.</summary>
/// <remarks>
/// <para>
/// The Windows SDK projection would weigh about 25 MB for two interfaces. Every WinRT interface
/// starts with the six <c>IUnknown</c>/<c>IInspectable</c> methods; its own follow from slot 6 in
/// metadata order. IIDs and order are read from the Windows metadata.
/// </para>
/// <para>
/// Each call runs on its own thread with <c>RoInitialize(MTA)</c>: waiting on the UI thread would
/// freeze it, and a pool thread may already have another apartment.
/// </para>
/// </remarks>
internal static unsafe partial class WinRt
{
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    /// <summary>Runs <paramref name="work"/> on a fresh MTA thread.</summary>
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
            Name = "WinTo WinRT",
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
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    public static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    public static nint Factory(string className, Guid iid)
    {
        nint name;
        fixed (char* chars = className)
            Check(WindowsCreateString(chars, (uint)className.Length, &name));

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

    /// <summary>Waits for an <c>IAsyncOperation&lt;T&gt;</c> of an object, returns <c>GetResults</c> (slot 8), releases the operation.</summary>
    public static nint Results(nint operation, TimeSpan timeout)
    {
        try
        {
            WaitFor(operation, timeout);
            nint result;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, 8))(operation, &result));
            return result;
        }
        finally
        {
            Release(operation);
        }
    }

    /// <summary>A Boolean property: one byte in the WinRT ABI.</summary>
    public static bool ReadBool(nint instance, int slot)
    {
        byte value;
        return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(instance, slot))(instance, &value) >= 0 && value != 0;
    }

    /// <summary>An Int32 or enum property.</summary>
    public static int ReadInt32(nint instance, int slot)
    {
        int value;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(instance, slot))(instance, &value));
        return value;
    }

    /// <summary>A TimeSpan or DateTime property: one Int64 in 100-ns units.</summary>
    public static long ReadInt64(nint instance, int slot)
    {
        long value;
        Check(((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(instance, slot))(instance, &value));
        return value;
    }

    /// <summary>An object property; 0 for null.</summary>
    public static nint ReadObject(nint instance, int slot)
    {
        nint value;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &value));
        return value;
    }

    /// <summary>Waits for an operation via <c>IAsyncInfo</c>, without building a callback.</summary>
    /// <remarks>With a deadline: a hanging radio API must not take the control center with it.</remarks>
    public static void WaitFor(nint operation, TimeSpan timeout)
    {
        var info = Query(operation, IidAsyncInfo);
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

                Thread.Sleep(10);
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
