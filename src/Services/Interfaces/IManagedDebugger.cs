using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Protocol;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless managed debugging service. Handles ICorDebug V4 runtime lifecycle,
/// managed stack frame resolution, and CLR initialization orchestration.
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

    /// <summary>Whether the ICorDebug V4 runtime has been initialized.</summary>
    bool IsInitialized(NativeDebuggerModel model);

    /// <summary>
    /// Initializes managed debugging when the CLR is detected. Applies pending managed
    /// breakpoints and optionally starts the deferred breakpoint poller.
    /// </summary>
    void TryInitializeManaged(NativeDebuggerModel model);

    /// <summary>
    /// Gets managed stack frames for the current thread using ICorDebug V4
    /// frame enumeration with PDB-based source resolution.
    /// </summary>
    StackFrame[] GetManagedStackFrames(NativeDebuggerModel model);

    /// <summary>
    /// Resolves a native instruction pointer to a managed method name and source
    /// location using the profiler's JIT method map and PDB data. Returns <c>null</c>
    /// if the IP doesn't belong to a known JIT'd method.
    /// </summary>
    (string Name, Source? Source, int Line)? ResolveFrameFromProfilerData(NativeDebuggerModel model, ulong instructionPointer);

    /// <summary>
    /// Overlays managed frame info (method names, source locations) onto native stack frames
    /// that lack source information.
    /// </summary>
    void MergeManagedFrames(NativeDebuggerModel model, StackFrame[] nativeFrames);

    /// <summary>
    /// Attempts to read managed locals for a frame identified by its instruction pointer.
    /// Uses the profiler's JIT method map to find the method token, assembly path, and IL
    /// offset, then reads parameters and locals via SOS <c>!clrstack -a</c> through
    /// <see cref="IDbgEngWrapper.ExecuteCommandWithCapture"/>.
    /// Returns a managed variablesReference handle, or 0 on failure.
    /// </summary>
    int TryGetManagedLocals(NativeDebuggerModel model, ulong instructionPointer);

    /// <summary>
    /// Finds the assembly DLL path by matching the assembly name against loaded ICorDebug
    /// modules. Used by managed stepping to locate PDBs for sequence point resolution.
    /// </summary>
    string? FindAssemblyPath(NativeDebuggerModel model, string assemblyName);
}
