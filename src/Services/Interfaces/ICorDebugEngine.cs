using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// ICorDebug-based debug engine service. Uses ICorDebug interop debugging
/// for both managed (IL-level breakpoints) and native debugging.
/// All mutable state lives in <see cref="CorDebugEngineModel"/>.
/// </summary>
public interface ICorDebugEngine
{
    /// <summary>Creates a new engine model with dispose action wired up.</summary>
    CorDebugEngineModel CreateModel();

    /// <summary>Launches a process under ICorDebug. Blocks until the CLR has initialized.</summary>
    void Launch(CorDebugEngineModel model, string program, string? cwd, string[]? args = null);

    /// <summary>Attaches to a running process by PID.</summary>
    void Attach(CorDebugEngineModel model, uint pid);

    /// <summary>Continues execution. Sets configDone on first call.</summary>
    void Continue(CorDebugEngineModel model);

    /// <summary>Requests the target to break.</summary>
    void Break(CorDebugEngineModel model);

    /// <summary>Steps over one source line.</summary>
    void StepOver(CorDebugEngineModel model);

    /// <summary>Steps into the next call.</summary>
    void StepInto(CorDebugEngineModel model);

    /// <summary>Steps out of the current function.</summary>
    void StepOut(CorDebugEngineModel model);

    /// <summary>Sets breakpoints for a source file. Managed breakpoints use ICorDebugCode::CreateBreakpoint.</summary>
    Breakpoint[] SetBreakpoints(CorDebugEngineModel model, string filePath, SourceBreakpoint[] requested);

    /// <summary>Gets the current call stack.</summary>
    StackFrame[] GetStackTrace(CorDebugEngineModel model, int maxFrames);

    /// <summary>Gets the scopes (locals, arguments) for a stack frame.</summary>
    Scope[] GetScopes(CorDebugEngineModel model, int frameId);

    /// <summary>Gets variables for a variablesReference handle.</summary>
    Variable[] GetVariables(CorDebugEngineModel model, int variablesReference);

    /// <summary>Gets all debugger threads.</summary>
    DapThread[] GetThreads(CorDebugEngineModel model);

    /// <summary>Gets the thread ID that hit the last event.</summary>
    int GetStoppedThreadId(CorDebugEngineModel model);

    /// <summary>Terminates the debugged process.</summary>
    void Terminate(CorDebugEngineModel model);

    /// <summary>Detaches from the debugged process.</summary>
    void Detach(CorDebugEngineModel model);
}
