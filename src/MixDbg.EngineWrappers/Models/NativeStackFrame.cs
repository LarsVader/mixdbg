namespace MixDbg.Models;

/// <summary>
/// A single frame from a native stack trace. Exposes only the instruction
/// pointer needed for symbol resolution; the full dbgeng frame data is
/// cached internally by the wrapper for scope/variable operations.
/// </summary>
public readonly record struct NativeStackFrame(ulong InstructionOffset);
