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

        // Hit 1: OnAddClick — ICorDebug handles pre-JIT breakpoints, fires on first call.
        await WhenWaitingForStoppedEvent(timeout: 20);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: OnMultiplyClick — fires on first call.
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
    public async Task ManagedBreakpoint_WhenDoubleClicked_BothFireOnSecondCall()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();
        await WhenSettingTwoManagedBreakpoints();
        await WhenLaunchingWithAutoTestDouble();
        await WhenSendingConfigurationDone();

        // Hit 1: OnAddClick — second call (first JITs, DAC needs ~12s to detect, second hits hw BP).
        await WhenWaitingForStoppedEvent(timeout: 40);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: OnMultiplyClick — second call.
        await WhenWaitingForStoppedEvent(timeout: 60);
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

    [Fact]
    public async Task ManagedBreakpoint_WhenCliWrapperMethodBreakpointed_StopsWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set breakpoints on C++/CLI wrapper methods in ManagedCalculator.h.
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _cliWrapperBpFile, name = "ManagedCalculator.h" },
            breakpoints = new[] { new { line = _cliWrapperAddLine }, new { line = _cliWrapperMultiplyLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // Use --auto-test: first-click BPs now work for C++/CLI via assembly-level
        // WATCH (MIXDBG_WATCH_ASSEMBLIES). The profiler hooks all methods from the
        // C++/CLI assembly, so ENTER fires on the very first call.
        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit 1: ManagedCalculator::Add (first call)
        await WhenWaitingForStoppedEvent(timeout: 20);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: ManagedCalculator::Multiply (first call)
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenStackTraceHasSource(hitIndex: 0, "ManagedCalculator.h");
        ThenBreakpointWasHit(hitIndex: 1);
        ThenStackTraceHasSource(hitIndex: 1, "ManagedCalculator.h");
        ThenNoLogErrors();
    }

    [Fact]
    public async Task MixedBreakpoints_WhenNativeAndManagedBothSet_BothFireInSameSession()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set managed BPs: line 65 (before native call) and line 68 (after native call returns).
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _bpFile, name = "MainWindow.xaml.cs" },
            breakpoints = new[] { new { line = _addLine }, new { line = _afterNativeCallLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // Set a native BP on Calculator::Add (native C++).
        await SendDapRequest(5, "setBreakpoints", new
        {
            source = new { path = _nativeBpFile, name = "Calculator.cpp" },
            breakpoints = new[] { new { line = _nativeAddLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit 1: managed BP at OnAddClick line 65 (before the native call).
        await WhenWaitingForStoppedEvent(timeout: 20);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: native BP at Calculator::Add (inside the native call).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 3: managed BP at OnAddClick line 68 (after the native call returns).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenStackTraceHasSource(hitIndex: 0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(hitIndex: 0, _addLine);
        ThenBreakpointWasHit(hitIndex: 1);
        ThenStackTraceHasSource(hitIndex: 1, "Calculator.cpp");
        ThenBreakpointWasHit(hitIndex: 2);
        ThenStackTraceHasSource(hitIndex: 2, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(hitIndex: 2, _afterNativeCallLine);
        ThenNoLogErrors();
    }

    [Fact]
    public async Task MixedBreakpoints_WhenAllThreeLayers_AllFire()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set C# managed BP on OnAddClick (line 65).
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _bpFile, name = "MainWindow.xaml.cs" },
            breakpoints = new[] { new { line = _addLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // Set C++/CLI BP on ManagedCalculator::Add (line 14).
        await SendDapRequest(5, "setBreakpoints", new
        {
            source = new { path = _cliWrapperBpFile, name = "ManagedCalculator.h" },
            breakpoints = new[] { new { line = _cliWrapperAddLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // Set native C++ BP on Calculator::Add (line 7).
        await SendDapRequest(6, "setBreakpoints", new
        {
            source = new { path = _nativeBpFile, name = "Calculator.cpp" },
            breakpoints = new[] { new { line = _nativeAddLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit 1: C# BP at OnAddClick (line 65).
        await WhenWaitingForStoppedEvent(timeout: 20);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: C++/CLI BP at ManagedCalculator::Add (line 14).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 3: native BP at Calculator::Add (line 7).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenStackTraceHasSource(hitIndex: 0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(hitIndex: 0, _addLine);
        ThenBreakpointWasHit(hitIndex: 1);
        ThenStackTraceHasSource(hitIndex: 1, "ManagedCalculator.h");
        ThenBreakpointWasHit(hitIndex: 2);
        ThenStackTraceHasSource(hitIndex: 2, "Calculator.cpp");
        ThenNoLogErrors();
    }

    [Fact]
    public async Task MixedBreakpoints_WhenAllFourBreakpointsSet_AllFireInOrder()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set two C# managed BPs: line 65 (before native call) and line 68 (after).
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _bpFile, name = "MainWindow.xaml.cs" },
            breakpoints = new[] { new { line = _addLine }, new { line = _afterNativeCallLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // Set C++/CLI BP on ManagedCalculator::Add (line 14).
        await SendDapRequest(5, "setBreakpoints", new
        {
            source = new { path = _cliWrapperBpFile, name = "ManagedCalculator.h" },
            breakpoints = new[] { new { line = _cliWrapperAddLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // Set native C++ BP on Calculator::Add (line 7).
        await SendDapRequest(6, "setBreakpoints", new
        {
            source = new { path = _nativeBpFile, name = "Calculator.cpp" },
            breakpoints = new[] { new { line = _nativeAddLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit 1: C# BP at OnAddClick (line 65).
        await WhenWaitingForStoppedEvent(timeout: 20);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: C++/CLI BP at ManagedCalculator::Add (line 14).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 3: native BP at Calculator::Add (line 7).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 4: C# BP at OnAddClick (line 68, after native call returns).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenStackTraceHasSource(hitIndex: 0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(hitIndex: 0, _addLine);
        ThenBreakpointWasHit(hitIndex: 1);
        ThenStackTraceHasSource(hitIndex: 1, "ManagedCalculator.h");
        ThenBreakpointWasHit(hitIndex: 2);
        ThenStackTraceHasSource(hitIndex: 2, "Calculator.cpp");
        ThenBreakpointWasHit(hitIndex: 3);
        ThenStackTraceHasSource(hitIndex: 3, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(hitIndex: 3, _afterNativeCallLine);
        ThenNoLogErrors();
    }

    [Fact]
    public async Task MixedBreakpoints_WhenAllLayersOnBothMethods_AllEightStopsFire()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // C# BPs: before and after native call in both OnAddClick and OnMultiplyClick.
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _bpFile, name = "MainWindow.xaml.cs" },
            breakpoints = new[]
            {
                new { line = _addLine },
                new { line = _afterNativeCallLine },
                new { line = _multiplyLine },
                new { line = _afterMultiplyNativeCallLine },
            },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // C++/CLI BPs: Add and Multiply.
        await SendDapRequest(5, "setBreakpoints", new
        {
            source = new { path = _cliWrapperBpFile, name = "ManagedCalculator.h" },
            breakpoints = new[] { new { line = _cliWrapperAddLine }, new { line = _cliWrapperMultiplyLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        // Native BPs: Add and Multiply.
        await SendDapRequest(6, "setBreakpoints", new
        {
            source = new { path = _nativeBpFile, name = "Calculator.cpp" },
            breakpoints = new[] { new { line = _nativeAddLine }, new { line = _nativeMultiplyLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Add click: 4 stops (C# 65 → CLI 14 → native 7 → C# 68).
        for (int i = 0; i < 4; i++)
        {
            await WhenWaitingForStoppedEvent(timeout: 30);
            await WhenRequestingStackTraceForMultipleThreads();
            await WhenSendingContinue();
        }

        // Multiply click: 4 stops (C# 74 → CLI 19 → native 12 → C# 77).
        for (int i = 0; i < 4; i++)
        {
            await WhenWaitingForStoppedEvent(timeout: 30);
            await WhenRequestingStackTraceForMultipleThreads();
            await WhenSendingContinue();
        }

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        // Add cycle: C# → CLI → native → C#.
        ThenBreakpointWasHit(hitIndex: 0);
        ThenStackTraceHasSource(hitIndex: 0, "MainWindow.xaml.cs");
        ThenBreakpointWasHit(hitIndex: 1);
        ThenStackTraceHasSource(hitIndex: 1, "ManagedCalculator.h");
        ThenBreakpointWasHit(hitIndex: 2);
        ThenStackTraceHasSource(hitIndex: 2, "Calculator.cpp");
        ThenBreakpointWasHit(hitIndex: 3);
        ThenStackTraceHasSource(hitIndex: 3, "MainWindow.xaml.cs");

        // Multiply cycle: C# → CLI → native → C#.
        ThenBreakpointWasHit(hitIndex: 4);
        ThenStackTraceHasSource(hitIndex: 4, "MainWindow.xaml.cs");
        ThenBreakpointWasHit(hitIndex: 5);
        ThenStackTraceHasSource(hitIndex: 5, "ManagedCalculator.h");
        ThenBreakpointWasHit(hitIndex: 6);
        ThenStackTraceHasSource(hitIndex: 6, "Calculator.cpp");
        ThenBreakpointWasHit(hitIndex: 7);
        ThenStackTraceHasSource(hitIndex: 7, "MainWindow.xaml.cs");

        ThenNoLogErrors();
    }

    [Fact]
    public async Task ManagedBreakpoint_WhenBreakpointInsideMethodBody_StopsAtExactLine()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // Set breakpoints INSIDE the if-block (line 65 = ManagedCalculator.Add call,
        // line 74 = ManagedCalculator.Multiply call) — NOT at method entry.
        await SendDapRequest(2, "setBreakpoints", new
        {
            source = new { path = _bpFile, name = "MainWindow.xaml.cs" },
            breakpoints = new[] { new { line = _addBodyLine }, new { line = _multiplyBodyLine } },
        });
        await WhenWaitingForResponse("setBreakpoints", timeout: 5);

        await WhenLaunchingWithAutoTest();
        await WhenSendingConfigurationDone();

        // Hit 1: should stop at line 65 (inside OnAddClick), NOT at line 61/63.
        await WhenWaitingForStoppedEvent(timeout: 20);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        // Hit 2: should stop at line 74 (inside OnMultiplyClick).
        await WhenWaitingForStoppedEvent(timeout: 30);
        await WhenRequestingStackTraceForMultipleThreads();
        await WhenSendingContinue();

        await WhenWaitingForSeconds(2);
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenBreakpointWasHit(hitIndex: 0);
        ThenStackTraceHasSource(hitIndex: 0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(hitIndex: 0, _addBodyLine);
        ThenBreakpointWasHit(hitIndex: 1);
        ThenStackTraceHasSource(hitIndex: 1, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(hitIndex: 1, _multiplyBodyLine);
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

    private async Task WhenLaunchingWithAutoTestDouble()
    {
        await SendDapRequest(3, "launch", new
        {
            program = _wpfAppPath.Replace("/", "\\"),
            cwd = Path.GetDirectoryName(_wpfAppPath)!.Replace("/", "\\"),
            args = new[] { "--auto-test-double" },
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

    private async Task WhenWaitingForSeconds(int seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds));

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
            await Task.Delay(100);
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
            await Task.Delay(200);
        }
        _stoppedReasons.Add(null);
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
            await Task.Delay(200);
        }
        _stackTraceSourcePaths.Add(null);
        _stackTraceLines.Add(0);
    }

    private async Task WhenWaitingAndConsumingStackTraceResponse(int timeout)
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
                    _ = _responses.Remove(resp);
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

    private void ThenStackTraceStoppedAtLine(int hitIndex, int expectedLine)
    {
        Assert.True(hitIndex < _stackTraceLines.Count,
            $"Expected stack trace #{hitIndex} but only got {_stackTraceLines.Count}");
        Assert.Equal(expectedLine, _stackTraceLines[hitIndex]);
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

    // Paths relative to repo root — computed from test assembly location.
    // Assembly: test/IntegrationTests/bin/Debug/net10.0/ → 5 levels up.
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
    private const int _multiplyLine = 74;
    private const int _addBodyLine = 67;       // int result = ManagedCalculator.Add(a, b);
    private const int _multiplyBodyLine = 76;  // int result = ManagedCalculator.Multiply(a, b);
    private const int _afterNativeCallLine = 68; // ResultText.Text = $"{a} + {b} = {result}";
    private const int _cliWrapperAddLine = 14;      // return NativeLib::Calculator::Add(a, b);
    private const int _cliWrapperMultiplyLine = 19;  // return NativeLib::Calculator::Multiply(a, b);
    private const int _nativeAddLine = 7;       // return a + b; (Calculator.cpp)
    private const int _nativeMultiplyLine = 12;  // return a * b; (Calculator.cpp)
    private const int _afterMultiplyNativeCallLine = 77; // ResultText.Text = $"{a} × {b} = {result}";

    private readonly string _sessionLogPath = Path.Combine(
        Path.GetTempPath(), $"mixdbg-test-{Guid.NewGuid():N}.log");

    private Process? _process;
    private Task? _readTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _outputBuilder = new();
    private readonly List<JsonObject> _responses = [];
    private readonly List<JsonObject> _events = [];
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
        // Don't delete — keep for post-failure inspection.
        // try { File.Delete(_sessionLogPath); } catch { }
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
                    lock (_responses) { _responses.Add(obj); }
                else if (msgType == "event")
                    lock (_events) { _events.Add(obj); }
            }
            catch { }
        }

        _ = _partialBuffer.Clear();
        _ = _partialBuffer.Append(buf);
    }

    #endregion
}