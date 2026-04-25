namespace MixDbg.Models;

/// <summary>
/// Reason why the debugger stopped, as determined by
/// <see cref="Services.Interfaces.IStepResolutionService.DetermineStopReason"/>.
/// Maps to DAP stopped event reason strings.
/// </summary>
public enum StopReason
{
    /// <summary>A user breakpoint was hit.</summary>
    Breakpoint,

    /// <summary>A step operation completed.</summary>
    Step,

    /// <summary>The user requested a pause (break).</summary>
    Pause,
}

/// <summary>
/// Extension methods for <see cref="StopReason"/>.
/// </summary>
public static class StopReasonExtensions
{
    /// <summary>
    /// Converts the enum value to the DAP protocol string for the stopped event reason field.
    /// </summary>
    public static string ToDapString(this StopReason reason) => reason switch
    {
        StopReason.Breakpoint => "breakpoint",
        StopReason.Step => "step",
        StopReason.Pause => "pause",
        _ => "unknown",
    };
}
