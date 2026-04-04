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
}
