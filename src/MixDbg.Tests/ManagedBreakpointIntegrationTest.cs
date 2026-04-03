using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MixDbg.Tests;

/// <summary>
/// End-to-end integration test that spawns MixDbg.exe with --logpath,
/// sends DAP messages, launches WpfApp with --auto-test, and verifies
/// managed breakpoints fire with source info on multiple methods across
/// continue cycles.
/// </summary>
public sealed class ManagedBreakpointIntegrationTest : IAsyncLifetime
{
    // ── Tests ───────────────────────────────────────────

    [Fact]
    public async Task ManagedBreakpoint_WhenTwoMethodsBreakpointed_BothFireWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();
        await WhenSettingTwoManagedBreakpoints();
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit 1: OnAddClick — auto-test clicks Add at 3s (JITs), then at 7s (bp fires).
        await WhenWaitingForStoppedEvent(timeout: 20);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: OnMultiplyClick — auto-test clicks Multiply after gap (JITs), then again (bp fires).
        // Longer timeout: WpfApp timers pause during debug stops, extending real elapsed time.
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenStackTraceHasSource(hitIndex: 0, "MainWindow.xaml.cs");
        ThenBreakpointWasHit(hitIndex: 1);
        ThenStackTraceHasSource(hitIndex: 1, "MainWindow.xaml.cs");
        ThenNoLogErrors();
    }

    [Fact]
    public async Task ManagedBreakpoint_WhenSlowUserDelaysFirstClick_BothStillFire()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();
        await WhenSettingTwoManagedBreakpoints();
        await WhenLaunchingWithAutoTestSlow();
        await WhenSendingConfigurationDone();

        // Hit 1: OnAddClick — slow auto-test delays 15s before first click (JITs),
        // then 4s later (bp fires). Burns many CreateRuntime calls polling before JIT.
        // 14 stackTrace requests per stop mimics nvim-dap's real behavior.
        await WhenWaitingForStoppedEvent(timeout: 40);
        await WhenRequestingStackTraceForManyThreads(14);
        await WhenSendingContinue();

        // Hit 2: OnMultiplyClick — same gap as normal test.
        await WhenWaitingForStoppedEvent(timeout: 40);
        await WhenRequestingStackTraceForManyThreads(14);
        await WhenSendingContinue();

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenBreakpointWasHit(hitIndex: 1);
        ThenNoLogErrors();
    }

    #region Given

    private void GivenMixDbgAndWpfAppExist()
    {
        if (!File.Exists(_mixDbgPath))
            Assert.Fail($"MixDbg not found at {_mixDbgPath} — build it first");
        if (!File.Exists(_wpfAppPath))
            Assert.Fail($"WpfApp not found at {_wpfAppPath} — build it first");
    }

    #endregion

    #region When

    private async Task WhenStartingMixDbg()
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _mixDbgPath,
                Arguments = $"--logpath \"{_sessionLogPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        _process.Start();

        _readTask = Task.Run(async () =>
        {
            var stream = _process.StandardOutput.BaseStream;
            var buf = new byte[65536];
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    int read = await stream.ReadAsync(buf, _cts.Token);
                    if (read == 0) break;
                    var text = Encoding.UTF8.GetString(buf, 0, read);
                    _outputBuilder.Append(text);
                    ParseDapMessages(text);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        });
    }

    private async Task WhenSendingInitialize()
    {
        await SendDapRequest(1, "initialize", new { adapterID = "test", clientID = "test" });
        await WhenWaitingForResponse("initialize", timeout: 5);
    }

    private async Task WhenSettingTwoManagedBreakpoints()
    {
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _bpFile, name = "MainWindow.xaml.cs" },
            breakpoints = new[] { new { line = _addLine }, new { line = _multiplyLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);
    }

    private async Task WhenLaunchingWithAutoTest()
    {
        await SendDapRequest(3, "launch", new
        {
            program = _wpfAppPath.Replace("/", "\\"),
            cwd = Path.GetDirectoryName(_wpfAppPath)!.Replace("/", "\\"),
            args = new[] { "--auto-test" },
        });
        await WhenWaitingForResponse("launch", timeout: 10);
    }

    private async Task WhenLaunchingWithAutoTestSlow()
    {
        await SendDapRequest(3, "launch", new
        {
            program = _wpfAppPath.Replace("/", "\\"),
            cwd = Path.GetDirectoryName(_wpfAppPath)!.Replace("/", "\\"),
            args = new[] { "--auto-test-slow" },
        });
        await WhenWaitingForResponse("launch", timeout: 10);
    }

    private async Task WhenSendingConfigurationDone()
    {
        await SendDapRequest(4, "configurationDone", new { });
        await WhenWaitingForResponse("configurationDone", timeout: 5);
    }

    private async Task WhenRequestingStackTraceForMultipleThreads()
    {
        // Mimic nvim-dap: request threads, then stackTrace for each thread.
        // nvim-dap typically requests for 3-5 threads in a WPF app.
        _nextSeq++;
        await SendDapRequest(_nextSeq, "threads", new { });
        await WhenWaitingForResponse("threads", timeout: 10);

        // First thread (breakpoint thread) — record its source.
        _nextSeq++;
        await SendDapRequest(_nextSeq, "stackTrace", new { threadId = 0, startFrame = 0, levels = 5 });
        await WhenWaitingForStackTraceResponse(timeout: 10);

        // Additional threads — send, wait, and CONSUME responses to avoid leftovers
        // that would be picked up by the next WhenWaitingForStackTraceResponse.
        for (int tid = 1; tid < 3; tid++)
        {
            _nextSeq++;
            await SendDapRequest(_nextSeq, "stackTrace", new { threadId = tid, startFrame = 0, levels = 5 });
            await WhenWaitingAndConsumingStackTraceResponse(timeout: 10);
        }
    }

    private async Task WhenRequestingStackTraceForManyThreads(int threadCount)
    {
        // Mimic nvim-dap requesting stackTrace for all threads. In a WPF app
        // nvim-dap typically sends 10-14 stackTrace requests. Without the
        // stack trace cache, each call triggers GetStackTrace + symbol lookups
        // (~100 COM calls), degrading the DAC and breaking CreateRuntime.
        _nextSeq++;
        await SendDapRequest(_nextSeq, "threads", new { });
        await WhenWaitingForResponse("threads", timeout: 10);

        _nextSeq++;
        await SendDapRequest(_nextSeq, "stackTrace", new { threadId = 0, startFrame = 0, levels = 5 });
        await WhenWaitingForStackTraceResponse(timeout: 10);

        for (int tid = 1; tid < threadCount; tid++)
        {
            _nextSeq++;
            await SendDapRequest(_nextSeq, "stackTrace", new { threadId = tid, startFrame = 0, levels = 5 });
            await WhenWaitingAndConsumingStackTraceResponse(timeout: 10);
        }
    }

    private async Task WhenSendingContinue()
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "continue", new { threadId = 0 });
    }

    private async Task WhenSendingDisconnect()
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "disconnect", new { terminateDebuggee = true });
    }

    private async Task WhenWaitingForSeconds(int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }

    private async Task WhenWaitingForResponse(string command, int timeout)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                if (_responses.Any(r =>
                    r["command"]?.GetValue<string>() == command))
                    return;
            }
            await Task.Delay(100);
        }
    }

    private async Task WhenWaitingForStoppedEvent(int timeout)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_events)
            {
                var stopped = _events.FirstOrDefault(e =>
                    e["event"]?.GetValue<string>() == "stopped");
                if (stopped != null)
                {
                    _stoppedReasons.Add(stopped["body"]?["reason"]?.GetValue<string>());
                    _events.Remove(stopped);
                    return;
                }
            }
            await Task.Delay(200);
        }
        _stoppedReasons.Add(null);
    }

    private async Task WhenWaitingForStackTraceResponse(int timeout)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                var resp = _responses.FirstOrDefault(r =>
                    r["command"]?.GetValue<string>() == "stackTrace");
                if (resp != null)
                {
                    var frames = resp["body"]?["stackFrames"]?.AsArray();
                    var firstFrame = frames?.FirstOrDefault()?.AsObject();
                    var sourcePath = firstFrame?["source"]?["path"]?.GetValue<string>();
                    _stackTraceSourcePaths.Add(sourcePath);
                    _responses.Remove(resp);
                    return;
                }
            }
            await Task.Delay(200);
        }
        _stackTraceSourcePaths.Add(null);
    }

    private async Task WhenWaitingAndConsumingStackTraceResponse(int timeout)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                var resp = _responses.FirstOrDefault(r =>
                    r["command"]?.GetValue<string>() == "stackTrace");
                if (resp != null)
                {
                    _responses.Remove(resp);
                    return;
                }
            }
            await Task.Delay(200);
        }
    }

    private async Task WhenWaitingForExit()
    {
        _cts.Cancel();
        if (_process != null)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
        }
        if (_readTask != null)
            await _readTask;
    }

    #endregion

    #region Then

    private void ThenBreakpointWasHit(int hitIndex)
    {
        Assert.True(hitIndex < _stoppedReasons.Count,
            $"Expected stopped event #{hitIndex} but only got {_stoppedReasons.Count}");
        Assert.Equal("breakpoint", _stoppedReasons[hitIndex]);
    }

    private void ThenStackTraceHasSource(int hitIndex, string expectedFileName)
    {
        Assert.True(hitIndex < _stackTraceSourcePaths.Count,
            $"Expected stack trace #{hitIndex} but only got {_stackTraceSourcePaths.Count}");
        Assert.NotNull(_stackTraceSourcePaths[hitIndex]);
        Assert.Contains(expectedFileName, _stackTraceSourcePaths[hitIndex]!);
    }

    private void ThenNoLogErrors()
    {
        if (!File.Exists(_sessionLogPath))
            return;
        var log = File.ReadAllText(_sessionLogPath);
        Assert.DoesNotContain("CreateDacInstance failed", log);
        Assert.DoesNotContain("Could not find matching DAC", log);
    }

    #endregion

    #region Misc

    // Paths relative to repo root — computed from test assembly location.
    private static readonly string _repoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string _mixDbgPath = Path.Combine(
        _repoRoot, "src", "MixDbg", "bin", "Debug", "net10.0", "win-x64", "MixDbg.exe");
    private static readonly string _wpfAppPath = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "bin", "x64", "Debug", "net10.0-windows", "WpfApp.exe");
    private static readonly string _bpFile = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "MainWindow.xaml.cs");
    private const int _addLine = 48;
    private const int _multiplyLine = 57;

    private readonly string _sessionLogPath = Path.Combine(
        Path.GetTempPath(), $"mixdbg-test-{Guid.NewGuid():N}.log");

    private Process? _process;
    private Task? _readTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _outputBuilder = new();
    private readonly List<JsonObject> _responses = new();
    private readonly List<JsonObject> _events = new();
    private readonly List<string?> _stoppedReasons = new();
    private readonly List<string?> _stackTraceSourcePaths = new();
    private int _nextSeq = 10;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        _cts.Dispose();
        // Don't delete — keep for post-failure inspection.
        // try { File.Delete(_sessionLogPath); } catch { }
    }

    private async Task SendDapRequest(int seq, string command, object args)
    {
        var body = JsonSerializer.Serialize(new
        {
            seq,
            type = "request",
            command,
            arguments = args,
        });
        var msg = $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        await _process!.StandardInput.WriteAsync(msg);
        await _process.StandardInput.FlushAsync();
    }

    private readonly StringBuilder _partialBuffer = new();

    private void ParseDapMessages(string text)
    {
        _partialBuffer.Append(text);
        var buf = _partialBuffer.ToString();

        while (true)
        {
            var headerEnd = buf.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) break;

            var header = buf[..headerEnd];
            var contentStart = headerEnd + 4;

            if (!header.Contains("Content-Length:")) break;
            var lenStr = header.Split("Content-Length:")[1].Trim();
            if (!int.TryParse(lenStr, out var len)) break;
            if (buf.Length < contentStart + len) break;

            var json = buf.Substring(contentStart, len);
            buf = buf[(contentStart + len)..];

            try
            {
                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj == null) continue;

                var msgType = obj["type"]?.GetValue<string>();
                if (msgType == "response")
                    lock (_responses) { _responses.Add(obj); }
                else if (msgType == "event")
                    lock (_events) { _events.Add(obj); }
            }
            catch { }
        }

        _partialBuffer.Clear();
        _partialBuffer.Append(buf);
    }

    #endregion
}
