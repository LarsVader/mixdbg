using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MixDbg.Tests;

/// <summary>
/// Protocol-level startup integration tests that exercise the DAP handshake
/// itself, independent of breakpoints or stepping.
///
/// Regression target: a previous build emitted the <c>initialized</c> event
/// from inside the <c>initialize</c> handler, so the event reached the wire
/// before the response. nvim-dap's session.lua handles that event by calling
/// <c>set_breakpoints</c>; with zero configured breakpoints it runs the
/// <c>on_done</c> callback synchronously. <c>on_done</c> reads
/// <c>self.capabilities.supportsConfigurationDoneRequest</c> to decide whether
/// to send <c>configurationDone</c> — but capabilities are only populated by
/// the <c>initialize</c> RESPONSE, which had not arrived yet. The check
/// silently failed, <c>configurationDone</c> never reached the adapter, and
/// the engine sat in <c>ProcessCommandsUntilResume</c> forever.
///
/// Fix: <c>InitializeRequestHandlerService</c> now emits the event from
/// <see cref="MixDbg.Services.Interfaces.IDapAfterResponseAction.OnAfterResponse"/>,
/// which the dispatcher calls only after <c>SendResponse</c> returns.
/// </summary>
public sealed class DapStartupIntegrationTest : IAsyncLifetime
{
    [Fact]
    public async Task Initialize_ResponsePrecedesInitializedEvent()
    {
        GivenMixDbgExists();
        await WhenStartingMixDbg();

        await SendDapRequest(1, "initialize", new { adapterID = "test", clientID = "test" });
        await WhenWaitingForInitializeResponseAndInitializedEvent(timeout: 10);

        ThenInitializeResponseArrivedBeforeInitializedEvent();
    }

    [Fact]
    public async Task Launch_WhenNoBreakpointsSetBeforeLaunch_RunsToCompletionAndExits()
    {
        GivenMixDbgExists();
        GivenWpfAppExists();
        await WhenStartingMixDbg();

        await SendDapRequest(1, "initialize", new { adapterID = "test", clientID = "test" });
        await WhenWaitingForResponse("initialize", timeout: 5);

        // Skip setBreakpoints — this is the path that previously hung. The
        // PendingBreakpoints list at configurationDone time is empty.
        await SendDapRequest(2, "launch", new
        {
            program = _wpfAppPath.Replace("/", "\\"),
            cwd = Path.GetDirectoryName(_wpfAppPath)!.Replace("/", "\\"),
            args = new[] { "--auto-test" },
        });
        await WhenWaitingForResponse("launch", timeout: 15);

        await SendDapRequest(3, "configurationDone", new { });
        await WhenWaitingForResponse("configurationDone", timeout: 10);

        // --auto-test runs the click sequence and exits. Without the fix the
        // engine sits in ProcessCommandsUntilResume and never sends terminated.
        await WhenWaitingForEvent("terminated", timeout: 60);

        ThenTerminatedEventWasReceived();

        // Disconnect cleanly so MixDbg releases its log handle before we read it.
        await SendDapRequest(4, "disconnect", new { terminateDebuggee = true });
        await WhenWaitingForExit();

        ThenNoLogErrors();
    }

    #region Given

    private void GivenMixDbgExists()
    {
        if (!File.Exists(_mixDbgPath))
            Assert.Fail($"MixDbg not found at {_mixDbgPath} — build it first");
    }

