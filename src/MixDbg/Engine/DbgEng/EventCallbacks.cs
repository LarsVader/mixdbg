using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Receives debug events from dbgeng. Called on the engine thread.
/// </summary>
public sealed class EventCallbacks : IDebugEventCallbacks
{
    public event Action<IDebugBreakpoint>? OnBreakpoint;
    public event Action<uint>? OnExitProcess;
    public event Action<string?, string?>? OnLoadModule;
    public event Action<string?>? OnCreateProcess;
    public event Action<uint>? OnExceptionEvent;

    // Return DEBUG_STATUS values to control execution after events.
    // DEBUG_STATUS_NO_CHANGE (0xF) = let the engine decide.
    private const int NoChange = 0x0000000F;
    // DEBUG_STATUS_GO_NOT_HANDLED = 0x08
    private const int GoNotHandled = 8;

    public int GetInterestMask(out uint Mask)
    {
        Mask = (uint)(
            DebugEvent.Breakpoint |
            DebugEvent.Exception |
            DebugEvent.CreateProcess |
            DebugEvent.ExitProcess |
            DebugEvent.LoadModule |
            DebugEvent.CreateThread |
            DebugEvent.ExitThread |
            DebugEvent.SessionStatus |
            DebugEvent.ChangeDebuggeeState |
            DebugEvent.ChangeEngineState);
        return 0; // S_OK
    }

    public int Breakpoint(IDebugBreakpoint Bp)
    {
        OnBreakpoint?.Invoke(Bp);
        return 0x00000001; // DEBUG_STATUS_BREAK
    }

    public int Exception(IntPtr Exception, uint FirstChance)
    {
        OnExceptionEvent?.Invoke(FirstChance);
        return GoNotHandled;
    }

    public int CreateThread(ulong Handle, ulong DataOffset, ulong StartOffset)
        => NoChange;

    public int ExitThread(uint ExitCode)
        => NoChange;

    public int CreateProcess(ulong ImageFileHandle, ulong Handle,
        ulong BaseOffset, uint ModuleSize, string? ModuleName,
        string? ImageName, uint CheckSum, uint TimeDateStamp,
        ulong InitialThreadHandle, ulong ThreadDataOffset, ulong StartOffset)
    {
        OnCreateProcess?.Invoke(ImageName);
        return NoChange;
    }

    public int ExitProcess(uint ExitCode)
    {
        OnExitProcess?.Invoke(ExitCode);
        return 0x00000001; // DEBUG_STATUS_BREAK
    }

    public int LoadModule(ulong ImageFileHandle, ulong BaseOffset,
        uint ModuleSize, string? ModuleName, string? ImageName,
        uint CheckSum, uint TimeDateStamp)
    {
        OnLoadModule?.Invoke(ModuleName, ImageName);
        return NoChange;
    }

    public int UnloadModule(string? ImageBaseName, ulong BaseOffset)
        => NoChange;

    public int SystemError(uint Error, uint Level)
        => NoChange;

    public int SessionStatus(uint Status)
        => 0; // S_OK

    public int ChangeDebuggeeState(uint Flags, ulong Argument)
        => 0;

    public int ChangeEngineState(uint Flags, ulong Argument)
        => 0;

    public int ChangeSymbolState(uint Flags, ulong Argument)
        => 0;
}
