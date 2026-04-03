namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Access type flags for hardware (data/processor) breakpoints.
/// Used with <see cref="IDebugBreakpoint.SetDataParameters"/>.
/// </summary>
public static class DebugBreakAccess
{
    public const uint Read = 0x00000001;    // DEBUG_BREAK_READ
    public const uint Write = 0x00000002;   // DEBUG_BREAK_WRITE
    public const uint Execute = 0x00000004; // DEBUG_BREAK_EXECUTE
    public const uint Io = 0x00000008;      // DEBUG_BREAK_IO
}
