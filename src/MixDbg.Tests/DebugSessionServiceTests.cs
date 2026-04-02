using MixDbg.Dap;
using MixDbg.Models;
using MixDbg.Services;
using NSubstitute;

namespace MixDbg.Tests;

public sealed class DebugSessionServiceTests
{
    [Fact]
    public void CreateModel_WhenCalled_ReturnsUninitializedSession()
    {
        WhenCreatingModel();

        ThenSessionStateIs(SessionState.Uninitialized);
    }

    [Fact]
    public void Initialize_WhenCalled_SetsStateToInitialized()
    {
        WhenInitializing();

        ThenSessionStateIs(SessionState.Initialized);
    }

    [Fact]
    public void Initialize_WhenCalled_SendsInitializedEvent()
    {
        WhenInitializing();

        ThenInitializedEventWasSent();
    }

    [Fact]
    public void Initialize_WhenCalled_ReturnsCapabilities()
    {
        WhenInitializing();

        ThenCapabilitiesSupportsConfigurationDone();
        ThenCapabilitiesSupportsTerminate();
        ThenCapabilitiesSupportsEvaluateForHovers();
    }

    [Fact]
    public void Launch_WhenCalled_CreatesEngineAndLaunches()
    {
        GivenLaunchArgs("C:/app/test.exe", cwd: "C:/app");

        WhenLaunching();

        ThenEngineWasCreated();
        ThenNativeDebuggerLaunchWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Launch_WhenNoExplicitCwd_UsesProgDir()
    {
        GivenLaunchArgs("C:/app/bin/test.exe", cwd: null);

        WhenLaunching();

        ThenNativeDebuggerLaunchWasCalledWithCwd("C:/app/bin");
    }

    [Fact]
    public void Launch_WhenSymbolPathProvided_JoinsAndPasses()
    {
        GivenLaunchArgsWithSymbolPath("C:/app/test.exe", ["C:/symbols", "C:/other"]);

        WhenLaunching();

        ThenNativeDebuggerLaunchWasCalledWithSymbolPathContaining("C:/symbols");
        ThenNativeDebuggerLaunchWasCalledWithSymbolPathContaining("C:/other");
    }

    [Fact]
    public void Attach_WhenPidProvided_AttachesToProcess()
    {
        GivenAttachArgs(pid: 1234);

        WhenAttaching();

        ThenEngineWasCreated();
        ThenNativeDebuggerAttachWasCalledWithPid(1234);
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Attach_WhenNoPid_ThrowsInvalidOperation()
    {
        GivenAttachArgs(pid: null);

        WhenAttachingExpectingException();

        ThenInvalidOperationExceptionWasThrown();
    }

    [Fact]
    public void SetBreakpoints_WhenNoEngine_StoresPending()
    {
        GivenBreakpointsArgs("C:/src/main.cpp", [10, 20]);

        WhenSettingBreakpoints();

        ThenPendingBreakpointsCountIs(1);
        ThenBreakpointResponseCountIs(2);
    }

    [Fact]
    public void SetBreakpoints_WhenNoEngine_NativeFileBreakpointsAreVerified()
    {
        GivenSourceFileIsNative("C:/src/main.cpp");
        GivenBreakpointsArgs("C:/src/main.cpp", [10]);

        WhenSettingBreakpoints();

        ThenBreakpointAtIndexIsVerified(0, true);
    }

    [Fact]
    public void SetBreakpoints_WhenNoEngine_ManagedFileBreakpointsAreNotVerified()
    {
        GivenSourceFileIsManaged("C:/src/Program.cs");
        GivenBreakpointsArgs("C:/src/Program.cs", [10]);

        WhenSettingBreakpoints();

        ThenBreakpointAtIndexIsVerified(0, false);
    }

    [Fact]
    public void SetBreakpoints_WhenEngineReady_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();
        GivenBreakpointsArgs("C:/src/main.cpp", [10]);
        GivenNativeDebuggerReturnsBreakpoints();

        WhenSettingBreakpoints();

        ThenNativeDebuggerSetBreakpointsWasCalled();
    }

    [Fact]
    public void ConfigurationDone_WhenNoEngine_SetsConfiguredState()
    {
        WhenCallingConfigurationDone();

        ThenSessionStateIs(SessionState.Configured);
    }

    [Fact]
    public void ConfigurationDone_WhenEngineAndPendingBreakpoints_AppliesThem()
    {
        GivenAnEngineIsRunning();
        GivenPendingBreakpointsExist("C:/src/main.cpp", [10]);
        GivenNativeDebuggerReturnsBreakpoints();

        WhenCallingConfigurationDone();

        ThenNativeDebuggerSetBreakpointsWasCalled();
        ThenNativeDebuggerContinueWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void ConfigurationDone_WhenEngineAndPendingBreakpoints_SendsBreakpointEvents()
    {
        GivenAnEngineIsRunning();
        GivenPendingBreakpointsExist("C:/src/main.cpp", [10]);
        GivenNativeDebuggerReturnsBreakpoints();

        WhenCallingConfigurationDone();

        ThenBreakpointEventWasSent();
    }

    [Fact]
    public void Continue_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();

        WhenContinuing();

        ThenNativeDebuggerContinueWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Continue_WhenNoEngine_DoesNothing()
    {
        WhenContinuing();

        ThenNativeDebuggerContinueWasNotCalled();
    }

    [Fact]
    public void StepOver_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();

        WhenSteppingOver();

        ThenNativeDebuggerStepOverWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void StepInto_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();

        WhenSteppingInto();

        ThenNativeDebuggerStepIntoWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void StepOut_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();

        WhenSteppingOut();

        ThenNativeDebuggerStepOutWasCalled();
        ThenSessionStateIs(SessionState.Running);
    }

    [Fact]
    public void Pause_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();

        WhenPausing();

        ThenNativeDebuggerBreakWasCalled();
    }

