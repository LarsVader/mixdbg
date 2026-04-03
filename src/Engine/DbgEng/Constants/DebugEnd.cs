namespace MixDbg.Engine.DbgEng;

public static class DebugEnd
{
    public const uint Passive = 0;
    public const uint ActiveTerminate = 1;
    public const uint ActiveDetach = 2;
    public const uint EndReentrant = 3;
    public const uint EndDisconnect = 4;
}
