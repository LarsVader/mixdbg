using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Minimal COM interface for <c>IDebugAdvanced</c>. Exposes thread context
/// read/write needed by the <c>ICorDebugDataTarget</c> bridge.
/// GUID: f2df5f53-071f-47bd-9de6-5734c3fed689
/// </summary>
[ComImport, Guid("f2df5f53-071f-47bd-9de6-5734c3fed689")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugAdvanced
{
    // Slot 0: GetThreadContext
    [PreserveSig]
    int GetThreadContext(IntPtr Context, uint ContextSize);

    // Slot 1: SetThreadContext
    [PreserveSig]
    int SetThreadContext(IntPtr Context, uint ContextSize);
}
