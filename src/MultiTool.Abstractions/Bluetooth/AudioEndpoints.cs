using System.Runtime.InteropServices;

namespace MultiTool.Abstractions.Bluetooth;

/// <summary>A playback endpoint as the MMDevice API sees it.</summary>
/// <param name="Id">The endpoint id for <see cref="AudioEndpoints.Request"/>.</param>
/// <param name="Name">Display name, e.g. "Headphones (AirPods Pro)".</param>
/// <param name="ContainerId">Groups the endpoints of one physical device.</param>
/// <param name="State"><c>DEVICE_STATE_…</c>: 1 active, 8 unplugged.</param>
public sealed record EndpointInfo(string Id, string Name, Guid ContainerId, int State);

/// <summary>Enumerates playback endpoints and connects or disconnects Bluetooth audio.</summary>
/// <remarks>
/// <para>
/// Connecting uses <c>KSPROPSETID_BtAudio</c> with <c>KSPROPERTY_ONESHOT_RECONNECT</c> or
/// <c>…_DISCONNECT</c>, the documented way the Windows Bluetooth audio driver connects. A non-Bluetooth
/// endpoint answers with an error and stays untouched.
/// </para>
/// <para>Calls block until the driver is done (seconds when connecting); never on the UI thread.</para>
/// <para>Shared by the Bluetooth audio module and the control center. Since 0.11.0.</para>
/// </remarks>
public static class AudioEndpoints
{
    public const int DeviceStateActive = 0x1;
    private const int DeviceStateMaskAll = 0xF;
    private const int DataFlowRender = 0;

    private static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint OneShotReconnect = 0;
    private const uint OneShotDisconnect = 1;
    private const uint KsPropertyTypeGet = 1;

    private static readonly PropertyKey KeyFriendlyName =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    private static readonly PropertyKey KeyContainerId =
        new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    /// <summary>Creates the device enumerator via <c>CoCreateInstance</c>, not <c>new</c> on a <c>[ComImport]</c> class.</summary>
    /// <remarks>
    /// From a module load context, <c>new</c> returns a bare <c>__ComObject</c> and the cast to the wrapper
    /// class fails ("Unable to cast … System.__ComObject"); a test project in the default context doesn't
    /// show it. Casting to the <b>interface</b> goes through QueryInterface and works in any context.
    /// </remarks>
    internal static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = typeof(IMMDeviceEnumerator).GUID;

        Marshal.ThrowExceptionForHR(CoCreateInstance(
            ref clsid, nint.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out var instance));

