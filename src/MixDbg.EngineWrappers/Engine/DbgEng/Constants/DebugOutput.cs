namespace MixDbg.Engine.DbgEng.Constants;

/// <summary>
/// Mask values passed to <see cref="IDebugOutputCallbacks.Output"/>. Identifies
/// the source/category of the text emitted by the engine.
/// </summary>
internal static class DebugOutput
{
    public const uint Normal = 0x00000001;
    public const uint Error = 0x00000002;
    public const uint Warning = 0x00000004;
    public const uint Verbose = 0x00000008;
    public const uint Prompt = 0x00000010;
    public const uint PromptRegisters = 0x00000020;
    public const uint ExtensionWarning = 0x00000040;

    /// <summary>Output from the debuggee, e.g. <c>OutputDebugString</c> /
    /// <c>Trace.WriteLine</c> / <c>Debug.WriteLine</c>.</summary>
    public const uint Debuggee = 0x00000080;

    public const uint DebuggeePrompt = 0x00000100;
    public const uint Symbols = 0x00000200;
}
