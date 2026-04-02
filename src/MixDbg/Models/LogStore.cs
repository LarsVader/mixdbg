namespace MixDbg.Models;

public sealed class LogStore
{
    internal List<LogEntry> Entries { get; } = [];

    internal Lock Lock { get; } = new();

    internal string FilePath { get; }

    public LogStore(string filePath)
    {
        FilePath = filePath;
    }
}
