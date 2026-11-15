using System.Runtime.InteropServices;

namespace BebirdScope;

/// <summary>Kernel-streaming control interface exposed by the camera's capture filter.</summary>
[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IKsControl
{
    // Only the first vtable slot is used. KsMethod / KsEvent are never called.
    [PreserveSig]
    int KsProperty(IntPtr property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned);
}

/// <summary>
/// Reads the K10's onboard accelerometer. The sensor sits on an I2C bus that the camera chip
/// (Etron eSP570) bridges through UVC Extension Unit selector 2:
///   SET  [0x84, i2cAddr(0x30), reg, 0, 0, 0]
///   GET  -> [0x84, LSB, MSB, 0, status1, status2]
/// All calls must come from ONE thread; the SET/GET pair is not re-entrant on the device.
/// </summary>
internal sealed class K10Sensor : IDisposable
{
    static readonly Guid XuGuid = new Guid("c2b1ccad-abf6-48b8-8e37-32d4f3a3feec");

    const uint KSPROPERTY_TYPE_GET = 0x00000001;
    const uint KSPROPERTY_TYPE_SET = 0x00000002;
    const uint KSPROPERTY_TYPE_TOPOLOGY = 0x10000000;
    const uint XuNodeId = 3;      // topology node of the extension unit (descriptor unit ID 4)
    const uint XuSelector = 2;    // I2C bridge
    const byte I2cWriteAddr = 0x30;
    const byte CmdRead16 = 0x84;
    const int KsNodePropSize = 32; // KSPROPERTY (24) + NodeId + Reserved

    readonly IKsControl _ks;
    IntPtr _pSet, _pGet, _pData;

    public int LastHr { get; private set; }

    public K10Sensor(IKsControl ks)
    {
        _ks = ks;
        _pSet = MakeNodeProperty(KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_TOPOLOGY);
        _pGet = MakeNodeProperty(KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_TOPOLOGY);
        _pData = Marshal.AllocHGlobal(6);
    }

    static IntPtr MakeNodeProperty(uint flags)
    {
        IntPtr p = Marshal.AllocHGlobal(KsNodePropSize);
        Marshal.Copy(XuGuid.ToByteArray(), 0, p, 16);
        Marshal.WriteInt32(p, 16, (int)XuSelector);
        Marshal.WriteInt32(p, 20, (int)flags);
        Marshal.WriteInt32(p, 24, (int)XuNodeId);
        Marshal.WriteInt32(p, 28, 0);
        return p;
    }

    /// <summary>Atomic 16-bit little-endian read of (reg, reg+1).</summary>
    public bool TryRead16(byte reg, out short value)
    {
        value = 0;
        if (_pData == IntPtr.Zero) return false;

        Marshal.WriteByte(_pData, 0, CmdRead16);
        Marshal.WriteByte(_pData, 1, I2cWriteAddr);
        Marshal.WriteByte(_pData, 2, reg);
        for (int i = 3; i < 6; i++) Marshal.WriteByte(_pData, i, 0);

        int hr = _ks.KsProperty(_pSet, KsNodePropSize, _pData, 6, out uint returned);
        if (hr != 0) { LastHr = hr; return false; }

        for (int i = 0; i < 6; i++) Marshal.WriteByte(_pData, i, 0);
        hr = _ks.KsProperty(_pGet, KsNodePropSize, _pData, 6, out returned);
        if (hr != 0 || returned < 6) { LastHr = hr != 0 ? hr : unchecked((int)0x80004005); return false; }

        byte echo = Marshal.ReadByte(_pData, 0);
        byte lsb = Marshal.ReadByte(_pData, 1);
        byte msb = Marshal.ReadByte(_pData, 2);
        byte st1 = Marshal.ReadByte(_pData, 4);
        byte st2 = Marshal.ReadByte(_pData, 5);
        if (echo != CmdRead16 || st1 != 0 || st2 != 0) { LastHr = unchecked((int)0x80004005); return false; }

        value = (short)((msb << 8) | lsb);
        return true;
    }

    public void Dispose()
    {
        if (_pSet != IntPtr.Zero) { Marshal.FreeHGlobal(_pSet); _pSet = IntPtr.Zero; }
        if (_pGet != IntPtr.Zero) { Marshal.FreeHGlobal(_pGet); _pGet = IntPtr.Zero; }
        if (_pData != IntPtr.Zero) { Marshal.FreeHGlobal(_pData); _pData = IntPtr.Zero; }
    }
}
