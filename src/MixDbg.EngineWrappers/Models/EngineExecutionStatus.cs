namespace MixDbg.Models;

/// <summary>
/// Debug engine execution status. Maps to dbgeng DEBUG_STATUS constants
/// but decouples the rest of the codebase from the COM layer.
/// </summary>
public enum EngineExecutionStatus : uint
{
    /// <summary>No status change.</summary>
    NoChange = 0,

    /// <summary>Continue execution.</summary>
    Go = 1,

    /// <summary>Continue, mark exception as handled.</summary>
    GoHandled = 2,

    /// <summary>Continue, mark exception as not handled.</summary>
    GoNotHandled = 3,

    /// <summary>Step over the next source line or instruction.</summary>
    StepOver = 4,

    /// <summary>Step into the next source line or instruction.</summary>
    StepInto = 5,

    /// <summary>Target is broken (stopped).</summary>
    Break = 6,

    /// <summary>No debuggee is attached.</summary>
    NoDebuggee = 7,
}
