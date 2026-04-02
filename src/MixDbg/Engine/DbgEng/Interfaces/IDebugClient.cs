using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

// GUID: 27fe5639-8407-4f47-8364-ee118fb08ac8

[ComImport, Guid("27fe5639-8407-4f47-8364-ee118fb08ac8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugClient
{
    // Slots 0-8: AttachKernel through GetRunningProcessDescription
    void _VtblGap1_9();

    // Slot 9
    [PreserveSig]
    int AttachProcess(
        ulong Server,
        uint ProcessId,
        uint AttachFlags);

    // Slot 10
    [PreserveSig]
    int CreateProcess(
        ulong Server,
        [MarshalAs(UnmanagedType.LPStr)] string CommandLine,
        uint CreateFlags);

    // Slot 11: CreateProcessAndAttach
    void _VtblGap2_1();

    // Slots 12-20
    void _VtblGap3_9();

    // Slot 21
    [PreserveSig]
    int TerminateProcesses();

    // Slot 22
    [PreserveSig]
    int DetachProcesses();

    // Slot 23
    [PreserveSig]
    int EndSession(uint Flags);

    // Slot 24
    [PreserveSig]
    int GetExitCode(out uint Code);

    // Slots 25-42
    void _VtblGap4_18();

    // Slot 43
    [PreserveSig]
    int SetEventCallbacks(
        [MarshalAs(UnmanagedType.Interface)] IDebugEventCallbacks Callbacks);

    // Slot 44: FlushCallbacks (not needed)
}
