using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MixDbg.Tests;

/// <summary>
/// End-to-end integration tests for stepping (next/stepIn/stepOut) across
/// native, managed, and cross-boundary code. Spawns MixDbg.exe, sends DAP
/// messages, launches WpfApp with --auto-test, sets a breakpoint, then
/// exercises step operations and verifies stopped events with correct source info.
/// </summary>
public sealed class SteppingIntegrationTest : IAsyncLifetime
{
    // ── Tests ───────────────────────────────────────────

    [Fact(Skip = "we have complexer tests now")]
    public async Task ManagedStepOver_WhenAtCSharpLine_AdvancesToNextLine()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set C# BP at line 65 (if TryGetInputs...)
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit the breakpoint.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");

        // Step over — should advance to next C# line.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "step");
        ThenStackTraceHasSource(1, "MainWindow.xaml.cs");
        // Should be on a different line than the BP line.
        ThenStackTraceLineIsNot(1, _addLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task ManagedStepInto_WhenAtCallSite_EntersCalledMethod()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 65 (reliable method-entry BP), then step over to reach
        // the call site at line 67, then step into the call.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit the breakpoint at line 65. Extended timeout for the profiler hook path.
        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Step over twice: 65 → 66 → 67 (the ManagedCalculator.Add call).
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        ThenStoppedWithReason(1, "step");
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        ThenStoppedWithReason(2, "step");

        // Step into — should enter ManagedCalculator.Add in C++/CLI, not advance
        // to line 68 (which would be step-over behavior).
        await WhenSendingStepIn();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(3, "step");
        // Must NOT land on line 68 of MainWindow.xaml.cs (that's step-over, not step-into).
        // Should land in ManagedCalculator.h (C++/CLI) or Calculator.cpp (native).
        ThenStackTraceHasSourceOneOf(0, "ManagedCalculator.h", "Calculator.cpp");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task ManagedStepInto_WhenAtCliWrapperCallSite_EntersNativeCode()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 65, step over ×2 to line 67, step into C++/CLI,
        // then step into again to enter native Calculator::Add.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit breakpoint at line 65.
        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Step over twice: 65 → 66 → 67.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);

        // Step into C++/CLI: 67 → ManagedCalculator.h:14.
        await WhenSendingStepIn();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(3, "step");
        ThenStackTraceHasSourceOneOf(0, "ManagedCalculator.h", "Calculator.cpp");

        // Step into native: ManagedCalculator.h:14 → Calculator.cpp:7 (first statement).
        await WhenSendingStepIn();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(4, "step");
        ThenStackTraceHasSource(1, "Calculator.cpp");
        ThenStackTraceStoppedAtLine(1, _nativeAddLine); // line 7: return a + b;

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task ManagedStepOver_WhenAtLastCliLine_StepsOutToCSharpLine68()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at C++/CLI ManagedCalculator.h line 14 (return NativeLib::Calculator::Add(a, b);).
        await WhenSettingBreakpoint(_cliWrapperBpFile, "ManagedCalculator.h", _cliWrapperAddLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit the C++/CLI breakpoint.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "ManagedCalculator.h");

        // Step over at the last line of the CLI wrapper — no next line,
        // so should behave like step-out and land in C# line 68.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "step");
        ThenStackTraceHasSource(1, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(1, 68);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task ManagedStepOut_WhenInCliWrapper_ReturnsToCSharpCallSite()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at C++/CLI ManagedCalculator.h line 14 (return NativeLib::Calculator::Add(a, b);).
        await WhenSettingBreakpoint(_cliWrapperBpFile, "ManagedCalculator.h", _cliWrapperAddLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit the C++/CLI breakpoint.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "ManagedCalculator.h");

        // Step out — should return to C# MainWindow.xaml.cs line 68
        // (the line after the ManagedCalculator.Add call at line 67).
        await WhenSendingStepOut();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "step");
        ThenStackTraceHasSource(1, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(1, 68);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task ManagedStepOut_WhenSteppedPastCall_ReturnsToPreviousLine()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 65 (reliable), step over to 67 (call site), step over again
        // to 68 (past the call), then step out of the method.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit the breakpoint at line 65.
        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Step over 3 times: 65 → 66 → 67 → 68.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);

        // Step out — should leave OnAddClick and return to the caller.
        await WhenSendingStepOut();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(4, "step");
        // Should return to caller (OnAddClickAction wrapper or ScheduleActions lambda).
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task CrossBoundaryStepInto_WhenNativeBpThenStepOut_ReturnsToCaller()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set managed + native BPs. Hit managed first, continue, then native BP fires.
        // Step out from native back to caller.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenSettingBreakpoint(_nativeBpFile, "Calculator.cpp", _nativeAddLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit managed BP first, continue past it.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenSendingContinue();

