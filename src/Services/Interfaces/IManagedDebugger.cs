using MixDbg.Dap;
using MixDbg.Models;

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
    /// Resolves exact (assembly, token) pairs from breakpoint file:line hints by
    /// searching for PDB files on disk. Used to tell the CLR profiler which exact
    /// methods to block on during JIT (zero overhead for all other JITs).
    /// </summary>
    List<(string Assembly, int Token)> ResolveTokensFromBreakpoints(IEnumerable<(string FilePath, int Line)> breakpoints);
}