    private void GivenWpfAppExists()
    {
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
        _ = _process.Start();

        _readTask = Task.Run(async () =>
        {
            Stream stream = _process.StandardOutput.BaseStream;
            byte[] buf = new byte[65536];
            // Decoder is stateful: stateless GetString would decode a partial
            // multi-byte UTF-8 sequence at the tail of any read into a
            // replacement char. Holding the decoder buffers the partial bytes
            // until the rest arrives on the next read.
            Decoder decoder = Encoding.UTF8.GetDecoder();
            // GetMaxCharCount is the principled upper bound for the worst
            // case (the decoder may flush a buffered partial sequence as a
            // replacement char + the current bytes).
            char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buf.Length)];
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    int read = await stream.ReadAsync(buf, _cts.Token);
                    if (read == 0) break;
                    int charCount = decoder.GetChars(buf, 0, read, chars, 0, flush: false);
                    if (charCount > 0)
                    {
                        ParseDapMessages(new string(chars, 0, charCount));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        });
    }

    private async Task WhenWaitingForResponse(string command, int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_messages)
            {
                if (_messages.Any(m =>
                    m["type"]?.GetValue<string>() == "response" &&
                    m["command"]?.GetValue<string>() == command))
                {
                    return;
                }
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        Assert.Fail($"timed out after {timeout}s waiting for response to '{command}'");
    }

    private async Task WhenWaitingForEvent(string evt, int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_messages)
            {
                if (_messages.Any(m =>
                    m["type"]?.GetValue<string>() == "event" &&
                    m["event"]?.GetValue<string>() == evt))
                {
                    return;
                }
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        Assert.Fail($"timed out after {timeout}s waiting for '{evt}' event");
    }

    private async Task WhenWaitingForExit()
    {
        if (_process == null)
        {
            return;
        }
        // Give MixDbg a moment to flush its log on disconnect.
        try
        {
            using CancellationTokenSource exitWait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            exitWait.CancelAfter(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(exitWait.Token);
        }
        catch (OperationCanceledException) { }
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
        }
        _cts.Cancel();
        if (_readTask != null)
        {
            await _readTask;
        }
    }

    /// <summary>Wait until both the initialize response AND the initialized event have arrived.</summary>
    private async Task WhenWaitingForInitializeResponseAndInitializedEvent(int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        bool gotResponse = false;
        bool gotEvent = false;
        while (DateTime.UtcNow < deadline)
        {
            lock (_messages)
            {
                gotResponse = _messages.Any(m =>
                    m["type"]?.GetValue<string>() == "response" &&
                    m["command"]?.GetValue<string>() == "initialize");
                gotEvent = _messages.Any(m =>
                    m["type"]?.GetValue<string>() == "event" &&
                    m["event"]?.GetValue<string>() == "initialized");
                if (gotResponse && gotEvent)
                {
                    return;
                }
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        Assert.Fail($"timed out after {timeout}s — gotInitializeResponse={gotResponse}, gotInitializedEvent={gotEvent}");
    }

    #endregion

    #region Then

    private void ThenInitializeResponseArrivedBeforeInitializedEvent()
    {
        lock (_messages)
        {
            int responseIdx = _messages.FindIndex(m =>
                m["type"]?.GetValue<string>() == "response" &&
                m["command"]?.GetValue<string>() == "initialize");
            int eventIdx = _messages.FindIndex(m =>
                m["type"]?.GetValue<string>() == "event" &&
                m["event"]?.GetValue<string>() == "initialized");

            Assert.True(responseIdx >= 0, "initialize response never arrived");
            Assert.True(eventIdx >= 0, "initialized event never arrived");
            Assert.True(responseIdx < eventIdx,
                $"DAP spec violation: initialized event (idx={eventIdx}) arrived before initialize response (idx={responseIdx}). " +
                "This breaks nvim-dap when the user has no breakpoints set — capabilities are empty " +
                "when on_done runs, so configurationDone is never sent and the engine hangs forever.");
        }
    }

    private void ThenTerminatedEventWasReceived()
    {
        lock (_messages)
        {
            Assert.Contains(_messages, m =>
                m["type"]?.GetValue<string>() == "event" &&
                m["event"]?.GetValue<string>() == "terminated");
        }
    }

    private void ThenNoLogErrors()
    {
        if (!File.Exists(_sessionLogPath))
            return;
        string log = File.ReadAllText(_sessionLogPath);
        Assert.DoesNotContain("CreateDacInstance failed", log);
        Assert.DoesNotContain("Could not find matching DAC", log);
    }

    #endregion

    #region Misc

    private static readonly string _repoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string _mixDbgPath = Path.Combine(
        _repoRoot, "src", "bin", "Debug", "net10.0", "win-x64", "MixDbg.exe");
    private static readonly string _wpfAppPath = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "bin", "x64", "Debug", "net10.0-windows", "WpfApp.exe");

    private readonly string _sessionLogPath = Path.Combine(
        Path.GetTempPath(), $"mixdbg-startup-test-{Guid.NewGuid():N}.log");

    private Process? _process;
    private Task? _readTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _messageArrived = new(0);

    /// <summary>All DAP messages in arrival order — the ordering check needs both
    /// responses and events in one stream to compare indexes.</summary>
    private readonly List<JsonObject> _messages = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
        }
        // Drain the reader before disposing the semaphore — otherwise the
        // reader can call _messageArrived.Release() after Dispose() (e.g. a
        // message arrives between CTS.Cancel and the process kill) and
        // throw ObjectDisposedException on the background task.
        if (_readTask != null)
        {
            try { await _readTask; } catch { /* reader catches its own errors */ }
        }
        _process?.Dispose();
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

            // Parse per-line so additional headers (e.g. a future Content-Type)
            // don't break length extraction. On malformed framing we abort the
            // parse loop — the buffer stays pinned, the test then times out
            // via Assert.Fail in the WhenWaitingFor* helpers with a clear
            // diagnostic. Better than silently advancing past the corruption.
            const string lengthHeader = "Content-Length:";
            string? lengthLine = Array.Find(
                header.Split("\r\n"),
                l => l.StartsWith(lengthHeader, StringComparison.OrdinalIgnoreCase));
            if (lengthLine is null) break;
            if (!int.TryParse(lengthLine[lengthHeader.Length..].Trim(), out int len)) break;
            if (buf.Length < contentStart + len) break;

            string json = buf.Substring(contentStart, len);
            buf = buf[(contentStart + len)..];

            try
            {
                JsonObject? obj = JsonNode.Parse(json)?.AsObject();
                if (obj == null) continue;

                lock (_messages) { _messages.Add(obj); }
                _ = _messageArrived.Release();
            }
            catch { }
        }

        _ = _partialBuffer.Clear();
        _ = _partialBuffer.Append(buf);
    }

    #endregion
}