        // Hit native BP in Calculator::Add.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "breakpoint");
        ThenStackTraceHasSource(0, "Calculator.cpp");

        // Step out from native — should return to caller and stop.
        await WhenSendingStepOut();
        await WhenWaitingForStoppedEvent(timeout: 15);
        ThenStoppedWithReason(2, "step");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task NativeStepOver_WhenAtNativeLine_AdvancesToNextLine()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set managed BP at line 65 plus native BP at Calculator::Add line 7.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenSettingBreakpoint(_nativeBpFile, "Calculator.cpp", _nativeAddLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit managed BP first, continue past it.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenSendingContinue();

        // Hit the native breakpoint.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "breakpoint");
        ThenStackTraceHasSource(0, "Calculator.cpp");

        // Step over — should advance and stop.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        ThenStoppedWithReason(2, "step");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task NativeStepOver_WhenAtLastLine_StepsOutToCSharpLine68()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set managed BP at line 65 plus native BP at Calculator::Add line 7.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenSettingBreakpoint(_nativeBpFile, "Calculator.cpp", _nativeAddLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit managed BP first, continue past it.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenSendingContinue();

        // Hit the native breakpoint at line 7 (return a + b; — the last statement).
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "breakpoint");
        ThenStackTraceHasSource(0, "Calculator.cpp");
        ThenStackTraceStoppedAtLine(0, _nativeAddLine);

        // Step over at the last line — no next line in Calculator::Add, so this
        // should behave like step-out and land in C# MainWindow.xaml.cs line 68.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(2, "step");
        ThenStackTraceHasSource(1, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(1, 68);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    [Fact(Skip = "we have complexer tests now")]
    public async Task StepOutFromNative_WhenInNativeAdd_ReturnsToCSharpLine68()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set managed BP at line 65 plus native BP at Calculator::Add line 7.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _addLine);
        await WhenSettingBreakpoint(_nativeBpFile, "Calculator.cpp", _nativeAddLine);
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit managed BP first, continue past it.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenSendingContinue();

        // Hit the native breakpoint.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "breakpoint");
        ThenStackTraceHasSource(0, "Calculator.cpp");

        // Step out from native line 7 — C++/CLI line 14 is just a return statement,
        // so we should skip it entirely and land in C# MainWindow.xaml.cs line 68.
        await WhenSendingStepOut();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(2, "step");
        ThenStackTraceHasSource(1, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(1, 68);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
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
                    _ = _outputBuilder.Append(text);
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

    private async Task WhenSettingBreakpoint(string filePath, string fileName, int line)
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "setBreakpoints", new
        {
            source = new { path = filePath, name = fileName },
            breakpoints = new[] { new { line } },
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

    private async Task WhenSendingConfigurationDone()
    {
        await SendDapRequest(4, "configurationDone", new { });
        await WhenWaitingForResponse("configurationDone", timeout: 5);
    }

    private async Task WhenSendingNext()
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "next", new { threadId = 0 });
    }

