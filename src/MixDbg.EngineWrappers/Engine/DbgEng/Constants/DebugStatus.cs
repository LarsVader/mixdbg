namespace MixDbg.Engine.DbgEng;

internal static class DebugStatus
{
    public const uint NoChange = 0;
    public const uint Go = 1;
    public const uint GoHandled = 2;
    public const uint GoNotHandled = 3;
    public const uint StepOver = 4;
    public const uint StepInto = 5;
    public const uint Break = 6;
    public const uint NoDebuggee = 7;
    public const uint StepBranch = 8;
}