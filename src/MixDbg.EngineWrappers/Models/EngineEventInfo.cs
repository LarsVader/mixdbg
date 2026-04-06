namespace MixDbg.Models;

/// <summary>
/// Information about the last debug engine event, returned by
/// <see cref="Services.IDbgEngWrapper.GetLastEventInfo"/>.
/// </summary>
public sealed record EngineEventInfo(uint Type, uint ProcessId, uint ThreadId, string Description);
