using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless managed debugging service using ClrMD for runtime inspection
/// and SOS <c>!bpmd</c> for managed breakpoints. All methods execute on
/// the engine thread (called from within queued commands).
/// All mutable state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
public interface IManagedDebugger
{
    /// <summary>
    /// Initializes the ClrMD runtime from the existing dbgeng client and
    /// loads the SOS debugger extension. Must be called on the engine thread
    /// after the CLR has loaded.
    /// </summary>
    /// <returns><c>true</c> if initialization succeeded.</returns>
    bool InitializeRuntime(NativeDebuggerModel model);

    /// <summary>
    /// Sets managed breakpoints for a source file using ClrMD to resolve
    /// file:line to method names, then SOS <c>!bpmd</c> to set them.
    /// </summary>
    Breakpoint[] SetManagedBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested);

    /// <summary>
    /// Gets managed stack frames for the current thread using ClrMD.
    /// Returns frames with method names and source locations (from PDB).
    /// </summary>
    StackFrame[] GetManagedStackFrames(NativeDebuggerModel model);

    /// <summary>Whether the ClrMD runtime and SOS are loaded.</summary>
    bool IsInitialized(NativeDebuggerModel model);

    /// <summary>
    /// Checks all deferred managed breakpoints to see if any methods have been
    /// JIT-compiled since last check. For resolved methods, sets hardware execution
    /// breakpoints (<c>ba e1</c>) via CPU debug registers. Returns DAP breakpoint
    /// objects for any that were successfully resolved.
    /// </summary>
    Breakpoint[] TryResolveDeferredBreakpoints(NativeDebuggerModel model);
}
