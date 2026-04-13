namespace MixDbg.Models;

/// <summary>
/// Mutable state for the logging service. Holds an in-memory list of
/// log entries, a lock for thread safety, the file path for persistent
/// log output (~\mixdbg.log), and a buffered writer kept open for the
/// session to avoid repeated open/close overhead.
/// </summary>
public sealed class LogStore(string filePath) : IDisposable
{
    internal List<LogEntry> Entries { get; } = [];

    internal Lock Lock { get; } = new();

    internal string FilePath { get; } = filePath;

    /// <summary>
    /// Minimum severity to log. Messages below this level are discarded.
    /// Defaults to <see cref="LogLevel.Info"/>.
    /// </summary>
    internal LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>
    /// Persistent buffered writer for the log file. Opened lazily on the
    /// first write, stays open until dispose. Replaces the per-call
    /// <c>File.AppendAllText</c> that was the main performance bottleneck.
    /// </summary>
    internal StreamWriter? Writer { get; set; }

    public void Dispose()
    {
        lock (Lock)
        {
            Writer?.Dispose();
            Writer = null;
        }
    }
}
