namespace MixDbg.Services;

public sealed class LogService : ILogService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "mixdbg.log");
    private static readonly object Lock = new();

    public void Write(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        lock (Lock)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
