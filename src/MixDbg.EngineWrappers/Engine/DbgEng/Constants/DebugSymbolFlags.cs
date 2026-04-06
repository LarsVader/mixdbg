namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Flags from DEBUG_SYMBOL_PARAMETERS.Flags in dbgeng.h.
/// </summary>
internal static class DebugSymbolFlags
{
    public const uint ExpansionLevelMask = 0x0000000F;
    public const uint Expanded = 0x00000010;
    public const uint ReadOnly = 0x00000020;
    public const uint IsArray = 0x00000040;
    public const uint IsFloat = 0x00000080;
    public const uint IsArgument = 0x00000100;
    public const uint IsLocal = 0x00000200;
}
