using System.Runtime.CompilerServices;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless logging service. All mutable state lives in
/// <see cref="LogStore"/>. Writes to both an in-memory entry list
/// and a log file on disk. Caller file name is captured automatically
/// via <see cref="CallerFilePathAttribute"/>.
/// </summary>
public interface ILoggingService
{
    /// <summary>Creates a new <see cref="LogStore"/> with the default log file path (~\mixdbg.log).</summary>
    LogStore CreateStore();

    /// <summary>Logs an informational message.</summary>
    void LogInfo(LogStore store, string message, [CallerFilePath] string sender = "");

    /// <summary>Logs a warning message.</summary>
    void LogWarning(LogStore store, string message, [CallerFilePath] string sender = "");

    /// <summary>Logs an error message.</summary>
    void LogError(LogStore store, string message, [CallerFilePath] string sender = "");

    /// <summary>Returns a snapshot of all log entries.</summary>
    IReadOnlyList<LogEntry> GetEntries(LogStore store);

    /// <summary>Removes all entries from the store.</summary>
    void Clear(LogStore store);
}
