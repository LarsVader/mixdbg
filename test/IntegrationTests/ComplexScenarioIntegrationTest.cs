using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MixDbg.Tests;

/// <summary>
/// Integration tests for complex debugging scenarios: recursion, loops,
/// exception handling across boundaries, async/await, and lambdas/closures.
/// Uses --auto-test-complex which fires: Fibonacci → CountPrimes → Factorial
/// → AsyncCalc → Complex → exit.
/// </summary>
public sealed class ComplexScenarioIntegrationTest : IAsyncLifetime
{
    // ── Recursion ──────────────────────────────────────────

    /// <summary>
    /// C# breakpoint on OnFibonacciClick fires and shows correct source.
    /// </summary>
    [Fact]
    public async Task Recursion_WhenCSharpFibonacciBpSet_StopsWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at OnFibonacciClick line 96: if (TryGetA(out int n))
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _fibonacciLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(0, _fibonacciLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// Native breakpoint inside recursive Calculator::Fibonacci fires.
    /// The method is called recursively, so the BP should hit and the stack
    /// trace should show Calculator.cpp.
    /// </summary>
    [Fact]
    public async Task Recursion_WhenNativeFibonacciBpSet_StopsInNativeCode()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // C# BP to catch the first stop, then native BP inside Fibonacci.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _fibonacciLine);
        await WhenSettingBreakpoint(_nativeBpFile, "Calculator.cpp", _nativeFibBodyLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        // Hit C# BP first.
        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");
        await WhenSendingContinue();

        // Hit native Fibonacci BP.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "breakpoint");
        ThenStackTraceHasSource(0, "Calculator.cpp");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// Step into from C# Fibonacci call site should enter C++/CLI or native code.
    /// Tests stepping into a recursive native function through the C++/CLI layer.
    /// </summary>
    [Fact]
    public async Task Recursion_WhenStepIntoFibonacciCall_EntersNativeCode()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at OnFibonacciClick line 96, step over to call site (line 98),
        // then step into ManagedCalculator.Fibonacci.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _fibonacciLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Step over: 96 → 98 (the ManagedCalculator.Fibonacci call).
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        ThenStoppedWithReason(1, "step");

        // Step into the Fibonacci call.
        await WhenSendingStepIn();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(2, "step");
        // Should land in C++/CLI wrapper or native Fibonacci.
        ThenStackTraceHasSourceOneOf(0, "ManagedCalculator.h", "Calculator.cpp");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// Step into TryGetA from OnFibonacciClick (line 96), step over inside
    /// TryGetA until the method returns, then verify we land back in
    /// OnFibonacciClick (line 98). Reproduces a bug where stepping over
    /// at the end of TryGetA does not return to the caller.
    /// </summary>
    [Fact]
    public async Task Recursion_WhenStepOverInTryGetA_ReturnsToFibonacciClick()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at OnFibonacciClick line 96: if (TryGetA(out int n))
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _fibonacciLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        // Hit the breakpoint at line 96.
        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Step into TryGetA.
        await WhenSendingStepIn();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "step");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        // Should be inside TryGetA (line 177: if (int.TryParse(...)))
        ThenStackTraceStoppedAtLine(0, _tryGetABodyLine);

        // Step over inside TryGetA — with input "7", TryParse succeeds,
        // so next stop is line 178 (return true).
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(2, "step");
        ThenStackTraceHasSource(1, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(1, _tryGetAReturnLine);

        // Step over at "return true" — TryGetA returns, landing on the
        // opening brace at line 97 in OnFibonacciClick (standard debugger behavior).
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(3, "step");
        ThenStackTraceHasSource(2, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(2, _fibonacciIfBodyLine);

        // Step over past the brace to the Fibonacci call at line 98.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(4, "step");
        ThenStackTraceHasSource(3, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(3, _fibonacciCallLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    // ── Loops ──────────────────────────────────────────────

    /// <summary>
    /// C# breakpoint on OnCountPrimesClick fires and shows correct source.
    /// </summary>
    [Fact]
    public async Task Loop_WhenCSharpCountPrimesBpSet_StopsWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _countPrimesLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        // Fibonacci fires first in the sequence; we need to continue past it
        // if we also set a fib BP. But we only set countPrimes BP, and auto-test-complex
        // calls Fibonacci (no BP) then CountPrimes (has BP). Should stop at CountPrimes.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(0, _countPrimesLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// Native breakpoint inside CountPrimes loop body fires. The for-loop
    /// iterates multiple times — verifies the debugger handles BPs in loops.
    /// </summary>
    [Fact]
    public async Task Loop_WhenNativeBpInsideLoop_StopsInLoopBody()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // C# BP on CountPrimes to catch it first, plus native BP on the IsPrime
        // call inside the loop (Calculator.cpp line 58).
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _countPrimesLine);
        await WhenSettingBreakpoint(_nativeBpFile, "Calculator.cpp", _nativeCountPrimesLoopLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        // Hit C# BP.
        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");
        await WhenSendingContinue();

        // Hit native loop BP.
        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "breakpoint");
        ThenStackTraceHasSource(0, "Calculator.cpp");

        // Continue — should hit again (it's in a loop).
        await WhenSendingContinue();
        await WhenWaitingForStoppedEvent(timeout: 15);
        ThenStoppedWithReason(2, "breakpoint");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    // ── Exception handling ─────────────────────────────────

    /// <summary>
    /// C# breakpoint on OnFactorialClick (try/catch method) fires.
    /// With input=7, factorial succeeds (no throw). Tests that the debugger
    /// handles stepping through try blocks correctly.
    /// </summary>
    [Fact]
    public async Task Exception_WhenFactorialBpInsideTryBlock_StopsWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP on the FactorialOrThrow call inside the try block (line 118).
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _factorialCallLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(0, _factorialCallLine);

        // Step over the call — should land on line 119 (ResultText assignment).
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "step");
        ThenStackTraceHasSource(1, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(1, _factorialResultLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// Step into FactorialOrThrow from C# → C++/CLI → native.
    /// Tests full cross-boundary step-into on a method with exception handling.
    /// </summary>
    [Fact]
    public async Task Exception_WhenStepIntoFactorial_EntersNativeCode()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at the call site (line 118).
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _factorialCallLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Step into FactorialOrThrow.
        await WhenSendingStepIn();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "step");
        ThenStackTraceHasSourceOneOf(0, "ManagedCalculator.h", "Calculator.cpp");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    // ── Async/await ────────────────────────────────────────

    /// <summary>
    /// C# breakpoint on async method entry fires. Async state machine rewriting
    /// changes IL structure — the debugger must map the BP correctly.
    /// </summary>
    [Fact]
    public async Task Async_WhenBpOnAsyncMethodEntry_StopsWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at OnAsyncCalcClick line 130: if (!TryGetInputs(...))
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _asyncEntryLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// C# breakpoint AFTER await (line 143: int sum = ManagedCalculator.SumRange).
    /// This is the hardest async scenario — the continuation runs after the state
    /// machine resumes on the UI thread. The debugger must resolve BPs in the
    /// second half of the async method (MoveNext continuation).
    /// </summary>
    [Fact]
    public async Task Async_WhenBpAfterAwait_StopsOnContinuation()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 143: int sum = ManagedCalculator.SumRange(1, b);
        // This line executes AFTER Task.Run completes and Task.Delay finishes.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _asyncAfterAwaitLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(0, _asyncAfterAwaitLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// C# breakpoint inside the Task.Run lambda (line 137: ManagedCalculator.Fibonacci).
    /// The lambda runs on a thread pool thread. Tests BP resolution on
    /// compiler-generated closure method + cross-thread stop.
    /// </summary>
    [Fact]
    public async Task Async_WhenBpInsideTaskRunLambda_StopsOnThreadPoolThread()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 137: int fib = ManagedCalculator.Fibonacci(a);
        // This runs inside Task.Run(() => { ... }) on a thread pool thread.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _asyncLambdaBodyLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    // ── Lambdas / closures / LINQ ──────────────────────────

    /// <summary>
    /// C# breakpoint inside foreach loop that calls a lambda and native code.
    /// Tests debugging through compiler-generated display class methods.
    /// </summary>
    [Fact]
    public async Task Complex_WhenBpInsideForeachWithLambda_StopsWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 164: int added = ManagedCalculator.Add(scaled, n);
        // Inside foreach loop, after lambda invocation (scale(n)).
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _complexLoopBodyLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(0, _complexLoopBodyLine);

        // Continue — should hit again on next loop iteration.
        await WhenSendingContinue();
        await WhenWaitingForStoppedEvent(timeout: 15);
        ThenStoppedWithReason(1, "breakpoint");

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// C# breakpoint on nested call expression (line 168: ManagedCalculator.Add(
    ///     ManagedCalculator.Multiply(...), ManagedCalculator.Fibonacci(...))).
    /// Tests BP on a line with multiple native calls.
    /// </summary>
    [Fact]
    public async Task Complex_WhenBpOnNestedCalls_StopsWithSource()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 168: int nested = ManagedCalculator.Add(...)
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _complexNestedCallLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(0, "breakpoint");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(0, _complexNestedCallLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    /// <summary>
    /// Step over inside the Complex method's foreach loop — should advance to
    /// the next line within the loop body, not skip the entire loop.
    /// </summary>
    [Fact]
    public async Task Complex_WhenStepOverInsideLoop_AdvancesOneLineNotEntireLoop()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 163: int scaled = scale(n); (first line inside foreach).
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _complexLambdaCallLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Step over — should go to line 164 (the Add call), not skip the loop.
        await WhenSendingNext();
        await WhenWaitingForStoppedEvent(timeout: 15);
        await WhenRequestingStackTrace();
        ThenStoppedWithReason(1, "step");
        ThenStackTraceHasSource(0, "MainWindow.xaml.cs");
        ThenStackTraceStoppedAtLine(0, _complexLoopBodyLine);

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();
        ThenNoLogErrors();
    }

    // ── Variables in complex contexts ──────────────────────

    /// <summary>
    /// When stopped inside OnComplexClick, scopes/variables should return locals
    /// including closure-captured variables (multiplier, threshold, etc.).
    /// </summary>
    [Fact]
    public async Task Complex_WhenStoppedInComplexMethod_VariablesIncludeLocals()
    {
        GivenMixDbgAndWpfAppExist();
        await WhenStartingMixDbg();
        await WhenSendingInitialize();

        // BP at line 164 — inside the loop, after scale(n) and before Add.
        // At this point: a, b, multiplier, scale, numbers, threshold, filtered,
        // total, n, scaled are all in scope.
        await WhenSettingBreakpoint(_bpFile, "MainWindow.xaml.cs", _complexLoopBodyLine);
        await WhenLaunchingWithAutoTestComplex();
        await WhenSendingConfigurationDone();

        await WhenWaitingForStoppedEvent(timeout: 60);
        ThenStoppedWithReason(0, "breakpoint");

        // Request stack trace for frame IDs.
        _nextSeq++;
        await SendDapRequest(_nextSeq, "stackTrace", new { threadId = 0, startFrame = 0, levels = 5 });
        await WhenWaitingForStackTraceResponse(timeout: 10);

        // Request scopes and variables.
        await WhenRequestingScopes(frameId: 1);
        await WhenRequestingVariablesIfScopeReturned();

        await WhenSendingContinue();
        await WhenSendingDisconnect();
        await WhenWaitingForExit();

        ThenScopesResponseHasLocals();
        ThenVariablesResponseHasAtLeastOneVariable();
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

    private async Task WhenLaunchingWithAutoTestComplex()
    {
        await SendDapRequest(3, "launch", new
        {
            program = _wpfAppPath.Replace("/", "\\"),
            cwd = Path.GetDirectoryName(_wpfAppPath)!.Replace("/", "\\"),
            args = new[] { "--auto-test-complex" },
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

    private async Task WhenRequestingScopes(int frameId)
    {
        _nextSeq++;
        await SendDapRequest(_nextSeq, "scopes", new { frameId });
        await WhenWaitingForScopesResponse(timeout: 10);
    }

    private async Task WhenRequestingVariablesIfScopeReturned()
    {
        if (_scopesResponse == null)
            return;

        JsonArray? scopes = _scopesResponse["body"]?["scopes"]?.AsArray();
        JsonObject? firstScope = scopes?.FirstOrDefault()?.AsObject();
        int varRef = firstScope?["variablesReference"]?.GetValue<int>() ?? 0;
        if (varRef == 0)
            return;

        _nextSeq++;
        await SendDapRequest(_nextSeq, "variables", new { variablesReference = varRef });
        await WhenWaitingForVariablesResponse(timeout: 10);
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

    private async Task WhenWaitingForScopesResponse(int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                JsonObject? resp = _responses.FirstOrDefault(r =>
                    r["command"]?.GetValue<string>() == "scopes");
                if (resp != null)
                {
                    _scopesResponse = resp;
                    _ = _responses.Remove(resp);
                    return;
                }
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
    }

    private async Task WhenWaitingForVariablesResponse(int timeout)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                JsonObject? resp = _responses.FirstOrDefault(r =>
                    r["command"]?.GetValue<string>() == "variables");
                if (resp != null)
                {
                    _variablesResponse = resp;
                    _ = _responses.Remove(resp);
                    return;
                }
            }
            _ = await _messageArrived.WaitAsync(TimeSpan.FromMilliseconds(200));
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

    private void ThenStackTraceStoppedAtLine(int hitIndex, int expectedLine)
    {
        Assert.True(hitIndex < _stackTraceLines.Count,
            $"Expected stack trace #{hitIndex} but only got {_stackTraceLines.Count}. Log: {_sessionLogPath}");
        Assert.Equal(expectedLine, _stackTraceLines[hitIndex]);
    }

    private void ThenScopesResponseHasLocals()
    {
        Assert.NotNull(_scopesResponse);
        JsonArray? scopes = _scopesResponse!["body"]?["scopes"]?.AsArray();
        Assert.NotNull(scopes);
        Assert.NotEmpty(scopes!);
        int varRef = scopes[0]!.AsObject()["variablesReference"]?.GetValue<int>() ?? 0;
        Assert.True(varRef > 0, "Scopes response should have a non-zero variablesReference");
    }

    private void ThenVariablesResponseHasAtLeastOneVariable()
    {
        Assert.NotNull(_variablesResponse);
        JsonArray? vars = _variablesResponse!["body"]?["variables"]?.AsArray();
        Assert.NotNull(vars);
        Assert.NotEmpty(vars!);
        JsonObject? first = vars[0]!.AsObject();
        string? name = first["name"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(name), "Variable should have a non-empty name");
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
    private static readonly string _nativeBpFile = Path.Combine(
        _repoRoot, "test", "TestApp", "NativeLib", "Calculator.cpp");

    // MainWindow.xaml.cs line numbers
    private const int _fibonacciLine = 96;         // if (TryGetA(out int n)) in OnFibonacciClick
    private const int _fibonacciIfBodyLine = 97;    // { opening brace of if-block in OnFibonacciClick
    private const int _fibonacciCallLine = 98;      // int result = ManagedCalculator.Fibonacci(n);
    private const int _countPrimesLine = 105;       // if (TryGetA(out int limit)) in OnCountPrimesClick
    private const int _factorialCallLine = 118;     // int result = ManagedCalculator.FactorialOrThrow(n);
    private const int _factorialResultLine = 119;   // ResultText.Text = $"{n}! = {result}";
    private const int _asyncEntryLine = 130;        // if (!TryGetInputs(...)) in OnAsyncCalcClick
    private const int _asyncLambdaBodyLine = 137;   // int fib = ManagedCalculator.Fibonacci(a); (inside Task.Run lambda)
    private const int _asyncAfterAwaitLine = 143;   // int sum = ManagedCalculator.SumRange(1, b); (after await)
    private const int _tryGetABodyLine = 177;        // if (int.TryParse(TextBoxA.Text, out a)) in TryGetA
    private const int _tryGetAReturnLine = 178;     // return true; in TryGetA
    private const int _complexLambdaCallLine = 163; // int scaled = scale(n); (inside foreach)
    private const int _complexLoopBodyLine = 164;   // int added = ManagedCalculator.Add(scaled, n);
    private const int _complexNestedCallLine = 168; // int nested = ManagedCalculator.Add(...)

    // Calculator.cpp line numbers
    private const int _nativeFibBodyLine = 21;      // int prev = Fibonacci(n - 1); (inside recursive body)
    private const int _nativeCountPrimesLoopLine = 58; // if (IsPrime(i)) (inside for loop)

    private readonly string _sessionLogPath = Path.Combine(
        Path.GetTempPath(), $"mixdbg-complex-test-{Guid.NewGuid():N}.log");

    private Process? _process;
    private Task? _readTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _outputBuilder = new();
    private readonly SemaphoreSlim _messageArrived = new(0);
    private readonly List<JsonObject> _responses = [];
    private readonly List<JsonObject> _events = [];
    private readonly List<string?> _stoppedReasons = [];
    private readonly List<string?> _stackTraceSourcePaths = [];
    private readonly List<int> _stackTraceLines = [];
    private JsonObject? _scopesResponse;
    private JsonObject? _variablesResponse;
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

        // Allow the OS to fully release named pipes, profiler DLL handles, and debug
        // sessions before the next test launches a new MixDbg + WpfApp pair.
        await Task.Delay(500);
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
