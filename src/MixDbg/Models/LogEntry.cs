namespace MixDbg.Models;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Sender,
    string Message);
