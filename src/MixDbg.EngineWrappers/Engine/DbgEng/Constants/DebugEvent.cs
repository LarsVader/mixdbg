namespace MixDbg.Engine.DbgEng.Constants;

[Flags]
internal enum DebugEvent : uint
{
    Breakpoint = 0x00000001,
    Exception = 0x00000002,
    CreateThread = 0x00000004,
    ExitThread = 0x00000008,
    CreateProcess = 0x00000010,
    ExitProcess = 0x00000020,
    LoadModule = 0x00000040,
    UnloadModule = 0x00000080,
    SystemError = 0x00000100,
    SessionStatus = 0x00000200,
    ChangeDebuggeeState = 0x00000400,
    ChangeEngineState = 0x00000800,
    ChangeSymbolState = 0x00001000,
}