using System.Runtime.InteropServices;

namespace MixDbg.Engine.DbgEng;

[StructLayout(LayoutKind.Sequential)]
public struct DEBUG_STACK_FRAME
{
    public ulong InstructionOffset;
    public ulong ReturnOffset;
    public ulong FrameOffset;
    public ulong StackOffset;
    public ulong FuncTableEntry;
    public ulong Params0, Params1, Params2, Params3;
    public ulong Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
    public int Virtual;
    public uint FrameNumber;
}
