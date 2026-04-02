namespace MixDbg.Models;

/// <summary>
/// Mutable state for the logging service. Holds an in-memory list of
/// log entries, a lock for thread safety, and the file path for
/// persistent log output (~\mixdbg.log).
/// </summary>
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
