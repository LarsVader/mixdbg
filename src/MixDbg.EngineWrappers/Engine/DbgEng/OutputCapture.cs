using System.Text;

namespace MixDbg.Engine.DbgEng;

/// <summary>
/// Captures text output from dbgeng commands by implementing
/// <see cref="IDebugOutputCallbacks"/>. Set as the client's output
/// callback before executing a command, then read <see cref="Text"/>
/// afterward.
/// </summary>
internal sealed class OutputCapture : IDebugOutputCallbacks
{
    private readonly StringBuilder _sb = new();

    /// <summary>The accumulated output text.</summary>
    public string Text => _sb.ToString();

    /// <summary>Clears the captured output.</summary>
    public void Clear() => _sb.Clear();

    /// <inheritdoc />
    public int Output(uint Mask, string Text)
    {
        _ = _sb.Append(Text);
        return 0; // S_OK
    }
}