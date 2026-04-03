using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Minimal COM interface for reading process memory via dbgeng.
/// Only exposes <c>ReadVirtual</c> (slot 3 after IUnknown).
/// </summary>
[ComImport, Guid("88f7dfab-3ea7-4c3a-aefb-c4e8106173aa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugDataSpaces
{
    // Slot 3: ReadVirtual
    [PreserveSig]
    int ReadVirtual(ulong Offset, IntPtr Buffer, uint BufferSize, out uint BytesRead);
}
