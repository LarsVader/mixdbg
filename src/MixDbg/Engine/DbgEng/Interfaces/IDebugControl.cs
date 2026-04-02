using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

// GUID: 5182e668-105e-416e-ad92-24ef800424ba

[ComImport, Guid("5182e668-105e-416e-ad92-24ef800424ba")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugControl
{
    // Slot 0: GetInterrupt
    void _VtblGap1_1();

    // Slot 1
    [PreserveSig]
    int SetInterrupt(uint Flags);

    // Slots 2-27
    void _VtblGap2_26();

    // Slot 28
    [PreserveSig]
    int GetStackTrace(
        ulong FrameOffset,
        ulong StackOffset,
        ulong InstructionOffset,
        IntPtr Frames,
        uint FramesSize,
        out uint FramesFilled);

    // Slots 29-45
    void _VtblGap3_17();

    // Slot 46
    [PreserveSig]
    int GetExecutionStatus(out uint Status);

    // Slot 47
    [PreserveSig]
    int SetExecutionStatus(uint Status);

    // Slots 48-62
    void _VtblGap4_15();

    // Slot 63
    [PreserveSig]
    int Execute(
        uint OutputControl,
        [MarshalAs(UnmanagedType.LPStr)] string Command,
        uint Flags);

    // Slot 64: ExecuteCommandFile
    void _VtblGap5_1();

    // Slot 65
    [PreserveSig]
    int GetNumberBreakpoints(out uint Number);

    // Slot 66
    [PreserveSig]
    int GetBreakpointByIndex(
        uint Index,
        [MarshalAs(UnmanagedType.Interface)] out IDebugBreakpoint Bp);

    // Slot 67
    [PreserveSig]
    int GetBreakpointById(
        uint Id,
        [MarshalAs(UnmanagedType.Interface)] out IDebugBreakpoint Bp);

    // Slot 68: GetBreakpointParameters
    void _VtblGap6_1();

    // Slot 69
    [PreserveSig]
    int AddBreakpoint(
        uint Type,
        uint DesiredId,
        [MarshalAs(UnmanagedType.Interface)] out IDebugBreakpoint Bp);

    // Slot 70
    [PreserveSig]
    int RemoveBreakpoint(
        [MarshalAs(UnmanagedType.Interface)] IDebugBreakpoint Bp);

    // Slots 71-89
    void _VtblGap7_19();

    // Slot 90
    [PreserveSig]
    int WaitForEvent(uint Flags, uint Timeout);

    // Slot 91
    [PreserveSig]
    int GetLastEventInformation(
        out uint Type,
        out uint ProcessId,
        out uint ThreadId,
        IntPtr ExtraInformation,
        uint ExtraInformationSize,
        out uint ExtraInformationUsed,
        IntPtr Description,
        uint DescriptionSize,
        out uint DescriptionUsed);
}
