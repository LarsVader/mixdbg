namespace MixDbg.Models;

/// <summary>
/// Mutable state for the DAP transport. Holds the stdin/stdout streams,
/// a write lock for thread-safe output, and an atomic sequence counter
/// for DAP message numbering.
/// </summary>
public sealed class DapServerModel(Stream input, Stream output)
{
    internal Stream Input { get; } = input;
    internal Stream Output { get; } = output;
    internal Lock WriteLock { get; } = new();
    internal int Seq;
}