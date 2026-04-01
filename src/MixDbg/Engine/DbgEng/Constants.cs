using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

public static class DbgEngNative
{
    [DllImport("dbgeng.dll")]
    public static extern int DebugCreate(
        [In] ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object @interface);
}

// ── Execution status ────────────────────────────────────

public static class DebugStatus
{
    public const uint NoChange = 0;
    public const uint Go = 1;
    public const uint GoHandled = 2;
    public const uint GoNotHandled = 3;
    public const uint StepOver = 4;
    public const uint StepInto = 5;
    public const uint Break = 6;
    public const uint NoDebuggee = 7;
    public const uint StepBranch = 8;
}

// ── Attach flags ────────────────────────────────────────

public static class DebugAttach
{
    public const uint Default = 0;
    public const uint NonInvasive = 1;
    public const uint Existing = 2;
}

// ── Create process flags ────────────────────────────────

public static class CreateProcessFlags
{
    public const uint DebugProcess = 0x00000001;
    public const uint DebugOnlyThisProcess = 0x00000002;
    public const uint CreateNewConsole = 0x00000010;
}

// ── Breakpoint flags ────────────────────────────────────

public static class DebugBreakpointType
{
    public const uint Code = 0;
    public const uint Data = 1;
}

public static class DebugBreakpointFlag
{
    public const uint Enabled = 0x00000004;
}

// ── Event interest mask ─────────────────────────────────

[Flags]
public enum DebugEvent : uint
{
    Breakpoint = 0x00000001,
    Exception = 0x00000002,
    CreateThread = 0x00000004,
    ExitThread = 0x00000008,
    CreateProcess = 0x00000010,
    ExitProcess = 0x00000020,
    LoadModule = 0x00000040,
    UnloadModule = 0x00000080,
    SystemError = 0x00000100,
    SessionStatus = 0x00000200,
    ChangeDebuggeeState = 0x00000400,
    ChangeEngineState = 0x00000800,
    ChangeSymbolState = 0x00001000,
}

// ── EndSession flags ────────────────────────────────────

public static class DebugEnd
{
    public const uint Passive = 0;
    public const uint ActiveTerminate = 1;
    public const uint ActiveDetach = 2;
    public const uint EndReentrant = 3;
    public const uint EndDisconnect = 4;
}

// ── Execute flags ───────────────────────────────────────

public static class DebugExecute
{
    public const uint Default = 0;
    public const uint NotLogged = 2;
    public const uint NoRepeat = 4;
}

public static class DebugOutCtl
{
    public const uint ThisClient = 0;
    public const uint AllClients = 1;
    public const uint AllOther = 2;
    public const uint Ignore = 3;
}

// ── Symbol options ──────────────────────────────────────

public static class SymOpt
{
    public const uint LoadLines = 0x00000010;
    public const uint DeferredLoads = 0x00000004;
    public const uint UndName = 0x00000002;
}

// ── Scope symbol group ──────────────────────────────────

public static class DebugScopeGroup
{
    public const uint Arguments = 1;
    public const uint Locals = 2;
    public const uint All = 3;
}

// ── Structs ─────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_STACK_FRAME
{
    public ulong InstructionOffset;
    public ulong ReturnOffset;
    public ulong FrameOffset;
    public ulong StackOffset;
    public ulong FuncTableEntry;
    public ulong Params0, Params1, Params2, Params3;
    public ulong Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
    public int Virtual;
    public uint FrameNumber;
}
