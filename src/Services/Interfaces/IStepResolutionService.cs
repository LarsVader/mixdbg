using MixDbg.Models;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless service that resolves what action to take after a debug stop during
/// stepping. Determines stop reasons, validates step landings, and manages the
/// lifecycle of managed step state (temp BPs, one-shot sites).
/// All methods must be called on the engine thread.
/// </summary>
public interface IStepResolutionService
{
    /// <summary>
    /// Examines model flags (<see cref="NativeDebuggerModel.ActiveManagedStep"/>,
    /// <see cref="NativeDebuggerModel.HitUserBreakpoint"/>,
    /// <see cref="NativeDebuggerModel.Stepping"/>,
    /// <see cref="NativeDebuggerModel.PauseRequested"/>) and returns the reason
    /// the debugger should report to DAP, or <see cref="StopReason.Continue"/>
    /// if this is a system stop that should be auto-continued.
    /// </summary>
    StopReason DetermineStopReason(NativeDebuggerModel model);

    /// <summary>
    /// After a native step completes, checks whether the current instruction pointer is on a useful
    /// source line. Returns <see cref="StepAutoAction.None"/> if normal,
    /// <see cref="StepAutoAction.ReStep"/> if on the same line (no progress),
    /// or <see cref="StepAutoAction.StepOut"/> if on a sourceless frame.
    /// </summary>
    StepAutoAction CheckStepLanding(NativeDebuggerModel model);

    /// <summary>
    /// Completes a managed step by removing temp breakpoints and clearing state.
    /// </summary>
    void CompleteManagedStep(NativeDebuggerModel model);
}

/// <summary>
/// Action to take after a native step lands on the current IP.
/// </summary>
public enum StepAutoAction
{
    /// <summary>Normal stop — present to user.</summary>
    None,

    /// <summary>Re-issue the same step (same line, deeper stack, or closing brace).</summary>
    ReStep,

    /// <summary>Auto step-out (sourceless frame).</summary>
    StepOut,
}
