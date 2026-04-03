using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Receives debug events from dbgeng. Called on the engine thread.
/// Callback return values are DEBUG_STATUS constants that control
/// whether WaitForEvent returns (BREAK) or continues (GO/NO_CHANGE).
/// </summary>
public sealed class EventCallbacks : IDebugEventCallbacks
{
    public event Action<IDebugBreakpoint>? OnBreakpoint;
    public event Action<uint>? OnExitProcess;
    public event Action<string?, string?>? OnLoadModule;
    public event Action<string?>? OnCreateProcess;
    public event Action<uint>? OnExceptionEvent;

    /// <summary>
    /// Fired on CLR notification exceptions (0xe0444143) which occur during JIT
    /// compilation. Used to resolve deferred managed breakpoints at the moment
    /// the method is compiled — before the compiled code executes.
    /// </summary>
    public event Action? OnClrNotification;

    private const int StatusGo = 1;        // DEBUG_STATUS_GO
    private const int StatusGoHandled = 2;  // DEBUG_STATUS_GO_HANDLED
    private const int StatusBreak = 6;      // DEBUG_STATUS_BREAK

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
        return StatusBreak;
    }

    public int Exception(IntPtr Exception, uint FirstChance)
    {
        // Read the exception code from EXCEPTION_RECORD64.ExceptionCode (first uint).
        uint exceptionCode = Exception != IntPtr.Zero
            ? (uint)Marshal.ReadInt32(Exception)
            : 0;

        // CLR notification exceptions (e0444143) are used internally by SOS
        // for JIT/module load notifications. Let them pass through without
        // breaking — SOS handles them to resolve deferred breakpoints.
        if (exceptionCode == 0xe0444143)
        {
            OnClrNotification?.Invoke();
            return StatusGoHandled;
        }

        OnExceptionEvent?.Invoke(FirstChance);
        // Break on all other exceptions so WaitForEvent returns.
        // The engine loop decides whether to auto-continue.
        return StatusBreak;
    }

    public int CreateThread(ulong Handle, ulong DataOffset, ulong StartOffset)
        => StatusGo;

    public int ExitThread(uint ExitCode)
        => StatusGo;

    public int CreateProcess(ulong ImageFileHandle, ulong Handle,
        ulong BaseOffset, uint ModuleSize, string? ModuleName,
        string? ImageName, uint CheckSum, uint TimeDateStamp,
        ulong InitialThreadHandle, ulong ThreadDataOffset, ulong StartOffset)
    {
        OnCreateProcess?.Invoke(ImageName);
        // Break so WaitForEvent returns after process creation.
        return StatusBreak;
    }

    public int ExitProcess(uint ExitCode)
    {
        OnExitProcess?.Invoke(ExitCode);
        return StatusBreak;
    }

    public int LoadModule(ulong ImageFileHandle, ulong BaseOffset,
        uint ModuleSize, string? ModuleName, string? ImageName,
        uint CheckSum, uint TimeDateStamp)
    {
        OnLoadModule?.Invoke(ModuleName, ImageName);
        return StatusGo;
    }

    public int UnloadModule(string? ImageBaseName, ulong BaseOffset)
        => StatusGo;

    public int SystemError(uint Error, uint Level)
        => StatusBreak;

    public int SessionStatus(uint Status)
        => 0;

    public int ChangeDebuggeeState(uint Flags, ulong Argument)
        => 0;

    public int ChangeEngineState(uint Flags, ulong Argument)
        => 0;

    public int ChangeSymbolState(uint Flags, ulong Argument)
        => 0;
}
