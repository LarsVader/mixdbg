namespace MixDbg.Models;

/// <summary>
/// Result of a <c>WaitForEvent</c> call, abstracting the raw HRESULT
/// into meaningful outcomes for the engine loop.
/// </summary>
public enum WaitForEventResult
{
    /// <summary>An event occurred and the target is now stopped.</summary>
    EventOccurred,

    /// <summary>The wait timed out without an event (S_FALSE).</summary>
    Timeout,

    /// <summary>The call failed (HRESULT was negative). The target should be considered terminated.</summary>
    Failed,
}
