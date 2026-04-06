using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng.Interfaces;

// GUID: 6a7ccc5f-fb5e-4dcc-b41c-6c20307bccc7

/// <summary>
/// COM interface for dbgeng symbol group inspection. Provides access to
/// symbol names, types, values, and expansion for struct/array members.
/// Vtable slots verified against dbgeng.h IDebugSymbolGroup2.
/// </summary>
[ComImport, Guid("6a7ccc5f-fb5e-4dcc-b41c-6c20307bccc7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugSymbolGroup2
{
    // ── IDebugSymbolGroup ──

    // Slot 0
    [PreserveSig]
    int GetNumberSymbols(out uint Number);

    // Slot 1: AddSymbol
    void _VtblGap1_1();

    // Slot 2: RemoveSymbolByName
    void _VtblGap2_1();

    // Slot 3: RemoveSymbolByIndex
    void _VtblGap3_1();

    // Slot 4
    [PreserveSig]
    int GetSymbolName(
        uint Index,
        IntPtr Buffer,
        uint BufferSize,
        out uint NameSize);

    // Slot 5
    [PreserveSig]
    int GetSymbolParameters(
        uint Start,
        uint Count,
        IntPtr Params);

    // Slot 6
    [PreserveSig]
    int ExpandSymbol(
        uint Index,
        [MarshalAs(UnmanagedType.Bool)] bool Expand);

    // Slots 7-9: OutputSymbols, WriteSymbol, OutputAsType
    void _VtblGap4_3();

    // ── IDebugSymbolGroup2 ──

    // Slots 10-14: Wide variants (AddSymbolWide, RemoveSymbolByNameWide,
    //              GetSymbolNameWide, WriteSymbolWide, OutputAsTypeWide)
    void _VtblGap5_5();

    // Slot 15
    [PreserveSig]
    int GetSymbolTypeName(
        uint Index,
        IntPtr Buffer,
        uint BufferSize,
        out uint NameSize);

    // Slot 16: GetSymbolTypeNameWide
    void _VtblGap6_1();

    // Slot 17: GetSymbolSize
    void _VtblGap7_1();

    // Slot 18: GetSymbolOffset
    void _VtblGap8_1();

    // Slot 19: GetSymbolRegister
    void _VtblGap9_1();

    // Slot 20
    [PreserveSig]
    int GetSymbolValueText(
        uint Index,
        IntPtr Buffer,
        uint BufferSize,
        out uint NameSize);
}