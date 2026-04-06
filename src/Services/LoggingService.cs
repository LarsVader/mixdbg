using System.Runtime.CompilerServices;

using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

internal sealed class LoggingService : ILoggingService
{
    private static readonly string DefaultLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "mixdbg.log");

    public LogStore CreateStore()
        => new(DefaultLogPath);

    public void LogInfo(
        LogStore store,
        string message,
        [CallerFilePath] string sender = "")
        => AddEntry(store, LogLevel.Info, message, sender);

    public void LogWarning(
        LogStore store,
        string message,
        [CallerFilePath] string sender = "")
        => AddEntry(store, LogLevel.Warning, message, sender);

    public void LogError(
        LogStore store,
        string message,
        [CallerFilePath] string sender = "")
        => AddEntry(store, LogLevel.Error, message, sender);

    public IReadOnlyList<LogEntry> GetEntries(LogStore store)
    {
        lock (store.Lock)
        {
            return [.. store.Entries];
        }
    }

    public void Clear(LogStore store)
    {
        lock (store.Lock)
        {
            store.Entries.Clear();
        }
    }

    private static void AddEntry(
        LogStore store,
        LogLevel level,
        string message,
        string callerFilePath)
    {
        string senderName = ExtractSender(callerFilePath);

        LogEntry entry = new(DateTime.Now, level, senderName, message);

        lock (store.Lock)
        {
            store.Entries.Add(entry);
            string line = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] [{entry.Sender}] {entry.Message}";
            File.AppendAllText(store.FilePath, line + Environment.NewLine);
        }
    }

    internal static string ExtractSender(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return "Unknown";

        int lastSep = filePath.LastIndexOfAny(['/', '\\']);
        string fileName = lastSep >= 0
            ? filePath[(lastSep + 1)..]
            : filePath;

        int dot = fileName.LastIndexOf('.');
        return dot > 0
            ? fileName[..dot]
            : fileName;
    }
}