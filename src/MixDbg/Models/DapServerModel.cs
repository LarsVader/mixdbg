namespace MixDbg.Models;

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
