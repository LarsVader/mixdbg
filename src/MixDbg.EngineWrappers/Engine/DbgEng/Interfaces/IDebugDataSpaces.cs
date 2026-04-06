using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Minimal COM interface for reading and writing process memory via dbgeng.
/// Exposes <c>ReadVirtual</c> (slot 0) and <c>WriteVirtual</c> (slot 1).
/// </summary>
[ComImport, Guid("88f7dfab-3ea7-4c3a-aefb-c4e8106173aa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugDataSpaces
{
    // Slot 0: ReadVirtual
    [PreserveSig]
    int ReadVirtual(ulong Offset, IntPtr Buffer, uint BufferSize, out uint BytesRead);

    // Slot 1: WriteVirtual
    [PreserveSig]
    int WriteVirtual(ulong Offset, IntPtr Buffer, uint BufferSize, out uint BytesWritten);
}
