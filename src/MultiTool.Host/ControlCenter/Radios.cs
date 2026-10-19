namespace MultiTool.Host.ControlCenter;

public enum RadioKind
{
    Other = 0,
    WiFi = 1,
    MobileBroadband = 2,
    Bluetooth = 3,
    FM = 4,
}

public enum RadioState
{
    Unknown = 0,
    On = 1,
    Off = 2,

    /// <summary>Off and not changeable by software: a hardware switch or a policy.</summary>
    Disabled = 3,
}

/// <summary>A radio adapter as Windows shows it.</summary>
public sealed record RadioInfo(RadioKind Kind, string Name, RadioState State);

/// <summary>Turns Wi-Fi and Bluetooth on and off via <c>Windows.Devices.Radios</c>.</summary>
/// <remarks>
/// <para>
/// The documented API, no rights needed; the same switch as Windows quick settings.
/// <c>WlanSetInterface</c> only knows Wi-Fi and often needs admin rights.
/// </para>
/// <para>
/// <c>RequestAccessAsync</c> before every switch, as documented; some privacy settings make
/// <c>SetStateAsync</c> fail without it.
/// </para>
/// <para>
/// Slots from metadata: <c>IRadioStatics</c> GetRadiosAsync 6, GetDeviceSelector 7, FromIdAsync 8,
/// RequestAccessAsync 9; <c>IRadio</c> SetStateAsync 6, add_/remove_StateChanged 7/8, State 9,
/// Name 10, Kind 11.
/// </para>
/// </remarks>
internal static unsafe class Radios
{
    private static readonly Guid IidRadioStatics = new("5fb6a12e-67cb-46ae-aae9-65919f86eff4");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static Task<List<RadioInfo>> ListAsync() => WinRt.RunAsync(List);

    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static Task<string?> SetAsync(RadioKind kind, bool on) => WinRt.RunAsync(() => Set(kind, on));

    private static List<RadioInfo> List()
    {
        var result = new List<RadioInfo>();
        ForEachRadio(radio =>
        {
            result.Add(new RadioInfo(Kind(radio), WinRt.ReadString(radio, 10), State(radio)));
            return false;
        });

        return result;
    }

    private static string? Set(RadioKind kind, bool on)
    {
        var access = RequestAccess();
        if (access != 1)
        {
            return access is 2
                ? "Windows privacy settings deny switching."
                : "Windows does not allow switching.";
        }

        string? problem = $"No {(kind == RadioKind.WiFi ? "Wi-Fi" : "Bluetooth")} adapter found.";

        ForEachRadio(radio =>
        {
            if (Kind(radio) != kind) return false;

            if (State(radio) == RadioState.Disabled)
            {
                problem = "The adapter is switched off, e.g. by a hardware switch.";
                return true;
            }

            // Retry once: if another process switches the same radio at the same time, Windows reports
            // ERROR_BUSY (documented) and the second attempt succeeds.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    nint operation = 0;
                    try
                    {
                        WinRt.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)WinRt.Slot(radio, 6))(
                            radio, on ? (int)RadioState.On : (int)RadioState.Off, &operation));
                        WinRt.WaitFor(operation, Timeout);

                        int status;
                        WinRt.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)WinRt.Slot(operation, 8))(operation, &status));
                        problem = status == 1 ? null : "Windows rejected the switch.";
                    }
                    finally
                    {
                        WinRt.Release(operation);
                    }

                    break;
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x800700AA) && attempt == 0)
                {
                    Thread.Sleep(150);
                }
            }

            return true;
        });

        return problem;
    }

    private static int RequestAccess()
    {
        nint statics = 0, operation = 0;
        try
        {
            statics = WinRt.Factory("Windows.Devices.Radios.Radio", IidRadioStatics);
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(statics, 9))(statics, &operation));
            WinRt.WaitFor(operation, Timeout);

            int status;
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)WinRt.Slot(operation, 8))(operation, &status));
            return status;
        }
        finally
        {
            WinRt.Release(operation);
            WinRt.Release(statics);
        }
    }

    /// <summary>Calls <paramref name="visit"/> per adapter until it returns <c>true</c>.</summary>
    private static void ForEachRadio(Func<nint, bool> visit)
    {
        nint statics = 0, operation = 0, list = 0;
        try
        {
            statics = WinRt.Factory("Windows.Devices.Radios.Radio", IidRadioStatics);
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(statics, 6))(statics, &operation));
            WinRt.WaitFor(operation, Timeout);

            // IAsyncOperation<IVectorView<Radio>>.GetResults, slot 8.
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(operation, 8))(operation, &list));

            uint count;
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)WinRt.Slot(list, 7))(list, &count));

            for (uint i = 0; i < count; i++)
            {
                nint radio = 0;
                try
                {
                    WinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)WinRt.Slot(list, 6))(list, i, &radio));
                    if (visit(radio)) return;
                }
                finally
                {
                    WinRt.Release(radio);
                }
            }
        }
        finally
        {
            WinRt.Release(list);
            WinRt.Release(operation);
            WinRt.Release(statics);
        }
    }

    private static RadioState State(nint radio)
    {
        int state;
        WinRt.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)WinRt.Slot(radio, 9))(radio, &state));
        return (RadioState)state;
    }

    private static RadioKind Kind(nint radio)
    {
        int kind;
        WinRt.Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)WinRt.Slot(radio, 11))(radio, &kind));
        return (RadioKind)kind;
    }
}
