namespace MixDbg.Models;

/// <summary>
/// Severity level for a log entry.
/// </summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Immutable log entry with timestamp, severity, auto-detected caller
/// file name, and message text.
/// </summary>
public sealed record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Sender,
    string Message);