    private async Task WhenSendingStepIn()
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "stepIn", new { threadId = 0 });
    }

    private async Task WhenSendingStepOut()
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "stepOut", new { threadId = 0 });
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

    private async Task WhenRequestingStackTrace()
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "threads", new { });
        await WhenWaitingForResponse("threads", timeout: 10);

        _nextSeq++;
        await SendDapRequest(_nextSeq, "stackTrace", new { threadId = 0, startFrame = 0, levels = 5 });
        await WhenWaitingForStackTraceResponse(timeout: 10);
    }

    private async Task WhenWaitingForResponse(string command, int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                if (_responses.Any(r =>
                    r["command"]?.GetValue<string>() == command))
                {
                    return;
                }
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
        Assert.Fail($"Timed out after {timeout}s waiting for stopped event " +
            $"(#{_stoppedReasons.Count}). Log: {_sessionLogPath}");
    }

    private async Task WhenWaitingForStackTraceResponse(int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                JsonObject? resp = _responses.FirstOrDefault(r =>
                    r["command"]?.GetValue<string>() == "stackTrace");
                if (resp != null)
                {
                    JsonArray? frames = resp["body"]?["stackFrames"]?.AsArray();
                    JsonObject? firstFrame = frames?.FirstOrDefault()?.AsObject();
                    string? sourcePath = firstFrame?["source"]?["path"]?.GetValue<string>();
                    int sourceLine = firstFrame?["line"]?.GetValue<int>() ?? 0;
                    _stackTraceSourcePaths.Add(sourcePath);
                    _stackTraceLines.Add(sourceLine);
                    _ = _responses.Remove(resp);
                    return;
                }
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        Assert.Fail($"Timed out after {timeout}s waiting for stackTrace response " +
            $"(#{_stackTraceSourcePaths.Count}). Log: {_sessionLogPath}");
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

    private void ThenStoppedWithReason(int hitIndex, string expected)
    {
        Assert.True(hitIndex < _stoppedReasons.Count,
            $"Expected stopped event #{hitIndex} but only got {_stoppedReasons.Count}. Log: {_sessionLogPath}");
        Assert.Equal(expected, _stoppedReasons[hitIndex]);
    }

    private void ThenStackTraceHasSource(int hitIndex, string expectedFileName)
    {
        Assert.True(hitIndex < _stackTraceSourcePaths.Count,
            $"Expected stack trace #{hitIndex} but only got {_stackTraceSourcePaths.Count}. Log: {_sessionLogPath}");
        Assert.NotNull(_stackTraceSourcePaths[hitIndex]);
        Assert.Contains(expectedFileName, _stackTraceSourcePaths[hitIndex]!);
    }

    private void ThenStackTraceHasSourceOneOf(int hitIndex, params string[] expectedFileNames)
    {
        Assert.True(hitIndex < _stackTraceSourcePaths.Count,
            $"Expected stack trace #{hitIndex} but only got {_stackTraceSourcePaths.Count}. Log: {_sessionLogPath}");
        Assert.NotNull(_stackTraceSourcePaths[hitIndex]);
        string path = _stackTraceSourcePaths[hitIndex]!;
        Assert.True(Array.Exists(expectedFileNames, path.Contains),
            $"Stack trace source '{path}' does not contain any of [{string.Join(", ", expectedFileNames)}]. Log: {_sessionLogPath}");
    }

    private void ThenStackTraceLineIsNot(int hitIndex, int unexpectedLine)
    {
        Assert.True(hitIndex < _stackTraceLines.Count,
            $"Expected stack trace #{hitIndex} but only got {_stackTraceLines.Count}. Log: {_sessionLogPath}");
        Assert.NotEqual(unexpectedLine, _stackTraceLines[hitIndex]);
    }

    private void ThenStackTraceStoppedAtLine(int hitIndex, int expectedLine)
    {
        Assert.True(hitIndex < _stackTraceLines.Count,
            $"Expected stack trace #{hitIndex} but only got {_stackTraceLines.Count}. Log: {_sessionLogPath}");
        Assert.Equal(expectedLine, _stackTraceLines[hitIndex]);
    }

    private void ThenNoLogErrors()
    {
        if (!File.Exists(_sessionLogPath))
            return;
        using FileStream fs = new(_sessionLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader sr = new(fs);
        string log = sr.ReadToEnd();
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
    private static readonly string _bpFile = Path.Combine(
        _repoRoot, "test", "TestApp", "WpfApp", "MainWindow.xaml.cs");
    private static readonly string _cliWrapperBpFile = Path.Combine(
        _repoRoot, "test", "TestApp", "CliWrapper", "ManagedCalculator.h");
    private static readonly string _nativeBpFile = Path.Combine(
        _repoRoot, "test", "TestApp", "NativeLib", "Calculator.cpp");
    private const int _addLine = 65;
    private const int _addBodyLine = 67;       // int result = ManagedCalculator.Add(a, b);
    private const int _cliWrapperAddLine = 14;  // return NativeLib::Calculator::Add(a, b);
    private const int _nativeAddLine = 7;       // return a + b;

    private readonly string _sessionLogPath = Path.Combine(
        Path.GetTempPath(), $"mixdbg-step-test-{Guid.NewGuid():N}.log");

    private Process? _process;
    private Task? _readTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _outputBuilder = new();
    private readonly List<JsonObject> _responses = [];
    private readonly List<JsonObject> _events = [];
    private readonly SemaphoreSlim _messageArrived = new(0);
    private readonly List<string?> _stoppedReasons = [];
    private readonly List<string?> _stackTraceSourcePaths = [];
    private readonly List<int> _stackTraceLines = [];
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
        _messageArrived.Dispose();

        // Allow the OS to fully release named pipes, profiler DLL handles, and debug
        // sessions before the next test launches a new MixDbg + WpfApp pair.
        await Task.Delay(500);
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
