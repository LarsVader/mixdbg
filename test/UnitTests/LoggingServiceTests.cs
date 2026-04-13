using MixDbg.Models;
using MixDbg.Services;

namespace MixDbg.Tests;

public sealed class LoggingServiceTests : IDisposable
{
    [Fact]
    public void LogInfo_WhenCalled_AddsInfoEntry()
    {
        WhenLoggingInfo("test message");

        ThenEntryCountIs(1);
        ThenLastEntryLevelIs(LogLevel.Info);
        ThenLastEntryMessageIs("test message");
    }

    [Fact]
    public void LogWarning_WhenCalled_AddsWarningEntry()
    {
        WhenLoggingWarning("warning message");

        ThenEntryCountIs(1);
        ThenLastEntryLevelIs(LogLevel.Warning);
    }

    [Fact]
    public void LogError_WhenCalled_AddsErrorEntry()
    {
        WhenLoggingError("error message");

        ThenEntryCountIs(1);
        ThenLastEntryLevelIs(LogLevel.Error);
    }

    [Fact]
    public void GetEntries_WhenMultipleEntries_ReturnsAll()
    {
        WhenLoggingInfo("first");
        WhenLoggingWarning("second");
        WhenLoggingError("third");

        WhenGettingEntries();

        ThenEntryCountIs(3);
    }

    [Fact]
    public void GetEntries_WhenCalled_ReturnsSnapshot()
    {
        WhenLoggingInfo("before snapshot");
        WhenGettingEntries();

        WhenLoggingInfo("after snapshot");

        ThenSnapshotCountIs(1);
    }

    [Fact]
    public void Clear_WhenEntriesExist_RemovesAll()
    {
        WhenLoggingInfo("entry 1");
        WhenLoggingInfo("entry 2");
        WhenClearing();

        ThenEntryCountIs(0);
    }

    [Fact]
    public void LogInfo_WhenCalled_ExtractsSenderFromCallerPath()
    {
        WhenLoggingInfo("test");

        WhenGettingEntries();

        ThenLastEntrySenderIs("LoggingServiceTests");
    }

    [Fact]
    public void ExtractSender_WhenFullWindowsPath_ReturnsFileNameWithoutExtension()
    {
        GivenCallerPath(@"C:\src\MyService.cs");

        WhenExtractingSender();

        ThenSenderIs("MyService");
    }

    [Fact]
    public void ExtractSender_WhenFullUnixPath_ReturnsFileNameWithoutExtension()
    {
        GivenCallerPath("/home/user/src/MyService.cs");

        WhenExtractingSender();

        ThenSenderIs("MyService");
    }

    [Fact]
    public void ExtractSender_WhenEmpty_ReturnsUnknown()
    {
        GivenCallerPath("");

        WhenExtractingSender();

        ThenSenderIs("Unknown");
    }

    [Fact]
    public void ExtractSender_WhenNull_ReturnsUnknown()
    {
        GivenCallerPath(null!);

        WhenExtractingSender();

        ThenSenderIs("Unknown");
    }

    [Fact]
    public void ExtractSender_WhenNoExtension_ReturnsFullFileName()
    {
        GivenCallerPath("/src/Makefile");

        WhenExtractingSender();

        ThenSenderIs("Makefile");
    }

    [Fact]
    public void LogInfo_WhenCalled_WritesToFile()
    {
        WhenLoggingInfo("file write test");

        ThenLogFileExists();
        ThenLogFileContains("file write test");
    }

    [Fact]
    public void LogVerbose_WhenMinLevelIsInfo_DoesNotAddEntry()
    {
        GivenMinLevel(LogLevel.Info);
        WhenLoggingVerbose("verbose message");

        ThenEntryCountIs(0);
    }

    [Fact]
    public void LogVerbose_WhenMinLevelIsVerbose_AddsEntry()
    {
        GivenMinLevel(LogLevel.Verbose);
        WhenLoggingVerbose("verbose message");

        ThenEntryCountIs(1);
        ThenLastEntryLevelIs(LogLevel.Verbose);
        ThenLastEntryMessageIs("verbose message");
    }

    [Fact]
    public void LogInfo_WhenMinLevelIsWarning_DoesNotAddEntry()
    {
        GivenMinLevel(LogLevel.Warning);
        WhenLoggingInfo("info message");

        ThenEntryCountIs(0);
    }

    [Fact]
    public void CreateStore_WhenCalled_ReturnsStoreWithDefaultPath()
    {
        WhenCreatingDefaultStore();

        ThenDefaultStorePathContains("mixdbg.log");
    }

    #region Given

    private void GivenCallerPath(string path) => _callerPath = path;

    private void GivenMinLevel(LogLevel level) => _logStore.MinLevel = level;

    #endregion

    #region When

    private void WhenLoggingVerbose(string message) => _testee.LogVerbose(_logStore, message);

    private void WhenLoggingInfo(string message) => _testee.LogInfo(_logStore, message);

    private void WhenLoggingWarning(string message) => _testee.LogWarning(_logStore, message);

    private void WhenLoggingError(string message) => _testee.LogError(_logStore, message);

    private void WhenGettingEntries() => _entries = _testee.GetEntries(_logStore);

    private void WhenClearing() => _testee.Clear(_logStore);

    private void WhenExtractingSender() => _extractedSender = LoggingService.ExtractSender(_callerPath);

    private void WhenCreatingDefaultStore() => _defaultStore = _testee.CreateStore();

    #endregion

    #region Then

    private void ThenEntryCountIs(int expected)
    {
        IReadOnlyList<LogEntry> entries = _testee.GetEntries(_logStore);
        Assert.Equal(expected, entries.Count);
    }

    private void ThenSnapshotCountIs(int expected) => Assert.Equal(expected, _entries!.Count);

    private void ThenLastEntryLevelIs(LogLevel expected)
    {
        IReadOnlyList<LogEntry> entries = _testee.GetEntries(_logStore);
        Assert.Equal(expected, entries[^1].Level);
    }

    private void ThenLastEntryMessageIs(string expected)
    {
        IReadOnlyList<LogEntry> entries = _testee.GetEntries(_logStore);
        Assert.Equal(expected, entries[^1].Message);
    }

    private void ThenLastEntrySenderIs(string expected) => Assert.Equal(expected, _entries![^1].Sender);

    private void ThenSenderIs(string expected) => Assert.Equal(expected, _extractedSender);

    private void ThenLogFileExists() => Assert.True(File.Exists(_logFilePath));

    private void ThenLogFileContains(string expected)
    {
        // Use FileShare.ReadWrite since the LogStore keeps its writer open.
        using FileStream fs = new(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(fs);
        string content = reader.ReadToEnd();
        Assert.Contains(expected, content);
    }

    private void ThenDefaultStorePathContains(string expected) => Assert.Contains(expected, _defaultStore!.FilePath);

    #endregion

    #region Misc

    private readonly LoggingService _testee = new();
    private readonly string _logFilePath = Path.Combine(Path.GetTempPath(), $"mixdbg_test_{Guid.NewGuid()}.log");
    private readonly LogStore _logStore;
    private IReadOnlyList<LogEntry>? _entries;
    private string _callerPath = "";
    private string? _extractedSender;
    private LogStore? _defaultStore;

    public LoggingServiceTests() => _logStore = new LogStore(_logFilePath);

    public void Dispose()
    {
        _logStore.Dispose();
        try { File.Delete(_logFilePath); } catch { }
    }

    #endregion
}