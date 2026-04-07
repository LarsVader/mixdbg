using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless deferred managed breakpoint resolution service. Handles JIT notifications,
/// DAC-based polling, module load binding, and profiler ENTER hook breakpoints.
/// All methods execute on the engine thread.
/// All mutable state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
public interface IManagedBreakpointResolver
{
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
    /// Called when a new module is loaded (from dbgeng LoadModule callback).
    /// Re-enumerates ICorDebug modules and tries to bind any pending managed
    /// breakpoints against newly loaded assemblies.
    /// </summary>
    Breakpoint[] OnModuleLoad(NativeDebuggerModel model);

    /// <summary>
    /// Called on managed module loads. Re-enumerates ICorDebug modules and tries
    /// to bind any pending managed breakpoints against newly loaded assemblies.
    /// Sends DAP breakpoint events for each resolved breakpoint.
    /// </summary>
    void TryBindManagedBreakpointsOnModuleLoad(NativeDebuggerModel model);

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

    /// <summary>
    /// Starts a timer that periodically interrupts the target to check if deferred
    /// managed breakpoints can be resolved after JIT compilation.
    /// </summary>
    void StartDeferredBreakpointPoller(NativeDebuggerModel model);
}
