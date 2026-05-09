using MixDbg.Engine.DbgEng.Constants;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Persistent <see cref="IDebugOutputCallbacks"/> that filters dbgeng output
/// to the <c>DEBUG_OUTPUT_DEBUGGEE</c> mask (text from
/// <c>OutputDebugString</c> / <c>Trace.WriteLine</c> / <c>Debug.WriteLine</c>)
/// and forwards it to the supplied delegate. Engine chatter (symbol loads,
/// command results, etc.) under other masks is dropped. Keeping the filter
/// here means callers outside the EngineWrappers assembly don't need to
/// reference dbgeng's mask constants.
/// </summary>
internal sealed class DebuggeeOutputForwarder(Action<string> _onDebuggeeText) : IDebugOutputCallbacks
{
    /// <inheritdoc />
    public int Output(uint Mask, string Text)
    {
        if ((Mask & DebugOutput.Debuggee) != 0 && !string.IsNullOrEmpty(Text))
            _onDebuggeeText(Text);
        return 0; // S_OK
    }
}
