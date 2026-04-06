namespace MixDbg.Models;

/// <summary>
/// A managed stack frame resolved via ICorDebug, with method name and
/// source location. Returned by <see cref="Services.ICorDebugWrapper.GetManagedStackFrames"/>.
/// </summary>
public sealed record ManagedFrameInfo(string Name, string? SourceFile, int Line, int ILOffset);
