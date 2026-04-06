using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

// GUID: 8c31e98c-983a-48a5-9016-6fe5d667a950

[ComImport, Guid("8c31e98c-983a-48a5-9016-6fe5d667a950")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDebugSymbols
{
    // Slots 0-2: GetSymbolOptions, AddSymbolOptions, RemoveSymbolOptions
    void _VtblGap1_3();

    // Slot 3
    [PreserveSig]
    int SetSymbolOptions(uint Options);

    // Slot 4
    [PreserveSig]
    int GetNameByOffset(
        ulong Offset,
        IntPtr NameBuffer,
        uint NameBufferSize,
        out uint NameSize,
        out ulong Displacement);

    // Slot 5: GetOffsetByName
    void _VtblGap2_1();

    // Slot 6: GetNearNameByOffset
    void _VtblGap3_1();

    // Slot 7
    [PreserveSig]
    int GetLineByOffset(
        ulong Offset,
        out uint Line,
        IntPtr FileBuffer,
        uint FileBufferSize,
        out uint FileSize,
        out ulong Displacement);

    // Slot 8
    [PreserveSig]
    int GetOffsetByLine(
        uint Line,
        [MarshalAs(UnmanagedType.LPStr)] string File,
        out ulong Offset);

    // Slots 9-11: GetNumberModules, GetModuleByIndex, GetModuleByModuleName
    void _VtblGap4_3();

    // Slot 12
    [PreserveSig]
    int GetModuleByOffset(
        ulong Offset,
        uint StartIndex,
        out uint Index,
        out ulong Base);

    // Slots 13-28
    void _VtblGap5_16();

    // Slot 29
    [PreserveSig]
    int SetScope(
        ulong InstructionOffset,
        IntPtr ScopeFrame,
        IntPtr ScopeContext,
        uint ScopeContextSize);

    // Slot 30: ResetScope
    void _VtblGap6_1();

    // Slot 31
    [PreserveSig]
    int GetScopeSymbolGroup(
        uint Flags,
        IntPtr Update,
        [MarshalAs(UnmanagedType.Interface)] out IDebugSymbolGroup2 Group);

    // Slots 32-37
    void _VtblGap7_6();

    // Slot 38
    [PreserveSig]
    int SetSymbolPath(
        [MarshalAs(UnmanagedType.LPStr)] string Path);

    // Slot 39
    [PreserveSig]
    int AppendSymbolPath(
        [MarshalAs(UnmanagedType.LPStr)] string Addition);

    // Slots 40-44: image/source path getters
    void _VtblGap9_5();

    // Slot 45
    [PreserveSig]
    int SetSourcePath(
        [MarshalAs(UnmanagedType.LPStr)] string Path);
}
