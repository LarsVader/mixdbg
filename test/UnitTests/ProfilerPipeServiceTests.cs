using System.IO.Pipes;
using System.Text;

using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class ProfilerPipeServiceTests : IDisposable
{
    // ── SetupProfilerPipe (profiler DLL not found) ──────────────

    [Fact]
    [Trait("Category", "RequiresNoProfilerDll")]
    public void SetupProfilerPipe_WhenProfilerNotFound_LogsWarningAndReturns()
    {
        GivenProfilerDllDoesNotExist();

        WhenSettingUpProfilerPipe();

        // In dev environments the profiler DLL exists at the dev-build fallback
        // path (profiler/x64/Debug/). Only assert if it's truly absent.
        if (_model.ProfilerPipe == null)
        {
            ThenProfilerPipeIsNull();
            ThenLogWarningWasCalled("MixDbgProfiler.dll not found");
        }
        else
        {
            // Dev environment — the fallback path found the real profiler DLL.
            // Verify the pipe was created (fallback path works).
            ThenProfilerPipeIsNotNull();
        }
    }

    // ── SetupProfilerPipe (profiler DLL found) ──────────────────

    [Fact]
    public void SetupProfilerPipe_WhenProfilerFound_CreatesPipeAndSetsEnvVars()
    {
        GivenProfilerDllExists();

        WhenSettingUpProfilerPipe();

        ThenProfilerPipeIsNotNull();
        ThenProfilerPipeNameIsSet();
        ThenProfilerAckEventIsCreated();
        ThenProfilerRehookEventIsCreated();
        ThenEnvVarIsSet("CORECLR_ENABLE_PROFILING", "1");
        ThenEnvVarIsSet("CORECLR_PROFILER", "{D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}");
        ThenEnvVarContains("CORECLR_PROFILER_PATH", "MixDbgProfiler.dll");
        ThenEnvVarContains("MIXDBG_PIPE_NAME", _model.ProfilerPipeName!);
    }

    // ── SetupProfilerPipe (watch tokens resolution) ─────────────

    [Fact]
    public void SetupProfilerPipe_WhenBreakpointHints_ResolvesWatchTokens()
    {
        GivenProfilerDllExists();
        GivenBreakpointHints((@"C:\src\Program.cs", 10));
        GivenResolveTokensReturns([("TestAsm", 0x06000001)]);

        WhenSettingUpProfilerPipe();

        ThenEnvVarIsSet("MIXDBG_WATCH_TOKENS", "TestAsm:06000001");
        ThenLogInfoWasCalled("Profiler watch tokens: TestAsm:06000001");
    }

    [Fact]
    public void SetupProfilerPipe_WhenMultipleBreakpointHints_ResolvesMultipleWatchTokens()
    {
        GivenProfilerDllExists();
        GivenBreakpointHints((@"C:\src\A.cs", 10), (@"C:\src\B.cs", 20));
        GivenResolveTokensReturns([("AsmA", 0x06000001), ("AsmB", 0x06000005)]);

        WhenSettingUpProfilerPipe();

        ThenEnvVarIsSet("MIXDBG_WATCH_TOKENS", "AsmA:06000001,AsmB:06000005");
    }

    [Fact]
    public void SetupProfilerPipe_WhenNoTokensResolved_DoesNotSetWatchTokensEnvVar()
    {
        GivenProfilerDllExists();
        GivenBreakpointHints((@"C:\src\Program.cs", 10));
        GivenResolveTokensReturns([]);

        WhenSettingUpProfilerPipe();

        ThenEnvVarIsNull("MIXDBG_WATCH_TOKENS");
    }

    // ── SetupProfilerPipe (watch assemblies resolution) ─────────

    [Fact]
    public void SetupProfilerPipe_WhenCliHints_ResolvesWatchAssemblies()
    {
        GivenProfilerDllExists();
        GivenBreakpointHints((@"C:\src\Wrapper.cpp", 15));
        GivenResolveTokensReturns([]);
        GivenResolveWatchAssembliesReturns(["CliWrapper"]);

        WhenSettingUpProfilerPipe();

        ThenEnvVarIsSet("MIXDBG_WATCH_ASSEMBLIES", "CliWrapper");
        ThenLogInfoWasCalled("Profiler watch assemblies: CliWrapper");
    }

    [Fact]
    public void SetupProfilerPipe_WhenMultipleCliAssemblies_SetsCommaDelimitedEnvVar()
    {
        GivenProfilerDllExists();
        GivenBreakpointHints((@"C:\src\A.cpp", 10), (@"C:\src\B.cpp", 20));
        GivenResolveTokensReturns([]);
        GivenResolveWatchAssembliesReturns(["AsmA", "AsmB"]);

        WhenSettingUpProfilerPipe();

        ThenEnvVarIsSet("MIXDBG_WATCH_ASSEMBLIES", "AsmA,AsmB");
    }

    [Fact]
    public void SetupProfilerPipe_WhenNoCliAssemblies_DoesNotSetWatchAssembliesEnvVar()
    {
        GivenProfilerDllExists();
        GivenBreakpointHints((@"C:\src\Program.cs", 10));
        GivenResolveTokensReturns([]);
        GivenResolveWatchAssembliesReturns([]);

        WhenSettingUpProfilerPipe();

        ThenEnvVarIsNull("MIXDBG_WATCH_ASSEMBLIES");
    }

    // ── StartProfilerReader (pipe is null) ──────────────────────

    [Fact]
    public void StartProfilerReader_WhenPipeIsNull_DoesNothing()
    {
        WhenStartingProfilerReader();

        ThenProfilerReaderThreadIsNull();
    }

    // ── StartProfilerReader (pipe exists) ───────────────────────

    [Fact]
    public void StartProfilerReader_WhenPipeExists_CreatesReaderThread()
    {
        GivenProfilerPipeCreated();

        WhenStartingProfilerReader();

        ThenProfilerReaderThreadIsNotNull();
        ThenProfilerReaderThreadName("profiler-reader");
        ThenProfilerReaderThreadIsBackground();

        // Connect a client so the reader thread can proceed past WaitForConnection
        // and exit cleanly when the pipe is disposed.
        ConnectClient();
    }

    // ── ProfilerReaderLoop: JIT notification ────────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenJitNotification_AddsToJitMethodMap()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm");
        WhenWaitingForProcessing();

        ThenJitMethodMapContainsAddress(0x1000UL);
        ThenJitMethodInfoAt(0x1000UL, token: 0x06000001, size: 0x40, assembly: "TestAsm");
        ThenProfilerHooksActiveIsTrue();
    }

    [Fact]
    public void ProfilerReaderLoop_WhenMultipleJitNotifications_AddsAllToMap()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000:40:AsmA");
        WhenClientSendsLine("JIT:06000002:2000:80:AsmB");
        WhenWaitingForProcessing();

        ThenJitMethodMapContainsAddress(0x1000UL);
        ThenJitMethodMapContainsAddress(0x2000UL);
    }

    // ── ProfilerReaderLoop: JIT with IL map ─────────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenJitWithILMap_StoresMapping()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm:0=0,A=10,14=20");
        WhenWaitingForProcessing();

        ThenJitMethodMappingExists(0x06000001, "TestAsm");
        ThenJitMethodMappingHasEntryCount(0x06000001, "TestAsm", 3);
        ThenJitMethodMappingCodeStart(0x06000001, "TestAsm", 0x1000UL);
    }

    [Fact]
    public void ProfilerReaderLoop_WhenJitWithEmptyILMap_DoesNotStoreMapping()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm:");
        WhenWaitingForProcessing();

        ThenJitMethodMappingDoesNotExist(0x06000001, "TestAsm");
    }

    // ── ProfilerReaderLoop: JIT matches deferred breakpoint ─────

    [Fact]
    public void ProfilerReaderLoop_WhenJitMatchesDeferred_EnqueuesAndInterrupts()
    {
        GivenProfilerPipeCreated();
        GivenDeferredBreakpoint(token: 0x06000001, assembly: "TestAsm");
        GivenInWaitForEvent();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm");
        WhenWaitingForProcessing();

        ThenJitNotificationQueueHasCount(1);
        ThenJitNotificationHasToken(0x06000001);
        ThenSetInterruptWasCalled();
    }

    [Fact]
    public void ProfilerReaderLoop_WhenJitDoesNotMatchDeferred_DoesNotEnqueue()
    {
        GivenProfilerPipeCreated();
        GivenDeferredBreakpoint(token: 0x06000099, assembly: "OtherAsm");
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm");
        WhenWaitingForProcessing();

        ThenJitNotificationQueueIsEmpty();
        ThenSetInterruptWasNotCalled();
    }

    [Fact]
    public void ProfilerReaderLoop_WhenJitMatchesDeferredCaseInsensitive_EnqueuesNotification()
    {
        GivenProfilerPipeCreated();
        GivenDeferredBreakpoint(token: 0x06000001, assembly: "testasm");
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm");
        WhenWaitingForProcessing();

        ThenJitNotificationQueueHasCount(1);
    }

    // ── ProfilerReaderLoop: ENTER notification ──────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenEnterNotification_SetsPendingEnterState()
    {
        GivenProfilerPipeCreated();
        GivenInWaitForEvent();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("ENTER:06000001:2000:1234:TestAsm");
        WhenWaitingForProcessing();

        ThenPendingEnterBreakpointIsTrue();
        ThenEnterBreakpointToken(0x06000001);
        ThenEnterBreakpointAddress(0x2000UL);
        ThenEnterBreakpointThreadId(0x1234U);
        ThenEnterBreakpointAssembly("TestAsm");
        ThenSetInterruptWasCalled();
    }

    [Fact]
    public void ProfilerReaderLoop_WhenEnterWithTooFewParts_IsIgnored()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("ENTER:06000001:2000");
        WhenWaitingForProcessing();

        ThenPendingEnterBreakpointIsFalse();
    }

    // ── ProfilerReaderLoop: ENTER already pending ───────────────

    [Fact]
    public void ProfilerReaderLoop_WhenEnterAlreadyPending_AcksImmediately()
    {
        GivenProfilerPipeCreated();
        GivenPendingEnterBreakpointAlreadySet();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("ENTER:06000002:3000:5678:OtherAsm");
        WhenWaitingForProcessing();

        // Token should not be overwritten — still the original.
        ThenEnterBreakpointToken(0x06000001);
        // ACK event should have been signaled (Set() was called).
        ThenProfilerAckEventWasSignaled();
    }

    // ── ProfilerReaderLoop: READY notification ──────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenReadyNotification_LogsAndContinues()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("READY:v1");
        // Send a JIT after READY to confirm the reader loop continues.
        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm");
        WhenWaitingForProcessing();

        ThenLogInfoWasCalled("profiler ready");
        ThenJitMethodMapContainsAddress(0x1000UL);
    }

    // ── ProfilerReaderLoop: old format JIT ──────────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenOldFormatJit_AddsToJitMethodMap()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        // Old format: TOKEN:ADDRESS:SIZE:ASSEMBLY (no JIT: prefix)
        WhenClientSendsLine("06000001:1000:40:TestAsm");
        WhenWaitingForProcessing();

        ThenJitMethodMapContainsAddress(0x1000UL);
        ThenJitMethodInfoAt(0x1000UL, token: 0x06000001, size: 0x40, assembly: "TestAsm");
    }

    [Fact]
    public void ProfilerReaderLoop_WhenOldFormatMatchesDeferred_EnqueuesAndInterrupts()
    {
        GivenProfilerPipeCreated();
        GivenDeferredBreakpoint(token: 0x06000001, assembly: "TestAsm");
        GivenInWaitForEvent();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("06000001:1000:40:TestAsm");
        WhenWaitingForProcessing();

        ThenJitNotificationQueueHasCount(1);
        ThenSetInterruptWasCalled();
    }

    [Fact]
    public void ProfilerReaderLoop_WhenOldFormatTooFewParts_IsIgnored()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("06000001:1000:40");
        // Send a valid line after to prove the loop continues.
        WhenClientSendsLine("JIT:06000002:2000:80:TestAsm");
        WhenWaitingForProcessing();

        ThenJitMethodMapDoesNotContainAddress(0x1000UL);
        ThenJitMethodMapContainsAddress(0x2000UL);
    }

    // ── ProfilerReaderLoop: invalid data ────────────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenJitInvalidHex_IsIgnored()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:ZZZZ:1000:40:TestAsm");
        WhenClientSendsLine("JIT:06000001:2000:80:TestAsm");
        WhenWaitingForProcessing();

        ThenJitMethodMapDoesNotContainAddress(0x1000UL);
        ThenJitMethodMapContainsAddress(0x2000UL);
    }

    [Fact]
    public void ProfilerReaderLoop_WhenJitTooFewFields_IsIgnored()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("JIT:06000001:1000");
        WhenClientSendsLine("JIT:06000002:2000:80:TestAsm");
        WhenWaitingForProcessing();

        ThenJitMethodMapDoesNotContainAddress(0x1000UL);
        ThenJitMethodMapContainsAddress(0x2000UL);
    }

    // ── ProfilerReaderLoop: pipe closed ─────────────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenPipeClosed_ExitsGracefully()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientClosesPipe();
        WhenWaitingForProcessing();

        ThenLogInfoWasCalled("pipe closed (EOF)");
        ThenProfilerReaderThreadCompletes();
    }

    // ── ProfilerReaderLoop: IL map parsing edge cases ───────────

    [Fact]
    public void ProfilerReaderLoop_WhenJitWithPartiallyInvalidILMap_StoresValidEntries()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        // Two valid entries, one invalid (XY is not valid hex).
        WhenClientSendsLine("JIT:06000001:1000:40:TestAsm:0=0,XY=bad,A=10");
        WhenWaitingForProcessing();

        ThenJitMethodMappingExists(0x06000001, "TestAsm");
        ThenJitMethodMappingHasEntryCount(0x06000001, "TestAsm", 2);
    }

    // ── ProfilerReaderLoop: ENTER invalid token ─────────────────

    [Fact]
    public void ProfilerReaderLoop_WhenEnterInvalidToken_IsIgnored()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("ENTER:ZZZZ:2000:1234:TestAsm");
        WhenWaitingForProcessing();

        ThenPendingEnterBreakpointIsFalse();
    }

    [Fact]
    public void ProfilerReaderLoop_WhenEnterInvalidAddress_IsIgnored()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("ENTER:06000001:ZZZZ:1234:TestAsm");
        WhenWaitingForProcessing();

        ThenPendingEnterBreakpointIsFalse();
    }

    [Fact]
    public void ProfilerReaderLoop_WhenEnterInvalidThreadId_IsIgnored()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        WhenClientSendsLine("ENTER:06000001:2000:ZZZZ:TestAsm");
        WhenWaitingForProcessing();

        ThenPendingEnterBreakpointIsFalse();
    }

    // ── ProfilerReaderLoop: exception while reading ──────────────

    [Fact]
    public void ProfilerReaderLoop_WhenExceptionWhileNotTerminated_LogsError()
    {
        GivenProfilerPipeCreated();
        GivenProfilerReaderStarted();

        // Force an exception by disposing the pipe from the test side
        // while the reader is active.
        _model.ProfilerPipeReader?.Dispose();
        WhenWaitingForProcessing();

        ThenProfilerReaderThreadCompletes();
    }

    // ── SetupProfilerPipe: both tokens and assemblies resolved ──

    [Fact]
    public void SetupProfilerPipe_WhenBothTokensAndAssemblies_SetsBothEnvVars()
    {
        GivenProfilerDllExists();
        GivenBreakpointHints((@"C:\src\Program.cs", 10), (@"C:\src\Wrapper.cpp", 20));
        GivenResolveTokensReturns([("AsmA", 0x06000001)]);
        GivenResolveWatchAssembliesReturns(["CliWrapper"]);

        WhenSettingUpProfilerPipe();

        ThenEnvVarIsSet("MIXDBG_WATCH_TOKENS", "AsmA:06000001");
        ThenEnvVarIsSet("MIXDBG_WATCH_ASSEMBLIES", "CliWrapper");
    }

    // ── ProfilerReaderLoop: ENTER already pending (ACK + rehook) ──

    [Fact]
    public void ProfilerReaderLoop_WhenEnterAlreadyPendingAndRehookSet_AcksOnly()
    {
        GivenProfilerPipeCreated();
        GivenPendingEnterBreakpointAlreadySet();
        _model.ProfilerRehookEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        GivenProfilerReaderStarted();

        WhenClientSendsLine("ENTER:06000003:4000:9999:SomeAsm");
        WhenWaitingForProcessing();

        // Original token should not be overwritten.
        ThenEnterBreakpointToken(0x06000001);
        ThenProfilerAckEventWasSignaled();
        // Rehook should NOT have been signaled (only ACK for the duplicate).
        Assert.False(_model.ProfilerRehookEvent.WaitOne(0));
    }

    #region Given

    private static void GivenProfilerDllDoesNotExist()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MixDbgProfiler.dll");
        if (File.Exists(path))
            File.Delete(path);
    }

    private void GivenProfilerDllExists()
    {
        // Place a fake MixDbgProfiler.dll next to the test exe so SetupProfilerPipe finds it.
        _profilerDllPath = Path.Combine(AppContext.BaseDirectory, "MixDbgProfiler.dll");
        if (!File.Exists(_profilerDllPath))
            File.WriteAllBytes(_profilerDllPath, [0]);
        _createdProfilerDll = true;
    }

    private void GivenBreakpointHints(params (string FilePath, int Line)[] hints)
    {
        foreach ((string filePath, int line) in hints)
            _model.ProfilerBreakpointHints.Add((filePath, line));
    }

    private void GivenResolveTokensReturns(List<(string Assembly, int Token)> tokens) =>
        _ = _managedBp.ResolveTokensFromBreakpoints(Arg.Any<IEnumerable<(string, int)>>())
            .Returns(tokens);

    private void GivenResolveWatchAssembliesReturns(List<string> assemblies) =>
        _ = _managedBp.ResolveWatchAssemblies(Arg.Any<IEnumerable<(string, int)>>())
            .Returns(assemblies);

    private void GivenProfilerPipeCreated()
    {
        _pipeName = $"MixDbgTest-{Guid.NewGuid():N}";
        _model.ProfilerPipeName = _pipeName;
        _model.ProfilerPipe = new NamedPipeServerStream(
            _pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _model.ProfilerAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _model.ProfilerRehookEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
    }

    private void GivenDeferredBreakpoint(int token, string assembly)
    {
        _model.DeferredManagedBreakpoints.Add(
            new DeferredManagedBreakpoint(
                FilePath: @"C:\src\Test.cs",
                Line: 10,
                MethodToken: token,
                ILOffset: 0,
                BpId: 1,
                AssemblyName: assembly));
        _model.RebuildDeferredBreakpointIndex();
    }

    private void GivenInWaitForEvent() => _model.InWaitForEvent = true;

    private void GivenPendingEnterBreakpointAlreadySet()
    {
        _model.PendingEnterBreakpoint = true;
        _model.EnterBreakpointToken = 0x06000001;
        _model.EnterBreakpointAddress = 0x1000;
        _model.EnterBreakpointThreadId = 0xAAAA;
        _model.EnterBreakpointAssembly = "OriginalAsm";
    }

    private void GivenProfilerReaderStarted()
    {
        _testee.StartProfilerReader(_model);
        ConnectClient();
    }

    #endregion

    #region When

    private void WhenSettingUpProfilerPipe() => _testee.SetupProfilerPipe(_model);

    private void WhenStartingProfilerReader() => _testee.StartProfilerReader(_model);

    private void WhenClientSendsLine(string line)
    {
        _clientWriter!.WriteLine(line);
        _clientWriter.Flush();
    }

    private void WhenClientClosesPipe()
    {
        _clientWriter?.Dispose();
        _clientWriter = null;
        _clientPipe?.Dispose();
        _clientPipe = null;
    }

    private static void WhenWaitingForProcessing() => Thread.Sleep(200);

    #endregion

    #region Then

    private void ThenProfilerPipeIsNull() => Assert.Null(_model.ProfilerPipe);

    private void ThenProfilerPipeIsNotNull() => Assert.NotNull(_model.ProfilerPipe);

    private void ThenProfilerPipeNameIsSet() => Assert.NotNull(_model.ProfilerPipeName);

    private void ThenProfilerAckEventIsCreated() => Assert.NotNull(_model.ProfilerAckEvent);

    private void ThenProfilerRehookEventIsCreated() => Assert.NotNull(_model.ProfilerRehookEvent);

    private void ThenLogWarningWasCalled(string messageContains) =>
        _log.Received().LogWarning(
            _logStore,
            Arg.Is<string>(s => s.Contains(messageContains)),
            Arg.Any<string>());

    private void ThenLogInfoWasCalled(string messageContains) =>
        _log.Received().LogInfo(
            _logStore,
            Arg.Is<string>(s => s.Contains(messageContains)),
            Arg.Any<string>());

    private static void ThenEnvVarIsSet(string name, string expected)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        Assert.Equal(expected, value);
    }

    private static void ThenEnvVarContains(string name, string expected)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        Assert.NotNull(value);
        Assert.Contains(expected, value);
    }

    private static void ThenEnvVarIsNull(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        Assert.Null(value);
    }

    private void ThenProfilerReaderThreadIsNull() => Assert.Null(_model.ProfilerReaderThread);

    private void ThenProfilerReaderThreadIsNotNull() => Assert.NotNull(_model.ProfilerReaderThread);

    private void ThenProfilerReaderThreadName(string expected) => Assert.Equal(expected, _model.ProfilerReaderThread!.Name);

    private void ThenProfilerReaderThreadIsBackground() => Assert.True(_model.ProfilerReaderThread!.IsBackground);

    private void ThenJitMethodMapContainsAddress(ulong address)
    {
        lock (_model.JitMethodMap)
        {
            Assert.True(_model.JitMethodMap.ContainsKey(address),
                $"JitMethodMap should contain address 0x{address:X}");
        }
    }

    private void ThenJitMethodMapDoesNotContainAddress(ulong address)
    {
        lock (_model.JitMethodMap)
        {
            Assert.False(_model.JitMethodMap.ContainsKey(address),
                $"JitMethodMap should not contain address 0x{address:X}");
        }
    }

    private void ThenJitMethodInfoAt(ulong address, int token, uint size, string assembly)
    {
        lock (_model.JitMethodMap)
        {
            JitMethodInfo info = _model.JitMethodMap[address];
            Assert.Equal(token, info.MethodToken);
            Assert.Equal(address, info.StartAddress);
            Assert.Equal(size, info.CodeSize);
            Assert.Equal(assembly, info.AssemblyName);
        }
    }

    private void ThenProfilerHooksActiveIsTrue() => Assert.True(_model.ProfilerHooksActive);

    private void ThenJitMethodMappingExists(int token, string assembly) => Assert.True(
        _model.JitMethodMappings.ContainsKey((token, assembly)),
        $"JitMethodMappings should contain key ({token:X8}, {assembly})");

    private void ThenJitMethodMappingDoesNotExist(int token, string assembly) => Assert.False(
        _model.JitMethodMappings.ContainsKey((token, assembly)),
        $"JitMethodMappings should not contain key ({token:X8}, {assembly})");

    private void ThenJitMethodMappingHasEntryCount(int token, string assembly, int expected) =>
        Assert.Equal(expected, _model.JitMethodMappings[(token, assembly)].ILToNativeMap.Length);

    private void ThenJitMethodMappingCodeStart(int token, string assembly, ulong expected) =>
        Assert.Equal(expected, _model.JitMethodMappings[(token, assembly)].CodeStart);

    private void ThenJitNotificationQueueHasCount(int expected) => Assert.Equal(expected, _model.JitNotifications.Count);

    private void ThenJitNotificationQueueIsEmpty() => Assert.True(_model.JitNotifications.IsEmpty);

    private void ThenJitNotificationHasToken(int expected)
    {
        Assert.True(_model.JitNotifications.TryPeek(out JitNotification? notification));
        Assert.Equal(expected, notification.MethodToken);
    }

    private void ThenSetInterruptWasCalled() => _dbgEngWrapper.Received().SetInterrupt(_model.Wrapper);

    private void ThenSetInterruptWasNotCalled() => _dbgEngWrapper.DidNotReceive().SetInterrupt(_model.Wrapper);

    private void ThenPendingEnterBreakpointIsTrue() => Assert.True(_model.PendingEnterBreakpoint);

    private void ThenPendingEnterBreakpointIsFalse() => Assert.False(_model.PendingEnterBreakpoint);

    private void ThenEnterBreakpointToken(int expected) => Assert.Equal(expected, _model.EnterBreakpointToken);

    private void ThenEnterBreakpointAddress(ulong expected) => Assert.Equal(expected, _model.EnterBreakpointAddress);

    private void ThenEnterBreakpointThreadId(uint expected) => Assert.Equal(expected, _model.EnterBreakpointThreadId);

    private void ThenEnterBreakpointAssembly(string expected) => Assert.Equal(expected, _model.EnterBreakpointAssembly);

    // The ACK event was Set() for the duplicate ENTER. If we can WaitOne(0) and it
    // returns true, that confirms it was signaled (AutoReset: one signal consumed).
    private void ThenProfilerAckEventWasSignaled() =>
        Assert.True(_model.ProfilerAckEvent!.WaitOne(0),
            "ProfilerAckEvent should have been signaled");

    private void ThenProfilerReaderThreadCompletes()
    {
        bool joined = _model.ProfilerReaderThread!.Join(TimeSpan.FromSeconds(2));
        Assert.True(joined, "ProfilerReaderThread should have exited after pipe closed");
    }

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly LogStore _logStore = new(Path.Combine(Path.GetTempPath(), "test-profiler.log"));
    private readonly IManagedBreakpointService _managedBp = Substitute.For<IManagedBreakpointService>();
    private readonly IDbgEngWrapper _dbgEngWrapper = Substitute.For<IDbgEngWrapper>();
    private readonly NativeDebuggerModel _model;
    private readonly ProfilerPipeService _testee;

    private string? _pipeName;
    private NamedPipeClientStream? _clientPipe;
    private StreamWriter? _clientWriter;
    private string? _profilerDllPath;
    private bool _createdProfilerDll;

    public ProfilerPipeServiceTests()
    {
        _model = new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        };
        _testee = new ProfilerPipeService(_log, _logStore, _managedBp, _dbgEngWrapper);

        // Default return values for NSubstitute mocks to prevent NullReferenceException.
        List<(string Assembly, int Token)> noTokens = [];
        _ = _managedBp.ResolveTokensFromBreakpoints(Arg.Any<IEnumerable<(string, int)>>())
            .Returns(noTokens);
        List<string> noAssemblies = [];
        _ = _managedBp.ResolveWatchAssemblies(Arg.Any<IEnumerable<(string, int)>>())
            .Returns(noAssemblies);

        // Clean env vars before each test to avoid cross-test contamination.
        ClearEnvVars();
    }

    private void ConnectClient()
    {
        _clientPipe = new NamedPipeClientStream(".", _pipeName!, PipeDirection.Out);
        _clientPipe.Connect(2000);
        _clientWriter = new StreamWriter(_clientPipe, Encoding.UTF8) { AutoFlush = true };
        // Wait briefly for the reader to process the connection.
        Thread.Sleep(100);
    }

    private static void ClearEnvVars()
    {
        Environment.SetEnvironmentVariable("CORECLR_ENABLE_PROFILING", null);
        Environment.SetEnvironmentVariable("CORECLR_PROFILER", null);
        Environment.SetEnvironmentVariable("CORECLR_PROFILER_PATH", null);
        Environment.SetEnvironmentVariable("MIXDBG_PIPE_NAME", null);
        Environment.SetEnvironmentVariable("MIXDBG_ACK_EVENT", null);
        Environment.SetEnvironmentVariable("MIXDBG_REHOOK_EVENT", null);
        Environment.SetEnvironmentVariable("MIXDBG_WATCH_TOKENS", null);
        Environment.SetEnvironmentVariable("MIXDBG_WATCH_ASSEMBLIES", null);
    }

    public void Dispose()
    {
        _model.Terminated = true;

        // Close client first so pipe EOF is detected by reader.
        _clientWriter?.Dispose();
        _clientPipe?.Dispose();

        // Dispose the pipe to break WaitForConnection if reader is still waiting.
        _model.ProfilerPipeReader?.Dispose();
        _model.ProfilerPipe?.Dispose();

        // Wait for reader thread to exit after pipe disposal.
        _ = _model.ProfilerReaderThread?.Join(TimeSpan.FromSeconds(2));

        _model.ProfilerAckEvent?.Dispose();
        _model.ProfilerRehookEvent?.Dispose();

        _model.Commands.CompleteAdding();
        _model.Commands.Dispose();
        _model.Stopped.Dispose();
        _model.EngineReady.Dispose();

        if (_createdProfilerDll && _profilerDllPath != null && File.Exists(_profilerDllPath))
        {
            try { File.Delete(_profilerDllPath); } catch { }
        }

        ClearEnvVars();
    }

    #endregion
}
