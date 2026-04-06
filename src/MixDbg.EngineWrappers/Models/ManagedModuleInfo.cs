namespace MixDbg.Models;

/// <summary>
/// Information about a loaded managed module, without exposing ICorDebug types.
/// Returned by <see cref="Services.ICorDebugWrapper.GetModules"/>.
/// </summary>
public sealed record ManagedModuleInfo(string? Path, string? PdbPath, long BaseAddress);