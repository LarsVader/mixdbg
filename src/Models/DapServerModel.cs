namespace MixDbg.Models;

/// <summary>
/// Mutable state for the DAP transport. Holds the stdin/stdout streams,
/// a write lock for thread-safe output, and an atomic sequence counter
/// for DAP message numbering.
/// </summary>
public sealed class DapServerModel
{
    internal Stream Input { get; }
    internal Stream Output { get; }
    internal Lock WriteLock { get; } = new();
    internal int Seq;

    public DapServerModel(Stream input, Stream output)
    {
        Input = input;
        Output = output;
    }
}
