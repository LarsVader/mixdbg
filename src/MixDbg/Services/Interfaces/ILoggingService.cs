using System.Runtime.CompilerServices;
using MixDbg.Models;

namespace MixDbg.Services;

public interface ILoggingService
{
    LogStore CreateStore();
    void LogInfo(LogStore store, string message, [CallerFilePath] string sender = "");
    void LogWarning(LogStore store, string message, [CallerFilePath] string sender = "");
    void LogError(LogStore store, string message, [CallerFilePath] string sender = "");
    IReadOnlyList<LogEntry> GetEntries(LogStore store);
    void Clear(LogStore store);
}
