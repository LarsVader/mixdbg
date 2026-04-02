using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless dbgeng wrapper service. Manages a dedicated engine thread
/// for COM thread affinity, a command queue for cross-thread operations,
/// and translates dbgeng events into DAP events.
/// All mutable state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
public interface INativeDebugger
{
    /// <summary>Creates a new engine model with dispose action wired up.</summary>
    NativeDebuggerModel CreateModel();

    /// <summary>Launches a process under the debugger. Blocks until the engine thread has initialized.</summary>
    void Launch(NativeDebuggerModel model, string program, string? cwd, string? symbolPath);

    /// <summary>Attaches to a running process by PID. Blocks until the engine thread has initialized.</summary>
    void Attach(NativeDebuggerModel model, uint pid, string? symbolPath);

    /// <summary>Queues a continue command. Sets configDone on first call.</summary>
    void Continue(NativeDebuggerModel model);

    /// <summary>Requests the target to break. Thread-safe — can be called while running.</summary>
    void Break(NativeDebuggerModel model);

    /// <summary>Steps over one source line.</summary>
    void StepOver(NativeDebuggerModel model);

    /// <summary>Steps into the next call.</summary>
    void StepInto(NativeDebuggerModel model);

    /// <summary>Steps out of the current function via the dbgeng "gu" command.</summary>
    void StepOut(NativeDebuggerModel model);

    /// <summary>Sets breakpoints for a source file on the engine thread. Uses deferred breakpoints when symbols are not yet loaded.</summary>
    Breakpoint[] SetBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested);

    /// <summary>Gets the current call stack with resolved function names and source locations.</summary>
    StackFrame[] GetStackTrace(NativeDebuggerModel model, int maxFrames);

    /// <summary>Gets the scopes (locals, arguments) for a stack frame by frame ID.</summary>
    Scope[] GetScopes(NativeDebuggerModel model, int frameId);

    /// <summary>Gets variables for a variablesReference handle allocated by GetScopes or a prior GetVariables call.</summary>
    Variable[] GetVariables(NativeDebuggerModel model, int variablesReference);

    /// <summary>Gets all debugger threads with engine and system IDs.</summary>
    DapThread[] GetThreads(NativeDebuggerModel model);

    /// <summary>Gets the engine thread ID that hit the last event.</summary>
    int GetStoppedThreadId(NativeDebuggerModel model);

    /// <summary>Terminates the debugged process and ends the session.</summary>
    void Terminate(NativeDebuggerModel model);

    /// <summary>Detaches from the debugged process without terminating it.</summary>
    void Detach(NativeDebuggerModel model);
}
