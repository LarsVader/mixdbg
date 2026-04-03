using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

// GUID: 5bd9d474-5975-423a-b88b-65a8e7110e65

[ComImport, Guid("5bd9d474-5975-423a-b88b-65a8e7110e65")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugBreakpoint
{
    // Slot 0
    [PreserveSig]
    int GetId(out uint Id);

    // Slot 1-2: GetType, GetAdder
    void _VtblGap1_2();

    // Slot 3
    [PreserveSig]
    int GetFlags(out uint Flags);

    // Slot 4
    [PreserveSig]
    int AddFlags(uint Flags);

    // Slot 5
    [PreserveSig]
    int RemoveFlags(uint Flags);

    // Slot 6: SetFlags
    void _VtblGap2_1();

    // Slot 7
    [PreserveSig]
    int GetOffset(out ulong Offset);

    // Slot 8
    [PreserveSig]
    int SetOffset(ulong Offset);

    // Slot 9
    [PreserveSig]
    int GetDataParameters(out uint Size, out uint AccessType);

    // Slot 10
    [PreserveSig]
    int SetDataParameters(uint Size, uint AccessType);
}
