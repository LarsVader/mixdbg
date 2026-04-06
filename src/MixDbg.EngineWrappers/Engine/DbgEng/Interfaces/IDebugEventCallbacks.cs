using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng.Interfaces;

// GUID: 337be28b-5036-4d72-b6bf-c45fbb9f2eaa
// We implement this interface to receive debug events.

[ComImport, Guid("337be28b-5036-4d72-b6bf-c45fbb9f2eaa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugEventCallbacks
{
    [PreserveSig]
    int GetInterestMask(out uint Mask);

    [PreserveSig]
    int Breakpoint(
        [MarshalAs(UnmanagedType.Interface)] IDebugBreakpoint Bp);

    [PreserveSig]
    int Exception(
        IntPtr Exception,
        uint FirstChance);

    [PreserveSig]
    int CreateThread(
        ulong Handle,
        ulong DataOffset,
        ulong StartOffset);

    [PreserveSig]
    int ExitThread(uint ExitCode);

    [PreserveSig]
    int CreateProcess(
        ulong ImageFileHandle,
        ulong Handle,
        ulong BaseOffset,
        uint ModuleSize,
        [MarshalAs(UnmanagedType.LPStr)] string? ModuleName,
        [MarshalAs(UnmanagedType.LPStr)] string? ImageName,
        uint CheckSum,
        uint TimeDateStamp,
        ulong InitialThreadHandle,
        ulong ThreadDataOffset,
        ulong StartOffset);

    [PreserveSig]
    int ExitProcess(uint ExitCode);

    [PreserveSig]
    int LoadModule(
        ulong ImageFileHandle,
        ulong BaseOffset,
        uint ModuleSize,
        [MarshalAs(UnmanagedType.LPStr)] string? ModuleName,
        [MarshalAs(UnmanagedType.LPStr)] string? ImageName,
        uint CheckSum,
        uint TimeDateStamp);

    [PreserveSig]
    int UnloadModule(
        [MarshalAs(UnmanagedType.LPStr)] string? ImageBaseName,
        ulong BaseOffset);

    [PreserveSig]
    int SystemError(uint Error, uint Level);

    [PreserveSig]
    int SessionStatus(uint Status);

    [PreserveSig]
    int ChangeDebuggeeState(uint Flags, ulong Argument);

    [PreserveSig]
    int ChangeEngineState(uint Flags, ulong Argument);

    [PreserveSig]
    int ChangeSymbolState(uint Flags, ulong Argument);
}