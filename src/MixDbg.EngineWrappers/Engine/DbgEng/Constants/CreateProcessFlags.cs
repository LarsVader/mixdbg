namespace MixDbg.Engine.DbgEng.Constants;

internal static class CreateProcessFlags
{
    public const uint DebugProcess = 0x00000001;
    public const uint DebugOnlyThisProcess = 0x00000002;
    public const uint CreateNewConsole = 0x00000010;
}