    [Fact]
    public void GetStackTrace_WhenNoEngine_ReturnsEmpty()
    {
        GivenStackTraceArgs(levels: 20);

        WhenGettingStackTrace();

        ThenStackFrameCountIs(0);
    }

    [Fact]
    public void GetStackTrace_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();
        GivenStackTraceArgs(levels: 20);
        GivenNativeDebuggerReturnsFrames(3);

        WhenGettingStackTrace();

        ThenStackFrameCountIs(3);
        ThenTotalFramesIs(3);
    }

    [Fact]
    public void GetStackTrace_WhenZeroLevels_DefaultsTo50()
    {
        GivenAnEngineIsRunning();
        GivenStackTraceArgs(levels: 0);
        GivenNativeDebuggerReturnsFrames(2);

        WhenGettingStackTrace();

        ThenNativeDebuggerGetStackTraceWasCalledWithMaxFrames(50);
    }

    [Fact]
    public void GetThreads_WhenNoEngine_ReturnsDefaultThread()
    {
        WhenGettingThreads();

        ThenThreadCountIs(1);
        ThenFirstThreadNameIs("Main Thread");
    }

    [Fact]
    public void GetThreads_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();
        GivenNativeDebuggerReturnsThreads(2);

        WhenGettingThreads();

        ThenThreadCountIs(2);
    }

    // ── GetScopes ──────────────────────────────────────────

    [Fact]
    public void GetScopes_WhenNoEngine_ReturnsEmpty()
    {
        GivenScopesArgs(frameId: 1);

        WhenGettingScopes();

        ThenScopeCountIs(0);
    }

    [Fact]
    public void GetScopes_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();
        GivenScopesArgs(frameId: 1);
        GivenNativeDebuggerReturnsScopes(1);

        WhenGettingScopes();

        ThenScopeCountIs(1);
        ThenNativeDebuggerGetScopesWasCalled();
    }

    // ── GetVariables ────────────────────────────────────────

    [Fact]
    public void GetVariables_WhenNoEngine_ReturnsEmpty()
    {
        GivenVariablesArgs(variablesReference: 1);

        WhenGettingVariables();

        ThenVariableCountIs(0);
    }

    [Fact]
    public void GetVariables_WhenEngineExists_DelegatesToNativeDebugger()
    {
        GivenAnEngineIsRunning();
        GivenVariablesArgs(variablesReference: 1);
        GivenNativeDebuggerReturnsVariables(3);

        WhenGettingVariables();

        ThenVariableCountIs(3);
        ThenNativeDebuggerGetVariablesWasCalled();
    }

    [Fact]
    public void Disconnect_WhenTerminateTrue_TerminatesEngine()
    {
        GivenAnEngineIsRunning();
        GivenDisconnectArgs(terminateDebuggee: true);

        WhenDisconnecting();

        ThenNativeDebuggerTerminateWasCalled();
        ThenSessionStateIs(SessionState.Terminated);
    }

    [Fact]
    public void Disconnect_WhenTerminateFalse_DetachesEngine()
    {
        GivenAnEngineIsRunning();
        GivenDisconnectArgs(terminateDebuggee: false);

        WhenDisconnecting();

        ThenNativeDebuggerDetachWasCalled();
        ThenSessionStateIs(SessionState.Terminated);
    }

    [Fact]
    public void Disconnect_WhenNoEngine_SetsTerminatedState()
    {
        GivenDisconnectArgs(terminateDebuggee: true);

        WhenDisconnecting();

        ThenSessionStateIs(SessionState.Terminated);
    }

    #region Given

    private void GivenLaunchArgs(string program, string? cwd)
    {
        _launchArgs = new LaunchRequestArguments { Program = program, Cwd = cwd };
    }

    private void GivenLaunchArgsWithSymbolPath(string program, string[] symbolPath)
    {
        _launchArgs = new LaunchRequestArguments { Program = program, SymbolPath = symbolPath };
    }

    private void GivenAttachArgs(int? pid)
    {
        _attachArgs = new AttachRequestArguments { Pid = pid };
    }

    private void GivenBreakpointsArgs(string filePath, int[] lines)
    {
        _breakpointArgs = new SetBreakpointsArguments
        {
            Source = new Source { Path = filePath },
            Breakpoints = lines.Select(l => new SourceBreakpoint { Line = l }).ToArray(),
        };
    }

    private void GivenStackTraceArgs(int levels)
    {
        _stackTraceArgs = new StackTraceArguments { Levels = levels };
    }

    private void GivenDisconnectArgs(bool terminateDebuggee)
    {
        _disconnectArgs = new DisconnectArguments { TerminateDebuggee = terminateDebuggee };
    }

    private void GivenAnEngineIsRunning()
    {
        _session.Engine = _engineModel;
    }

    private void GivenSourceFileIsNative(string path)
    {
        _sourceFiles.IsNativeFile(path).Returns(true);
    }

    private void GivenSourceFileIsManaged(string path)
    {
        _sourceFiles.IsNativeFile(path).Returns(false);
    }

    private void GivenNativeDebuggerReturnsBreakpoints()
    {
        _nativeDebugger.SetBreakpoints(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Any<SourceBreakpoint[]>())
            .Returns(ci =>
            {
                var bps = ci.ArgAt<SourceBreakpoint[]>(2);
                return bps.Select((bp, i) => new Breakpoint
                {
                    Id = i + 1,
                    Verified = true,
                    Line = bp.Line,
                }).ToArray();
            });
    }

    private void GivenNativeDebuggerReturnsFrames(int count)
    {
        _nativeDebugger.GetStackTrace(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns(Enumerable.Range(1, count).Select(i => new StackFrame
            {
                Id = i,
                Name = $"frame{i}",
            }).ToArray());
    }

    private void GivenNativeDebuggerReturnsThreads(int count)
    {
        _nativeDebugger.GetThreads(Arg.Any<NativeDebuggerModel>())
            .Returns(Enumerable.Range(1, count).Select(i => new DapThread
            {
                Id = i,
                Name = $"Thread {i}",
            }).ToArray());
    }

    private void GivenScopesArgs(int frameId)
    {
        _scopesArgs = new ScopesArguments { FrameId = frameId };
    }

    private void GivenVariablesArgs(int variablesReference)
    {
        _variablesArgs = new VariablesArguments { VariablesReference = variablesReference };
    }

    private void GivenNativeDebuggerReturnsScopes(int count)
    {
        _nativeDebugger.GetScopes(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns(Enumerable.Range(1, count).Select(i => new Scope
            {
                Name = $"Scope{i}",
                VariablesReference = i,
            }).ToArray());
    }

    private void GivenNativeDebuggerReturnsVariables(int count)
    {
        _nativeDebugger.GetVariables(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>())
            .Returns(Enumerable.Range(1, count).Select(i => new Variable
            {
                Name = $"var{i}",
                Value = $"{i}",
                VariablesReference = 0,
            }).ToArray());
    }

    private void GivenPendingBreakpointsExist(string filePath, int[] lines)
    {
        _session.PendingBreakpoints.Add(new SetBreakpointsArguments
        {
            Source = new Source { Path = filePath },
            Breakpoints = lines.Select(l => new SourceBreakpoint { Line = l }).ToArray(),
        });
    }

    #endregion

    #region When

    private void WhenCreatingModel()
    {
        _createdSession = _testee.CreateModel();
    }

    private void WhenInitializing()
    {
        _capabilities = _testee.Initialize(_session, new InitializeRequestArguments());
    }

    private void WhenLaunching()
    {
        _testee.Launch(_session, _launchArgs!);
    }

    private void WhenAttaching()
    {
        _testee.Attach(_session, _attachArgs!);
    }

    private void WhenAttachingExpectingException()
    {
        try { _testee.Attach(_session, _attachArgs!); }
        catch (Exception ex) { _thrownException = ex; }
    }

    private void WhenSettingBreakpoints()
    {
        _breakpointResponse = _testee.SetBreakpoints(_session, _breakpointArgs!);
    }

    private void WhenCallingConfigurationDone()
    {
        _testee.ConfigurationDone(_session);
    }

    private void WhenContinuing()
    {
        _testee.Continue(_session);
    }

    private void WhenSteppingOver()
    {
        _testee.StepOver(_session);
    }

    private void WhenSteppingInto()
    {
        _testee.StepInto(_session);
    }

    private void WhenSteppingOut()
    {
        _testee.StepOut(_session);
    }

    private void WhenPausing()
    {
        _testee.Pause(_session);
    }

    private void WhenGettingStackTrace()
    {
        _stackTraceResponse = _testee.GetStackTrace(_session, _stackTraceArgs!);
    }

    private void WhenGettingThreads()
    {
        _threadsResponse = _testee.GetThreads(_session);
    }

    private void WhenDisconnecting()
    {
        _testee.Disconnect(_session, _disconnectArgs!);
    }

    private void WhenGettingScopes()
    {
        _scopesResponse = _testee.GetScopes(_session, _scopesArgs!);
    }

    private void WhenGettingVariables()
    {
        _variablesResponse = _testee.GetVariables(_session, _variablesArgs!);
    }

    #endregion

    #region Then

    private void ThenSessionStateIs(SessionState expected)
    {
        var session = _createdSession ?? _session;
        Assert.Equal(expected, session.State);
    }

    private void ThenInitializedEventWasSent()
    {
        _server.Received(1).SendEvent(_transport, "initialized", Arg.Any<InitializedEventBody>());
    }

    private void ThenCapabilitiesSupportsConfigurationDone()
    {
        Assert.True(_capabilities!.SupportsConfigurationDoneRequest);
    }

    private void ThenCapabilitiesSupportsTerminate()
    {
        Assert.True(_capabilities!.SupportsTerminateRequest);
    }

    private void ThenCapabilitiesSupportsEvaluateForHovers()
    {
        Assert.True(_capabilities!.SupportsEvaluateForHovers);
    }

    private void ThenEngineWasCreated()
    {
        _nativeDebugger.Received(1).CreateModel();
    }

    private void ThenNativeDebuggerLaunchWasCalled()
    {
        _nativeDebugger.Received(1).Launch(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    private void ThenNativeDebuggerLaunchWasCalledWithCwd(string expected)
    {
        _nativeDebugger.Received(1).Launch(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Is<string?>(s => s != null && Path.GetFullPath(s) == Path.GetFullPath(expected)),
            Arg.Any<string?>());
    }

    private void ThenNativeDebuggerLaunchWasCalledWithSymbolPathContaining(string expected)
    {
        _nativeDebugger.Received(1).Launch(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(s => s != null && s.Contains(expected)));
    }

    private void ThenNativeDebuggerAttachWasCalledWithPid(uint expected)
    {
        _nativeDebugger.Received(1).Attach(
            Arg.Any<NativeDebuggerModel>(),
            expected,
            Arg.Any<string?>());
    }

    private void ThenInvalidOperationExceptionWasThrown()
    {
        Assert.IsType<InvalidOperationException>(_thrownException);
    }

    private void ThenPendingBreakpointsCountIs(int expected)
    {
        Assert.Equal(expected, _session.PendingBreakpoints.Count);
    }

    private void ThenBreakpointResponseCountIs(int expected)
    {
        Assert.Equal(expected, _breakpointResponse!.Breakpoints.Length);
    }

    private void ThenBreakpointAtIndexIsVerified(int index, bool expected)
    {
        Assert.Equal(expected, _breakpointResponse!.Breakpoints[index].Verified);
    }

    private void ThenNativeDebuggerSetBreakpointsWasCalled()
    {
        _nativeDebugger.Received().SetBreakpoints(
            Arg.Any<NativeDebuggerModel>(),
            Arg.Any<string>(),
            Arg.Any<SourceBreakpoint[]>());
    }

    private void ThenNativeDebuggerContinueWasCalled()
    {
        _nativeDebugger.Received().Continue(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerContinueWasNotCalled()
    {
        _nativeDebugger.DidNotReceive().Continue(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerStepOverWasCalled()
    {
        _nativeDebugger.Received(1).StepOver(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerStepIntoWasCalled()
    {
        _nativeDebugger.Received(1).StepInto(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerStepOutWasCalled()
    {
        _nativeDebugger.Received(1).StepOut(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerBreakWasCalled()
    {
        _nativeDebugger.Received(1).Break(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenStackFrameCountIs(int expected)
    {
        Assert.Equal(expected, _stackTraceResponse!.StackFrames.Length);
    }

    private void ThenTotalFramesIs(int expected)
    {
        Assert.Equal(expected, _stackTraceResponse!.TotalFrames);
    }

    private void ThenNativeDebuggerGetStackTraceWasCalledWithMaxFrames(int expected)
    {
        _nativeDebugger.Received(1).GetStackTrace(Arg.Any<NativeDebuggerModel>(), expected);
    }

    private void ThenThreadCountIs(int expected)
    {
        Assert.Equal(expected, _threadsResponse!.Threads.Length);
    }

    private void ThenFirstThreadNameIs(string expected)
    {
        Assert.Equal(expected, _threadsResponse!.Threads[0].Name);
    }

    private void ThenNativeDebuggerTerminateWasCalled()
    {
        _nativeDebugger.Received(1).Terminate(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenNativeDebuggerDetachWasCalled()
    {
        _nativeDebugger.Received(1).Detach(Arg.Any<NativeDebuggerModel>());
    }

    private void ThenBreakpointEventWasSent()
    {
        _server.Received().SendEvent(_transport, "breakpoint", Arg.Any<BreakpointEventBody>());
    }

    private void ThenScopeCountIs(int expected)
    {
        Assert.Equal(expected, _scopesResponse!.Scopes.Length);
    }

    private void ThenNativeDebuggerGetScopesWasCalled()
    {
        _nativeDebugger.Received(1).GetScopes(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>());
    }

    private void ThenVariableCountIs(int expected)
    {
        Assert.Equal(expected, _variablesResponse!.Variables.Length);
    }

    private void ThenNativeDebuggerGetVariablesWasCalled()
    {
        _nativeDebugger.Received(1).GetVariables(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>());
    }

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ISourceFileService _sourceFiles = Substitute.For<ISourceFileService>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly INativeDebugger _nativeDebugger = Substitute.For<INativeDebugger>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly DebugSessionModel _session = new();
    private readonly NativeDebuggerModel _engineModel = new();
    private readonly DebugSessionServiceTests _this;
    private readonly DebugSessionService _testee;

    private DebugSessionModel? _createdSession;
    private Capabilities? _capabilities;
    private LaunchRequestArguments? _launchArgs;
    private AttachRequestArguments? _attachArgs;
    private SetBreakpointsArguments? _breakpointArgs;
    private SetBreakpointsResponseBody? _breakpointResponse;
    private StackTraceArguments? _stackTraceArgs;
    private StackTraceResponseBody? _stackTraceResponse;
    private ThreadsResponseBody? _threadsResponse;
    private ScopesArguments? _scopesArgs;
    private ScopesResponseBody? _scopesResponse;
    private VariablesArguments? _variablesArgs;
    private VariablesResponseBody? _variablesResponse;
    private DisconnectArguments? _disconnectArgs;
    private Exception? _thrownException;

    public DebugSessionServiceTests()
    {
        _this = this;
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _nativeDebugger.CreateModel().Returns(_engineModel);
        _testee = new DebugSessionService(_server, _transport, _sourceFiles, _log, _logStore, _nativeDebugger);
    }

    #endregion
}
