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
    public event Action<string?, string?, ulong>? OnLoadModule;
    public event Action<string?>? OnCreateProcess;
    public event Action<uint>? OnExceptionEvent;

    /// <summary>
    /// Fired on CLR notification exceptions (0xe0444143) which occur during JIT
    /// compilation and other CLR internal events.
    /// </summary>
    public event Action? OnClrNotification;

    /// <summary>
    /// When set to <c>true</c> by the <see cref="OnClrNotification"/> handler,
    /// the next CLR notification exception returns BREAK instead of GO_HANDLED,
    /// causing <c>WaitForEvent</c> to return so deferred breakpoints can be resolved.
    /// Reset to <c>false</c> after use.
    /// </summary>
    internal bool ClrNotificationShouldBreak;

    /// <summary>
    /// Fired on EXCEPTION_BREAKPOINT (0x80000003) first-chance exceptions.
    /// The parameter is the exception address — used to detect managed breakpoint
    /// hits from ICorDebug IL breakpoints (which patch code with <c>int3</c>).
    /// </summary>
    public event Action<ulong>? OnExceptionBreakpoint;

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

        // CLR notification exceptions (e0444143) are internal CLR events.
        // When deferred managed breakpoints exist, break so the engine loop
        // can check if the JIT compiled the target method.
        if (exceptionCode == 0xe0444143)
        {
            OnClrNotification?.Invoke();
            if (ClrNotificationShouldBreak)
            {
                ClrNotificationShouldBreak = false;
                return StatusBreak;
            }
            return StatusGoHandled;
        }

        // EXCEPTION_BREAKPOINT (0x80000003) or EXCEPTION_SINGLE_STEP (0x80000004).
        // 0x80000003: may be a managed IL breakpoint hit (int3 patches).
        // 0x80000004: hardware execution breakpoint (ba e1) — fires as single-step on x64.
        if ((exceptionCode == 0x80000003 || exceptionCode == 0x80000004) && Exception != IntPtr.Zero)
        {
            // EXCEPTION_RECORD64.ExceptionAddress is at offset 16.
            ulong exceptionAddress = (ulong)Marshal.ReadInt64(Exception, 16);
            OnExceptionBreakpoint?.Invoke(exceptionAddress);
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
        OnLoadModule?.Invoke(ModuleName, ImageName, BaseOffset);
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
