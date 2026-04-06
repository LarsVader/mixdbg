using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Threads;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless dbgeng wrapper service. All mutable state lives in
/// <see cref="NativeDebuggerModel"/>. Methods suffixed with "OnEngine"
/// must be called on the engine thread (via <c>model.Commands.Add</c>
/// or <c>model.QueueEngineQuery</c>). Other methods are thread-safe.
/// </summary>
public interface INativeDebugger
{
    /// <summary>Creates a new engine model with dispose action wired up.</summary>
    NativeDebuggerModel CreateModel();

    /// <summary>Starts the engine thread. Caller must set model parameters first, then wait on EngineReady.</summary>
    void StartEngineThread(NativeDebuggerModel model);

    // ── Engine-thread methods (caller dispatches via Commands.Add) ──

    /// <summary>Resumes execution, clears transient BPs, and re-enables profiler hooks.</summary>
    void ExecuteContinueOnEngine(NativeDebuggerModel model);

    /// <summary>Steps over/into by setting the execution status.</summary>
    void ExecuteStepOnEngine(NativeDebuggerModel model, EngineExecutionStatus stepKind);

    /// <summary>Steps out via the dbgeng "gu" (go up) command.</summary>
    void ExecuteStepOutOnEngine(NativeDebuggerModel model);

    /// <summary>Sets breakpoints for a source file. Uses deferred breakpoints when symbols are not yet loaded.</summary>
    Breakpoint[] SetBreakpointsOnEngine(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested);

    /// <summary>Gets the current call stack with resolved function names and source locations.</summary>
    StackFrame[] GetStackTraceOnEngine(NativeDebuggerModel model, int maxFrames);

    /// <summary>Gets the scopes (locals, arguments) for a stack frame by frame ID.</summary>
    Scope[] GetScopesOnEngine(NativeDebuggerModel model, int frameId);

    /// <summary>Gets variables for a variablesReference handle.</summary>
    Variable[] GetVariablesOnEngine(NativeDebuggerModel model, int variablesReference);

    /// <summary>Gets all debugger threads with engine and system IDs.</summary>
    DapThread[] GetThreadsOnEngine(NativeDebuggerModel model);

    /// <summary>Gets the engine thread ID that hit the last event.</summary>
    int GetStoppedThreadIdOnEngine(NativeDebuggerModel model);

    // ── Thread-safe methods ──

    /// <summary>Requests the target to break. Thread-safe — uses SetInterrupt.</summary>
    void Break(NativeDebuggerModel model);

    /// <summary>Terminates the debugged process and ends the session.</summary>
    void Terminate(NativeDebuggerModel model);

    /// <summary>Detaches from the debugged process without terminating it.</summary>
    void Detach(NativeDebuggerModel model);
}