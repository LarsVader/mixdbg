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
    /// Drains <see cref="NativeDebuggerModel.ProfilerNotifications"/> and dispatches
    /// each notification:
    /// <list type="bullet">
    /// <item>JIT: merges the reported method into <see cref="NativeDebuggerModel.ManagedBpPlans"/> (if a deferred BP matches).</item>
    /// <item>ENTER: on first activation, installs HW BPs for every site in the plan and ACKs the profiler. On nested activations, increments the count and ACKs immediately.</item>
    /// <item>LEAVE/TAILCALL: decrements the activation count. When it reaches 0, removes all HW BPs installed for this method and drops the <see cref="NativeDebuggerModel.ActiveMethodBreakpoints"/> entry.</item>
    /// </list>
    /// Returns <c>true</c> if the event that brought us here was a bookkeeping-only
    /// stop (ENTER on an unplanned method, LEAVE decrement) and the caller should
    /// auto-continue. Returns <c>false</c> if no notifications were processed or a
    /// user-visible stop should proceed.
    /// </summary>
    bool ProcessProfilerNotifications(NativeDebuggerModel model);

    /// <summary>
    /// Starts a timer that periodically interrupts the target to check if deferred
    /// managed breakpoints can be resolved after JIT compilation.
    /// </summary>
    void StartDeferredBreakpointPoller(NativeDebuggerModel model);
}