        return (IMMDeviceEnumerator)instance;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint clsCtx,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);

    /// <summary>All playback endpoints, including unplugged ones.</summary>
    public static List<EndpointInfo> Snapshot()
    {
        var result = new List<EndpointInfo>();

        var enumerator = CreateEnumerator();
        try
        {
            Check(enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateMaskAll, out var collection));
            try
            {
                Check(collection.GetCount(out var count));

                for (var i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0) continue;
                    try
                    {
                        if (device.GetId(out var id) != 0) continue;
                        if (device.GetState(out var state) != 0) continue;

                        var name = "";
                        var container = Guid.Empty;

                        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) == 0)
                        {
                            try
                            {
                                name = ReadString(store, KeyFriendlyName) ?? "";
                                container = ReadGuid(store, KeyContainerId) ?? Guid.Empty;
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(store);
                            }
                        }

                        result.Add(new EndpointInfo(id, name, container, state));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return result;
    }

    /// <summary>
    /// Asks the Bluetooth driver to connect or disconnect this endpoint. <c>false</c> if the way to the
    /// device node fails or the driver rejects the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>KSPROPSETID_BtAudio</c> is answered by <b>the device node</b> (BthHfEnum / BthEnum), not the audio
    /// endpoint; the endpoint's own KS filter rejects <c>ONESHOT_RECONNECT</c>.
    /// </para>
    /// <para>
    /// The way is the device topology: from the endpoint through its connector to the connected
    /// connector on the device side. This also works while the endpoint is unplugged (<c>State 8</c>):
    /// the device node stays.
    /// </para>
    /// </remarks>
    public static bool Request(string endpointId, bool connect)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpointId, out var device) != 0) return false;
            try
            {
                var control = ResolveDeviceKsControl(enumerator, device);
                if (control is null) return false;

                try
                {
                    var property = new KsIdentifier
                    {
                        Set = KsPropSetBtAudio,
                        Id = connect ? OneShotReconnect : OneShotDisconnect,
                        Flags = KsPropertyTypeGet,
                    };

                    return control.KsProperty(ref property, (uint)Marshal.SizeOf<KsIdentifier>(),
                        nint.Zero, 0, out _) == 0;
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Walks from the playback endpoint through the topology to the Bluetooth device node and gets its
    /// <see cref="IKsControl"/>. <c>null</c> if the way breaks.
    /// </summary>
    /// <remarks>
    /// <c>IKsControl</c> is not on the device side's <see cref="IPart"/> (<c>IPart.Activate</c> returns
    /// <c>E_NOINTERFACE</c>) but on the device node's <b>IMMDevice</b>: get the device id from the other
    /// side and open the device through the enumerator.
    /// </remarks>
    private static IKsControl? ResolveDeviceKsControl(IMMDeviceEnumerator enumerator, IMMDevice endpoint)
    {
        var iidTopology = typeof(IDeviceTopology).GUID;
        if (endpoint.Activate(ref iidTopology, 23 /* CLSCTX_ALL */, nint.Zero, out var topoRaw) != 0)
            return null;

        var topology = (IDeviceTopology)topoRaw;
        try
        {
            // A playback endpoint has exactly one connector to the outside.
            if (topology.GetConnector(0, out var connector) != 0) return null;
            try
            {
                if (connector.GetConnectedTo(out var farConnector) != 0) return null;
                try
                {
                    var part = (IPart)farConnector;
                    if (part.GetTopologyObject(out var farTopoRaw) != 0) return null;

                    var farTopology = (IDeviceTopology)farTopoRaw;
                    try
                    {
                        if (farTopology.GetDeviceId(out var deviceId) != 0 || deviceId is null)
                            return null;

                        if (enumerator.GetDevice(deviceId, out var farDevice) != 0) return null;
                        try
                        {
                            var iidKs = typeof(IKsControl).GUID;
                            if (farDevice.Activate(ref iidKs, 23 /* CLSCTX_ALL */, nint.Zero, out var ksRaw) != 0)
                                return null;

                            return (IKsControl)ksRaw;
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(farDevice);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(farTopology);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(farConnector);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(connector);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(topology);
        }
    }

    private static void Check(int hr)
    {
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            // VT_LPWSTR = 31
            return value.Vt == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static Guid? ReadGuid(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            // VT_CLSID = 72; the pointer points to the GUID.
            return value.Vt == 72 && value.Pointer != nint.Zero
                ? Marshal.PtrToStructure<Guid>(value.Pointer)
                : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    // ------------------------------------------------------------------ COM interop

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>Just enough PROPVARIANT to read a string or a GUID; the value sits at offset 8 on x64.</summary>
    /// <remarks>
    /// <c>Size = 24</c> is required: the real PROPVARIANT is 24 bytes on x64 and <c>GetValue</c> writes the
    /// whole buffer. A shorter struct would be overwritten past its end.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public nint Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KsIdentifier
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsIdentifier property, uint propertyLength,
            nint data, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsMethod(ref KsIdentifier method, uint methodLength,
            nint data, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsEvent(ref KsIdentifier ksEvent, uint eventLength,
            nint data, uint dataLength, out uint bytesReturned);
    }

    // The three topology interfaces from devicetopology.h. Only one method each is used; the
    // vtable slots before it are parameterless placeholders, never called. Only the count of
    // methods matters for vtable order.

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount_();
        [PreserveSig] int GetSubunit_();
        [PreserveSig] int GetPartById_();
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
        // GetSignalPath, unused.
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IConnector
    {
        [PreserveSig] int GetType_();
        [PreserveSig] int GetDataFlow_();
        [PreserveSig] int ConnectTo_();
        [PreserveSig] int Disconnect_();
        [PreserveSig] int IsConnected_();
        [PreserveSig] int GetConnectedTo(out IConnector connectedTo);
        // GetConnectorIdConnectedTo, GetDeviceIdConnectedTo, unused.
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPart
    {
        [PreserveSig] int GetName_();
        [PreserveSig] int GetLocalId_();
        [PreserveSig] int GetGlobalId_();
        [PreserveSig] int GetPartType_();
        [PreserveSig] int GetSubType_();
        [PreserveSig] int GetControlInterfaceCount_();
        [PreserveSig] int GetControlInterface_();
        [PreserveSig] int EnumPartsIncoming_();
        [PreserveSig] int EnumPartsOutgoing_();
        [PreserveSig] int GetTopologyObject(out IDeviceTopology topology);
        // Activate, RegisterControlChangeCallback, UnregisterControlChangeCallback, unused.
    }
}

/// <summary>Reports changes to the set or state of endpoints, e.g. headphones switching connection.</summary>
/// <remarks>
/// The callback comes from a COM thread; receivers switch to the UI thread themselves. The
/// registration holds the enumerator until <see cref="Dispose"/> releases it.
/// </remarks>
public sealed class EndpointWatcher : IDisposable
{
    private readonly AudioEndpoints.IMMDeviceEnumerator _enumerator;
    private readonly Client _client;
    private bool _disposed;

    public event Action? Changed;

    public EndpointWatcher()
    {
        _enumerator = AudioEndpoints.CreateEnumerator();
        _client = new Client(this);
        Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_client));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _enumerator.UnregisterEndpointNotificationCallback(_client); }
        catch (Exception) { /* The service may already be gone during shutdown. */ }
        Marshal.ReleaseComObject(_enumerator);
    }

    private sealed class Client(EndpointWatcher owner) : AudioEndpoints.IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, int newState) => owner.Changed?.Invoke();
        public void OnDeviceAdded(string deviceId) => owner.Changed?.Invoke();
        public void OnDeviceRemoved(string deviceId) => owner.Changed?.Invoke();
        public void OnDefaultDeviceChanged(int flow, int role, string? deviceId) { }
        public void OnPropertyValueChanged(string deviceId, AudioEndpoints.PropertyKey key) { }
    }
}
