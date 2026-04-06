using MixDbg.Models;
using MixDbg.Models.Dap;

namespace MixDbg.Services;

/// <summary>
/// Stateless managed debugging service using ICorDebug V4
/// (<c>ICLRDebugging::OpenVirtualProcess</c>) piggybacked on the dbgeng session.
/// Sets IL-level breakpoints that handle pre-JIT methods automatically.
/// All methods execute on the engine thread.
/// All mutable state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
public interface IManagedDebugger
{
    /// <summary>
    /// Initializes ICorDebug V4 via <c>ICLRDebugging::OpenVirtualProcess</c>,
    /// piggybacking on the existing dbgeng session. Must be called on the engine
    /// thread after the CLR has loaded and the target is stopped.
    /// </summary>
    /// <returns><c>true</c> if initialization succeeded.</returns>
    bool InitializeRuntime(NativeDebuggerModel model);

    /// <summary>
    /// Sets managed breakpoints for a source file using PDB resolution to find
    /// method tokens, then <c>ICorDebugCode::CreateBreakpoint</c> for IL-level
    /// breakpoints. Handles pre-JIT methods automatically.
    /// </summary>
    Breakpoint[] SetManagedBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested);

    /// <summary>
    /// Gets managed stack frames for the current thread using ICorDebug V4
    /// frame enumeration with PDB-based source resolution.
    /// </summary>
    StackFrame[] GetManagedStackFrames(NativeDebuggerModel model);

    /// <summary>Whether the ICorDebug V4 runtime has been initialized.</summary>
    bool IsInitialized(NativeDebuggerModel model);

    /// <summary>
    /// Called when a new module is loaded (from dbgeng LoadModule callback).
    /// Re-enumerates ICorDebug modules and tries to bind any pending managed
    /// breakpoints against newly loaded assemblies.
    /// </summary>
    Breakpoint[] OnModuleLoad(NativeDebuggerModel model);

    /// <summary>
    /// Attempts to resolve deferred managed breakpoints by checking whether
    /// <c>GetFunctionFromToken(token).NativeCode</c> is now available (after
    /// JIT compilation). Resolved breakpoints are set as hardware breakpoints
    /// (<c>ba e1</c>) at the native code address.
    /// </summary>
    /// <returns>DAP breakpoint objects for each successfully resolved breakpoint.</returns>
    Breakpoint[] TryResolveDeferredBreakpoints(NativeDebuggerModel model);

    /// <summary>
    /// Handles a JIT compilation notification from the CLR profiler. If the
    /// JIT'd method matches a deferred managed breakpoint (by token + assembly name),
    /// sets a hardware breakpoint at the reported native address immediately.
    /// </summary>
    /// <returns>DAP breakpoint objects for each matched and resolved breakpoint.</returns>
    Breakpoint[] HandleJitNotifications(NativeDebuggerModel model);

    /// <summary>
    /// Resolves a native instruction pointer to a managed method name and source
    /// location using the profiler's JIT method map and PDB data. Returns <c>null</c>
    /// if the IP doesn't belong to a known JIT'd method.
    /// </summary>
    (string Name, Source? Source, int Line)? ResolveFrameFromProfilerData(NativeDebuggerModel model, ulong instructionPointer);

    /// <summary>
    /// Sets a transient hardware breakpoint at the given native address.
    /// Used by enter hook breakpoints — set on method entry, removed on Continue.
    /// </summary>
    void SetTransientBreakpoint(NativeDebuggerModel model, ulong address, string filePath, int line);

    /// <summary>
    /// Resolves exact (assembly, token) pairs from breakpoint file:line hints by
    /// searching for PDB files on disk. Used to tell the CLR profiler which exact
    /// methods to block on during JIT (zero overhead for all other JITs).
    /// </summary>
    List<(string Assembly, int Token)> ResolveTokensFromBreakpoints(IEnumerable<(string FilePath, int Line)> breakpoints);

    /// <summary>
    /// Resolves C++/CLI assembly names from breakpoint file:line hints. Returns
    /// distinct assembly names for C++/CLI files that have CLRSupport in their vcxproj.
    /// Used to set <c>MIXDBG_WATCH_ASSEMBLIES</c> so the profiler hooks all methods
    /// from these assemblies (enabling first-click breakpoints).
    /// </summary>
    List<string> ResolveWatchAssemblies(IEnumerable<(string FilePath, int Line)> breakpoints);

    /// <summary>
    /// Initializes managed debugging when the CLR is detected. Applies pending managed
    /// breakpoints and optionally starts the deferred breakpoint poller.
    /// </summary>
    void TryInitializeManaged(NativeDebuggerModel model);

    /// <summary>
    /// Called on managed module loads. Re-enumerates ICorDebug modules and tries
    /// to bind any pending managed breakpoints against newly loaded assemblies.
    /// </summary>
    void TryBindManagedBreakpointsOnModuleLoad(NativeDebuggerModel model);

    /// <summary>
    /// Removes transient hardware breakpoints set by enter hook notifications.
    /// Only active when profiler hooks are in use.
    /// </summary>
    void RemoveTransientManagedBreakpoints(NativeDebuggerModel model);

    /// <summary>
    /// Overlays managed frame info (method names, source locations) onto native stack frames
    /// that lack source information.
    /// </summary>
    void MergeManagedFrames(NativeDebuggerModel model, StackFrame[] nativeFrames);

    /// <summary>
    /// Starts a timer that periodically interrupts the target to check if deferred
    /// managed breakpoints can be resolved after JIT compilation.
    /// </summary>
    void StartDeferredBreakpointPoller(NativeDebuggerModel model);

    /// <summary>
    /// Processes pending JIT notifications and attempts to resolve deferred managed
    /// breakpoints. Sends DAP breakpoint events for each resolved breakpoint.
    /// Called from the engine loop on each stop.
    /// </summary>
    void ProcessPendingManagedBreakpoints(NativeDebuggerModel model);

    /// <summary>
    /// Handles a pending ENTER notification from the profiler. Sets a transient hardware
    /// breakpoint at the exact source line, ACKs the profiler, and resumes execution.
    /// Returns <c>true</c> if an ENTER was handled (caller should auto-continue).
    /// </summary>
    bool HandleEnterBreakpoint(NativeDebuggerModel model);
}