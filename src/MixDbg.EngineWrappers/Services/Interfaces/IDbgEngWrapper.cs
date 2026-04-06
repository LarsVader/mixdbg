using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless wrapper around the dbgeng COM interfaces. All mutable state lives
/// in <see cref="DbgEngWrapperModel"/>. Encapsulates COM interop so the rest
/// of the codebase never references dbgeng types directly.
/// Methods without explicit thread-safety notes must be called on the engine thread.
/// </summary>
public interface IDbgEngWrapper
{
    // ── Lifecycle ──

    /// <summary>Creates a new wrapper model (no COM objects yet).</summary>
    DbgEngWrapperModel CreateModel();

    /// <summary>
    /// Calls <c>DebugCreate</c> and initializes all COM interface references on the model.
    /// Must be called on the engine thread.
    /// </summary>
    void CreateEngine(DbgEngWrapperModel model);

    /// <summary>Launches a process under the debugger.</summary>
    void CreateProcess(DbgEngWrapperModel model, string cmdLine);

    /// <summary>Attaches to an existing process.</summary>
    void AttachProcess(DbgEngWrapperModel model, uint pid);

    /// <summary>Terminates the debugged process and ends the session. Thread-safe.</summary>
    void TerminateSession(DbgEngWrapperModel model);

    /// <summary>Detaches from the debugged process and ends the session. Thread-safe.</summary>
    void DetachSession(DbgEngWrapperModel model);

    // ── Symbols ──

    /// <summary>
    /// Configures symbol loading options (LoadLines, DeferredLoads, UndName)
    /// and sets the symbol/source paths.
    /// </summary>
    void InitializeSymbols(DbgEngWrapperModel model, string? symbolPath, string? sourcePath);

    /// <summary>Resolves a source file and line to a native address.</summary>
    (ulong Offset, bool Success) GetOffsetByLine(DbgEngWrapperModel model, uint line, string file);

    /// <summary>Gets the function name and displacement for a native address.</summary>
    (string Name, ulong Displacement)? GetNameByOffset(DbgEngWrapperModel model, ulong offset);

    /// <summary>Gets the source file and line number for a native address.</summary>
    (uint Line, string File)? GetLineByOffset(DbgEngWrapperModel model, ulong offset);

    /// <summary>Gets the module base address containing a native address.</summary>
    ulong? GetModuleByOffset(DbgEngWrapperModel model, ulong offset);

    // ── Execution ──

    /// <summary>Blocks until the target stops. Returns the HRESULT from WaitForEvent.</summary>
    int WaitForEvent(DbgEngWrapperModel model);

    /// <summary>Sets the execution status (Go, StepOver, StepInto, etc.).</summary>
    void SetExecutionStatus(DbgEngWrapperModel model, EngineExecutionStatus status);

    /// <summary>Gets the current execution status.</summary>
    EngineExecutionStatus GetExecutionStatus(DbgEngWrapperModel model);

    /// <summary>Requests the target to break. Thread-safe.</summary>
    void SetInterrupt(DbgEngWrapperModel model);

    /// <summary>
    /// Executes a dbgeng text command (e.g. "gu" for step-out).
    /// Returns the HRESULT.
    /// </summary>
    int ExecuteCommand(DbgEngWrapperModel model, string command);

    /// <summary>Gets information about the last debug event.</summary>
    EngineEventInfo GetLastEventInfo(DbgEngWrapperModel model);

    // ── Breakpoints ──

    /// <summary>
    /// Adds a software code breakpoint at the given offset.
    /// Returns the breakpoint ID and success flag.
    /// </summary>
    (uint BpId, bool Success) AddCodeBreakpoint(DbgEngWrapperModel model, ulong offset);

    /// <summary>
    /// Adds a hardware data breakpoint (execute) at the given address.
    /// Returns the breakpoint ID and success flag.
    /// </summary>
    (uint BpId, bool Success) AddHardwareBreakpoint(DbgEngWrapperModel model, ulong address, uint size);

    /// <summary>Removes a breakpoint by its engine-assigned ID.</summary>
    bool RemoveBreakpoint(DbgEngWrapperModel model, uint bpId);

    /// <summary>Returns the total number of breakpoints in the engine.</summary>
    uint GetBreakpointCount(DbgEngWrapperModel model);

    /// <summary>Gets the breakpoint ID at the given index, or null on failure.</summary>
    uint? GetBreakpointIdByIndex(DbgEngWrapperModel model, uint index);

    /// <summary>
    /// Creates a deferred breakpoint via the "bu" command for a source location
    /// that cannot yet be resolved (module not loaded).
    /// Returns the assigned breakpoint ID and success flag.
    /// </summary>
    (uint BpId, bool Success) AddDeferredBreakpoint(DbgEngWrapperModel model, string file, int line);

    // ── Stack ──

    /// <summary>
    /// Gets the native call stack. Also caches the raw dbgeng frame data
    /// internally for subsequent <see cref="SetScopeAndGetLocals"/> calls.
    /// </summary>
    NativeStackFrame[] GetStackTrace(DbgEngWrapperModel model, int maxFrames);

    // ── Scopes / Variables ──

    /// <summary>
    /// Sets the scope to the given frame (1-based ID from GetStackTrace)
    /// and allocates a variablesReference for the locals symbol group.
    /// Returns 0 if the frame is invalid or has no locals.
    /// </summary>
    int SetScopeAndGetLocals(DbgEngWrapperModel model, int frameId);

    /// <summary>
    /// Resolves variables for a previously allocated variablesReference handle.
    /// Expands child symbols as needed.
    /// </summary>
    VariableInfo[] GetVariables(DbgEngWrapperModel model, int variablesReference);

    /// <summary>
    /// Clears all variable references. Called on continue/step when variables become stale.
    /// </summary>
    void ClearVariables(DbgEngWrapperModel model);

    // ── Threads ──

    /// <summary>Gets the current dbgeng thread ID.</summary>
    uint GetCurrentThreadId(DbgEngWrapperModel model);

    /// <summary>Gets the OS thread ID of the current dbgeng thread.</summary>
    uint GetCurrentThreadSystemId(DbgEngWrapperModel model);

    /// <summary>Gets the dbgeng thread ID that triggered the last event.</summary>
    uint GetEventThreadId(DbgEngWrapperModel model);

    /// <summary>Enumerates all threads as (engine ID, OS system ID) pairs.</summary>
    (uint EngineId, uint SystemId)[] GetThreads(DbgEngWrapperModel model);

}