using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MixDbg.Tests;

/// <summary>
/// End-to-end integration test for attach-to-running-process. Spawns
/// <c>WpfApp.exe --auto-test-attach</c> directly (not via mixdbg launch),
/// captures its PID, then spawns <c>MixDbg.exe</c> and drives the DAP
/// <c>attach</c> request. The WpfApp's auto-test-attach mode idles 30s
/// before the first click — that pre-roll is the window during which
/// MixDbg attaches via diagnostic IPC, sets breakpoints, and signals
/// configurationDone.
///
/// In the current implementation the IL rewriter (<c>GetReJITParameters</c>
/// in the profiler) is a stub — managed BPs in attach mode rely on
/// JIT-time HW BPs (for newly-JIT'd methods) and the DAC fallback (for
/// already-JIT'd methods). Once the IL rewriter is implemented, this
/// test will additionally validate the unlimited-BPs behavior of M4V3
/// in attach mode.
/// </summary>
public sealed class AttachIntegrationTest : IAsyncLifetime
{
    [Fact]
    public async Task Attach_WhenManagedBreakpointSetAfterAttach_FiresWhenAutoTestClicks()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingWpfAppDirectly();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set a C# BP on OnAddClick BEFORE issuing attach — same as launch flow:
        // the BP is held in PendingManagedBreakpoints until the engine binds it.
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _bpFile, name = "MainWindow.xaml.cs" },
            breakpoints = new[] { new { line = _addLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        await WhenAttaching(_wpfAppPid);
        await WhenSendingConfigurationDone();

        // The auto-test-attach mode idles 30s, then clicks Add. Allow a generous
        // total window for the attach round-trip + JIT + BP install.
        await WhenWaitingForStoppedEvent(timeout: 60);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenEagerHardwareBpWasInstalled();
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

    private async Task WhenStartingWpfAppDirectly()
    {
        _wpfAppProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _wpfAppPath,
                Arguments = "--auto-test-attach",
                WorkingDirectory = Path.GetDirectoryName(_wpfAppPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        _ = _wpfAppProcess.Start();
        _wpfAppPid = _wpfAppProcess.Id;

        // Wait for the .NET diagnostic IPC pipe to appear instead of guessing
        // a 2 s sleep. The pipe is created by the runtime once the diagnostic
        // server is up, which is the same prerequisite our AttachProfiler IPC
        // depends on. Polling here avoids the race that would otherwise time
        // out our IPC connect on slow machines.
        string pipePath = $@"\\.\pipe\dotnet-diagnostic-{_wpfAppPid}";
        long deadlineMs = Environment.TickCount64 + 15_000;
        while (!File.Exists(pipePath) && Environment.TickCount64 < deadlineMs)
            await Task.Delay(100);

        Assert.True(File.Exists(pipePath),
            $"Diagnostic IPC pipe '{pipePath}' did not appear within 15 s — WpfApp's runtime never came up");
    }

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
        _ = _process.Start();

        _readTask = Task.Run(async () =>
        {
            Stream stream = _process.StandardOutput.BaseStream;
            byte[] buf = new byte[65536];
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    int read = await stream.ReadAsync(buf, _cts.Token);
                    if (read == 0) break;
                    string text = Encoding.UTF8.GetString(buf, 0, read);
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

    private async Task WhenAttaching(int pid)
    {
        await SendDapRequest(3, "attach", new { pid });
        await WhenWaitingForResponse("attach", timeout: 30);
    }

    private async Task WhenSendingConfigurationDone()
    {
        await SendDapRequest(4, "configurationDone", new { });
        await WhenWaitingForResponse("configurationDone", timeout: 5);
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

    private async Task WhenWaitingForResponse(string command, int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                if (_responses.Any(r => r["command"]?.GetValue<string>() == command))
                    return;
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
    }

    private async Task WhenWaitingForStoppedEvent(int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_events)
            {
                JsonObject? stopped = _events.FirstOrDefault(e =>
                    e["event"]?.GetValue<string>() == "stopped");
                if (stopped != null)
                {
                    _stoppedReasons.Add(stopped["body"]?["reason"]?.GetValue<string>());
                    _ = _events.Remove(stopped);
                    return;
                }
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        _stoppedReasons.Add(null);
    }

    private async Task WhenWaitingForExit()
    {
        _cts.Cancel();
        if (_process != null)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
        }
        if (_wpfAppProcess != null && !_wpfAppProcess.HasExited)
        {
            try { _wpfAppProcess.Kill(entireProcessTree: true); } catch { }
            await _wpfAppProcess.WaitForExitAsync();
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

    private void ThenNoLogErrors()
    {
        if (!File.Exists(_sessionLogPath))
            return;
        string log = File.ReadAllText(_sessionLogPath);
        Assert.DoesNotContain("CreateDacInstance failed", log);
        Assert.DoesNotContain("Could not find matching DAC", log);
        // Profiler-attach-specific failures we want to surface clearly.
        Assert.DoesNotContain("Profiler attach failed", log);
    }

    /// <summary>
    /// Asserts that the attach-specific eager-install path actually fired
    /// during this session. Without this, the test would pass even if
    /// attach-mode was completely broken — a launch-mode-style ENTER hook
    /// install or a DAC fallback would still satisfy ThenBreakpointWasHit.
    /// </summary>
    private void ThenEagerHardwareBpWasInstalled()
    {
        Assert.True(File.Exists(_sessionLogPath),
            $"Session log not found at {_sessionLogPath}");
        string log = File.ReadAllText(_sessionLogPath);
        Assert.Contains("ATTACH-EAGER: installed hw BP", log);
    }

    #endregion

    #region Misc

    private static readonly string _repoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string _mixDbgPath = Path.Combine(
        _repoRoot, "src", "bin", "Debug", "net10.0", "win-x64", "MixDbg.exe");
    private static readonly string _wpfAppPath = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "bin", "x64", "Debug", "net10.0-windows", "WpfApp.exe");
    private static readonly string _bpFile = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "MainWindow.xaml.cs");
    private const int _addLine = 65;

    private readonly string _sessionLogPath = Path.Combine(
        Path.GetTempPath(), $"mixdbg-attach-test-{Guid.NewGuid():N}.log");

    private Process? _process;
    private Process? _wpfAppProcess;
    private int _wpfAppPid;
    private Task? _readTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _messageArrived = new(0);
    private readonly List<JsonObject> _responses = [];
    private readonly List<JsonObject> _events = [];
    private readonly List<string?> _stoppedReasons = [];
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
        if (_wpfAppProcess != null && !_wpfAppProcess.HasExited)
        {
            try { _wpfAppProcess.Kill(entireProcessTree: true); } catch { }
            await _wpfAppProcess.WaitForExitAsync();
        }
        _process?.Dispose();
        _wpfAppProcess?.Dispose();
        _cts.Dispose();
        _messageArrived.Dispose();
    }

    private async Task SendDapRequest(int seq, string command, object args)
    {
        string body = JsonSerializer.Serialize(new
        {
            seq,
            type = "request",
            command,
            arguments = args,
        });
        string msg = $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        await _process!.StandardInput.WriteAsync(msg);
        await _process.StandardInput.FlushAsync();
    }

    private readonly StringBuilder _partialBuffer = new();

    private void ParseDapMessages(string text)
    {
        _ = _partialBuffer.Append(text);
        string buf = _partialBuffer.ToString();

        while (true)
        {
            int headerEnd = buf.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) break;

            string header = buf[..headerEnd];
            int contentStart = headerEnd + 4;

            if (!header.Contains("Content-Length:")) break;
            string lenStr = header.Split("Content-Length:")[1].Trim();
            if (!int.TryParse(lenStr, out int len)) break;
            if (buf.Length < contentStart + len) break;

            string json = buf.Substring(contentStart, len);
            buf = buf[(contentStart + len)..];

            try
            {
                JsonObject? obj = JsonNode.Parse(json)?.AsObject();
                if (obj == null) continue;

                string? msgType = obj["type"]?.GetValue<string>();
                if (msgType == "response")
                {
                    lock (_responses) { _responses.Add(obj); }
                    _ = _messageArrived.Release();
                }
                else if (msgType == "event")
                {
                    lock (_events) { _events.Add(obj); }
                    _ = _messageArrived.Release();
                }
            }
            catch { }
        }

        _ = _partialBuffer.Clear();
        _ = _partialBuffer.Append(buf);
    }

    #endregion
}
