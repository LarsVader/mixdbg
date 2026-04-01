using System.Runtime.InteropServices;
using System.Text;

namespace MixDbg.Engine.DbgEng;

// ── IDebugClient ────────────────────────────────────────
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

// ── IDebugControl ───────────────────────────────────────
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

// ── IDebugSymbols ───────────────────────────────────────
// GUID: 8c31e98c-983a-48a5-9016-6fe5d667a950

[ComImport, Guid("8c31e98c-983a-48a5-9016-6fe5d667a950")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugSymbols
{
    // Slots 0-2: GetSymbolOptions, AddSymbolOptions, RemoveSymbolOptions
    void _VtblGap1_3();

    // Slot 3
    [PreserveSig]
    int SetSymbolOptions(uint Options);

    // Slot 4
    [PreserveSig]
    int GetNameByOffset(
        ulong Offset,
        IntPtr NameBuffer,
        uint NameBufferSize,
        out uint NameSize,
        out ulong Displacement);

    // Slot 5: GetOffsetByName
    void _VtblGap2_1();

    // Slot 6: GetNearNameByOffset
    void _VtblGap3_1();

    // Slot 7
    [PreserveSig]
    int GetLineByOffset(
        ulong Offset,
        out uint Line,
        IntPtr FileBuffer,
        uint FileBufferSize,
        out uint FileSize,
        out ulong Displacement);

    // Slot 8
    [PreserveSig]
    int GetOffsetByLine(
        uint Line,
        [MarshalAs(UnmanagedType.LPStr)] string File,
        out ulong Offset);

    // Slots 9-28
    void _VtblGap4_20();

    // Slot 29
    [PreserveSig]
    int SetScope(
        ulong InstructionOffset,
        IntPtr ScopeFrame,
        IntPtr ScopeContext,
        uint ScopeContextSize);

    // Slot 30: ResetScope
    void _VtblGap5_1();

    // Slot 31: GetScopeSymbolGroup (skip for now)
    void _VtblGap6_1();

    // Slots 32-37
    void _VtblGap7_6();

    // Slot 38
    [PreserveSig]
    int SetSymbolPath(
        [MarshalAs(UnmanagedType.LPStr)] string Path);

    // Slot 39
    [PreserveSig]
    int AppendSymbolPath(
        [MarshalAs(UnmanagedType.LPStr)] string Addition);

    // Slots 40-44: image/source path getters
    void _VtblGap8_5();

    // Slot 45
    [PreserveSig]
    int SetSourcePath(
        [MarshalAs(UnmanagedType.LPStr)] string Path);
}

// ── IDebugBreakpoint ────────────────────────────────────
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
}

// ── IDebugSystemObjects ─────────────────────────────────
// GUID: 6b86fe2c-2c4f-4f0c-9da2-174311acc327

[ComImport, Guid("6b86fe2c-2c4f-4f0c-9da2-174311acc327")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugSystemObjects
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
}

// ── IDebugEventCallbacks ────────────────────────────────
// GUID: 337be28b-5036-4d72-b6bf-c45fbb9f2eaa
// We implement this interface to receive debug events.

[ComImport, Guid("337be28b-5036-4d72-b6bf-c45fbb9f2eaa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugEventCallbacks
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
