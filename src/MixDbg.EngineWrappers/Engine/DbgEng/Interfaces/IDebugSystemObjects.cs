using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

// GUID: 6b86fe2c-2c4f-4f0c-9da2-174311acc327

[ComImport, Guid("6b86fe2c-2c4f-4f0c-9da2-174311acc327")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugSystemObjects
{
    // Slot 0
    [PreserveSig]
    int GetEventThread(out uint Id);

    // Slot 1: GetEventProcess
    void _VtblGap1_1();

    // Slot 2
    [PreserveSig]
    int GetCurrentThreadId(out uint Id);

    // Slot 3
    [PreserveSig]
    int SetCurrentThreadId(uint Id);

    // Slots 4-5: GetCurrentProcessId, SetCurrentProcessId
    void _VtblGap2_2();

    // Slot 6
    [PreserveSig]
    int GetNumberThreads(out uint Number);

    // Slot 7: GetTotalNumberThreads
    void _VtblGap3_1();

    // Slot 8
    [PreserveSig]
    int GetThreadIdsByIndex(
        uint Start,
        uint Count,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[] Ids,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[]? SysIds);

    // Slots 9-13
    void _VtblGap4_5();

    // Slot 14
    [PreserveSig]
    int GetCurrentThreadSystemId(out uint SysId);

    // Slot 15
    [PreserveSig]
    int GetThreadIdBySystemId(uint SysId, out uint Id);

    // Slot 16
    [PreserveSig]
    int GetCurrentProcessSystemId(out uint SysId);
}