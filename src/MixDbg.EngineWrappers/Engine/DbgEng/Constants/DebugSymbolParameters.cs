using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Native DEBUG_SYMBOL_PARAMETERS struct from dbgeng.h.
/// Describes a symbol's module, type, parent, sub-element count, and flags.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DEBUG_SYMBOL_PARAMETERS
{
    public ulong Module;
    public uint TypeId;
    public uint ParentSymbol;
    public uint SubElements;
    public uint Flags;
    public ulong Reserved;
}