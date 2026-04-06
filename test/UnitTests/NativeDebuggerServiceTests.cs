using MixDbg.Models.Dap;
using MixDbg.Models;
using MixDbg.Services;
using NSubstitute;

namespace MixDbg.Tests;

public sealed class NativeDebuggerServiceTests : IDisposable
{
    // ── CreateModel ────────────────────────────────────────

    [Fact]
    public void CreateModel_WhenCalled_ReturnsModelWithDisposeAction()
    {
        WhenCreatingModel();

        ThenCreatedModelIsNotNull();
        ThenCreatedModelDisposeActionIsSet();
    }

    [Fact]
    public void CreateModel_WhenDisposed_SetsTerminated()
    {
        WhenCreatingModel();
        WhenDisposingCreatedModel();

        ThenCreatedModelIsTerminated();
    }

    // ── ExecuteContinueOnEngine ──────────────────────────────

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_SetsConfigDone()
    {
        WhenExecutingContinueOnEngine();

        ThenConfigDoneIsTrue();
    }

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_CallsSetExecutionStatusGo()
    {
        WhenExecutingContinueOnEngine();

        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.Go);
    }

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_ClearsCachedStackTrace()
    {
        _model.CachedStackTraceResult = [new StackFrame { Id = 1 }];

        WhenExecutingContinueOnEngine();

        Assert.Null(_model.CachedStackTraceResult);
    }

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_ClearsVariables()
    {
        WhenExecutingContinueOnEngine();

        _wrapper.Received(1).ClearVariables(_model.Wrapper);
    }

    // ── Break ──────────────────────────────────────────────

    [Fact]
    public void Break_WhenCalled_SetsPauseRequested()
    {
        WhenBreaking();

        ThenPauseRequestedIsTrue();
    }

    [Fact]
    public void Break_WhenCalled_CallsSetInterrupt()
    {
        WhenBreaking();

        ThenSetInterruptWasCalled();
    }

    // ── ExecuteStepOnEngine ─────────────────────────────────

    [Fact]
    public void ExecuteStepOnEngine_WhenStepOver_CallsSetExecutionStatusStepOver()
    {
        WhenExecutingStepOnEngine(EngineExecutionStatus.StepOver);

        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.StepOver);
    }

    [Fact]
    public void ExecuteStepOnEngine_WhenStepInto_CallsSetExecutionStatusStepInto()
    {
        WhenExecutingStepOnEngine(EngineExecutionStatus.StepInto);

        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.StepInto);
    }

    [Fact]
    public void ExecuteStepOnEngine_WhenCalled_ClearsVariables()
    {
        WhenExecutingStepOnEngine(EngineExecutionStatus.StepOver);

        _wrapper.Received(1).ClearVariables(_model.Wrapper);
    }

    // ── ExecuteStepOutOnEngine ──────────────────────────────

    [Fact]
    public void ExecuteStepOutOnEngine_WhenCalled_CallsExecuteCommandGu()
    {
        WhenExecutingStepOutOnEngine();

        ThenExecuteCommandWasCalledWith("gu");
    }

    [Fact]
    public void ExecuteStepOutOnEngine_WhenCalled_ClearsVariables()
    {
        WhenExecutingStepOutOnEngine();

        _wrapper.Received(1).ClearVariables(_model.Wrapper);
    }

    // ── Terminate ──────────────────────────────────────────

    [Fact]
    public void Terminate_WhenCalled_SetsTerminated()
    {
        WhenTerminating();

        ThenModelIsTerminated();
    }

    [Fact]
    public void Terminate_WhenTargetNotExited_CallsTerminateSession()
    {
        WhenTerminating();

        ThenTerminateSessionWasCalled();
    }

    [Fact]
    public void Terminate_WhenCalled_QueuesWakeCommand()
    {
        WhenTerminating();

        ThenCommandWasQueued();
    }

    // ── Detach ─────────────────────────────────────────────

    [Fact]
    public void Detach_WhenCalled_SetsTerminated()
    {
        WhenDetaching();

        ThenModelIsTerminated();
    }

    [Fact]
    public void Detach_WhenCalled_CallsDetachSession()
    {
        WhenDetaching();

        ThenDetachSessionWasCalled();
    }

    // ── SetBreakpointsOnEngine (managed file) ──────────────

    [Fact]
    public void SetBreakpointsOnEngine_WhenManagedFile_ReturnsPendingVerifiedBreakpoints()
    {
        GivenSourceFileIsManaged(@"C:\src\Program.cs");
        GivenBreakpointRequest(@"C:\src\Program.cs", [10, 20]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(2);
        ThenAllBreakpointsAreVerified(true);
        ThenBreakpointsHaveMessage("Pending — managed debugger not yet initialized");
    }

    // ── SetBreakpointsOnEngine (native, offset resolved) ────

    [Fact]
    public void SetBreakpointsOnEngine_WhenOffsetResolved_CreatesDirectBreakpoint()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineSucceeds(@"C:\src\main.cpp", line: 42, offset: 0x1000);
        GivenAddCodeBreakpointSucceeds(bpId: 5);
        GivenGetLineByOffsetSucceeds(offset: 0x1000, resolvedLine: 42);
        GivenBreakpointRequest(@"C:\src\main.cpp", [42]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, true);
        ThenBreakpointAtIndexHasLine(0, 42);
        ThenBreakpointAtIndexHasId(0, 5);
        ThenUserBreakpointIdsContains(5);
    }

    // ── SetBreakpointsOnEngine (native, deferred via bu) ────

    [Fact]
    public void SetBreakpointsOnEngine_WhenOffsetFails_UsesDeferredBreakpoint()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineFails(@"C:\src\main.cpp", line: 99);
        GivenAddDeferredBreakpointSucceeds(deferredBpId: 7);
        GivenBreakpointRequest(@"C:\src\main.cpp", [99]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, true);
        ThenBreakpointAtIndexHasId(0, 7);
        ThenUserBreakpointIdsContains(7);
    }

    [Fact]
    public void SetBreakpointsOnEngine_WhenDeferredFails_ReturnsUnverified()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineFails(@"C:\src\main.cpp", line: 99);
        GivenAddDeferredBreakpointFails();
        GivenBreakpointRequest(@"C:\src\main.cpp", [99]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, false);
        ThenBreakpointsHaveMessage("Could not resolve source line");
    }

    // ── SetBreakpointsOnEngine (removes old breakpoints) ────

    [Fact]
    public void SetBreakpointsOnEngine_WhenCalledAgain_RemovesOldBreakpoints()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenExistingBreakpointForFile(@"C:\src\main.cpp", line: 10, bpId: 3);
        GivenGetOffsetByLineSucceeds(@"C:\src\main.cpp", line: 20, offset: 0x3000);
        GivenAddCodeBreakpointSucceeds(bpId: 8);
        GivenGetLineByOffsetSucceeds(offset: 0x3000, resolvedLine: 20);
        GivenBreakpointRequest(@"C:\src\main.cpp", [20]);

        WhenSettingBreakpointsOnEngine();

        ThenRemoveBreakpointWasCalled(3);
        ThenUserBreakpointIdsDoesNotContain(3);
        ThenUserBreakpointIdsContains(8);
    }

    // ── SetBreakpointsOnEngine (multiple breakpoints) ───────

    [Fact]
    public void SetBreakpointsOnEngine_WhenMultipleLines_ReturnsAllResults()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineSucceedsForAll(@"C:\src\main.cpp",
            [(10, 0x1000), (20, 0x2000), (30, 0x3000)]);
        GivenAddCodeBreakpointSucceedsMultiple([1, 2, 3]);
        GivenGetLineByOffsetSucceedsForAll(
            [(0x1000, 10), (0x2000, 20), (0x3000, 30)]);
        GivenBreakpointRequest(@"C:\src\main.cpp", [10, 20, 30]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(3);
        ThenAllBreakpointsAreVerified(true);
    }

    // ── GetThreadsOnEngine ──────────────────────────────────

    [Fact]
    public void GetThreadsOnEngine_WhenThreadsExist_ReturnsThreadArray()
    {
        GivenThreadsExist([(0u, 1000u), (1u, 1001u), (2u, 1002u)]);

        WhenGettingThreadsOnEngine();

        ThenThreadResultCountIs(3);
        ThenThreadAtIndexHasId(0, 0);
        ThenThreadAtIndexHasId(1, 1);
        ThenThreadAtIndexNameContains(0, "1000");
    }

    [Fact]
    public void GetThreadsOnEngine_WhenNoThreads_ReturnsDefaultThread()
    {
        GivenNoThreadsExist();

        WhenGettingThreadsOnEngine();

        ThenThreadResultCountIs(1);
        ThenThreadAtIndexNameContains(0, "Main Thread");
    }

    // ── GetScopesOnEngine ──────────────────────────────────

    [Fact]
    public void GetScopesOnEngine_WhenWrapperReturnsZero_ReturnsEmpty()
    {
        GivenSetScopeAndGetLocalsReturns(0);

        WhenGettingScopesOnEngine(frameId: 99);

        ThenScopeResultCountIs(0);
    }

    [Fact]
    public void GetScopesOnEngine_WhenWrapperReturnsRef_ReturnsLocalsScope()
    {
        GivenSetScopeAndGetLocalsReturns(42);

        WhenGettingScopesOnEngine(frameId: 1);

        ThenScopeResultCountIs(1);
        ThenScopeAtIndexHasName(0, "Locals");
        ThenScopeAtIndexHasVariablesReference(0, 42);
    }

    // ── GetVariablesOnEngine ────────────────────────────────

    [Fact]
    public void GetVariablesOnEngine_WhenWrapperReturnsEmpty_ReturnsEmpty()
    {
        GivenGetVariablesReturns([]);

        WhenGettingVariablesOnEngine(variablesReference: 999);

        ThenVariableResultCountIs(0);
    }

    [Fact]
    public void GetVariablesOnEngine_WhenWrapperReturnsVars_ReturnsMappedVariables()
    {
        GivenGetVariablesReturns([
            new VariableInfo("x", "42", "int", 0),
            new VariableInfo("y", "3.14", "float", 0),
        ]);

        WhenGettingVariablesOnEngine(variablesReference: 1);

        ThenVariableResultCountIs(2);
        ThenVariableAtIndexHasName(0, "x");
        ThenVariableAtIndexHasValue(0, "42");
        ThenVariableAtIndexHasType(0, "int");
        ThenVariableAtIndexHasName(1, "y");
        ThenVariableAtIndexHasValue(1, "3.14");
    }

    // ── GetStoppedThreadIdOnEngine ──────────────────────────

    [Fact]
    public void GetStoppedThreadIdOnEngine_WhenCalled_ReturnsEventThread()
    {
        GivenEventThreadId(42);

        WhenGettingStoppedThreadIdOnEngine();

        ThenStoppedThreadIdIs(42);
    }

    #region Given

    private void GivenSourceFileIsNative(string path)
    {
        _sourceFiles.IsNativeFile(path).Returns(true);
    }

    private void GivenSourceFileIsManaged(string path)
    {
        _sourceFiles.IsNativeFile(path).Returns(false);
    }

    private void GivenBreakpointRequest(string filePath, int[] lines)
    {
        _bpFilePath = filePath;
        _bpRequested = lines.Select(l => new SourceBreakpoint { Line = l }).ToArray();
    }

    private void GivenGetOffsetByLineSucceeds(string file, int line, ulong offset)
    {
        _wrapper.GetOffsetByLine(_model.Wrapper, (uint)line, file)
            .Returns((offset, true));
    }

    private void GivenGetOffsetByLineSucceedsForAll(string file, (int line, ulong offset)[] mappings)
    {
        _wrapper.GetOffsetByLine(_model.Wrapper, Arg.Any<uint>(), file)
            .Returns(ci =>
            {
                var reqLine = (int)(uint)ci[1];
                var match = mappings.FirstOrDefault(m => m.line == reqLine);
                return match != default ? (match.offset, true) : (0UL, false);
            });
    }

    private void GivenGetOffsetByLineFails(string file, int line)
    {
        _wrapper.GetOffsetByLine(_model.Wrapper, (uint)line, file)
            .Returns((0UL, false));
    }

    private void GivenAddCodeBreakpointSucceeds(uint bpId)
    {
        _wrapper.AddCodeBreakpoint(_model.Wrapper, Arg.Any<ulong>())
            .Returns((bpId, true));
    }

    private void GivenAddCodeBreakpointSucceedsMultiple(uint[] bpIds)
    {
        var idx = 0;
        _wrapper.AddCodeBreakpoint(_model.Wrapper, Arg.Any<ulong>())
            .Returns(_ => (bpIds[idx++], true));
    }

    private void GivenGetLineByOffsetSucceeds(ulong offset, int resolvedLine)
    {
        _wrapper.GetLineByOffset(_model.Wrapper, offset)
            .Returns(((uint)resolvedLine, ""));
    }

    private void GivenGetLineByOffsetSucceedsForAll((ulong offset, int line)[] mappings)
    {
        _wrapper.GetLineByOffset(_model.Wrapper, Arg.Any<ulong>())
            .Returns(ci =>
            {
                var reqOffset = (ulong)ci[1];
                var match = mappings.FirstOrDefault(m => m.offset == reqOffset);
                return match != default ? ((uint Line, string File)?)(((uint)match.line, "")) : null;
            });
    }

    private void GivenAddDeferredBreakpointSucceeds(uint deferredBpId)
    {
        _wrapper.AddDeferredBreakpoint(_model.Wrapper, Arg.Any<string>(), Arg.Any<int>())
            .Returns((deferredBpId, true));
    }

    private void GivenAddDeferredBreakpointFails()
    {
        _wrapper.AddDeferredBreakpoint(_model.Wrapper, Arg.Any<string>(), Arg.Any<int>())
            .Returns((0u, false));
    }

    private void GivenExistingBreakpointForFile(string filePath, int line, uint bpId)
    {
        _model.BreakpointIds[$"{filePath}:{line}"] = bpId;
        _model.UserBreakpointIds.Add(bpId);
    }

    private void GivenThreadsExist((uint engineId, uint systemId)[] threads)
    {
        _wrapper.GetThreads(_model.Wrapper).Returns(threads);
    }

    private void GivenNoThreadsExist()
    {
        _wrapper.GetThreads(_model.Wrapper).Returns([]);
    }

    private void GivenEventThreadId(uint threadId)
    {
        _wrapper.GetEventThreadId(_model.Wrapper).Returns(threadId);
    }

    private void GivenSetScopeAndGetLocalsReturns(int variablesReference)
    {
        _wrapper.SetScopeAndGetLocals(_model.Wrapper, Arg.Any<int>())
            .Returns(variablesReference);
    }

    private void GivenGetVariablesReturns(VariableInfo[] vars)
    {
        _wrapper.GetVariables(_model.Wrapper, Arg.Any<int>())
            .Returns(vars);
    }

    #endregion

    #region When

    private void WhenCreatingModel()
    {
        _createdModel = _testee.CreateModel();
    }

    private void WhenDisposingCreatedModel()
    {
        _createdModel!.Dispose();
    }

    private void WhenExecutingContinueOnEngine()
    {
        _testee.ExecuteContinueOnEngine(_model);
    }

    private void WhenBreaking()
    {
        _testee.Break(_model);
    }

    private void WhenExecutingStepOnEngine(EngineExecutionStatus stepKind)
    {
        _testee.ExecuteStepOnEngine(_model, stepKind);
    }

    private void WhenExecutingStepOutOnEngine()
    {
        _testee.ExecuteStepOutOnEngine(_model);
    }

    private void WhenTerminating()
    {
        _testee.Terminate(_model);
    }

    private void WhenDetaching()
    {
        _testee.Detach(_model);
    }

    private void WhenSettingBreakpointsOnEngine()
    {
        _bpResults = _testee.SetBreakpointsOnEngine(_model, _bpFilePath!, _bpRequested!);
    }

    private void WhenGettingThreadsOnEngine()
    {
        _threadResults = _testee.GetThreadsOnEngine(_model);
    }

    private void WhenGettingStoppedThreadIdOnEngine()
    {
        _stoppedThreadId = _testee.GetStoppedThreadIdOnEngine(_model);
    }

    private void WhenGettingScopesOnEngine(int frameId)
    {
        _scopeResults = _testee.GetScopesOnEngine(_model, frameId);
    }

    private void WhenGettingVariablesOnEngine(int variablesReference)
    {
        _variableResults = _testee.GetVariablesOnEngine(_model, variablesReference);
    }

    #endregion

    #region Then

    private void ThenCreatedModelIsNotNull()
    {
        Assert.NotNull(_createdModel);
    }

    private void ThenCreatedModelDisposeActionIsSet()
    {
        Assert.NotNull(_createdModel!.DisposeAction);
    }

    private void ThenCreatedModelIsTerminated()
    {
        Assert.True(_createdModel!.Terminated);
    }

    private void ThenCommandWasQueued()
    {
        Assert.True(_model.Commands.Count > 0);
    }

    private void ThenConfigDoneIsTrue()
    {
        Assert.True(_model.ConfigDone);
    }

    private void ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus status)
    {
        _wrapper.Received().SetExecutionStatus(_model.Wrapper, status);
    }

    private void ThenPauseRequestedIsTrue()
    {
        Assert.True(_model.PauseRequested);
    }

    private void ThenSetInterruptWasCalled()
    {
        _wrapper.Received(1).SetInterrupt(_model.Wrapper);
    }

    private void ThenExecuteCommandWasCalledWith(string command)
    {
        _wrapper.Received(1).ExecuteCommand(_model.Wrapper, command);
    }

    private void ThenModelIsTerminated()
    {
        Assert.True(_model.Terminated);
    }

    private void ThenTerminateSessionWasCalled()
    {
        _wrapper.Received(1).TerminateSession(_model.Wrapper);
    }

    private void ThenDetachSessionWasCalled()
    {
        _wrapper.Received(1).DetachSession(_model.Wrapper);
    }

    private void ThenBreakpointResultCountIs(int expected)
    {
        Assert.Equal(expected, _bpResults!.Length);
    }

    private void ThenAllBreakpointsAreVerified(bool expected)
    {
        Assert.All(_bpResults!, bp => Assert.Equal(expected, bp.Verified));
    }

    private void ThenBreakpointsHaveMessage(string expected)
    {
        Assert.All(_bpResults!, bp => Assert.Equal(expected, bp.Message));
    }

    private void ThenBreakpointAtIndexIsVerified(int index, bool expected)
    {
        Assert.Equal(expected, _bpResults![index].Verified);
    }

    private void ThenBreakpointAtIndexHasLine(int index, int expected)
    {
        Assert.Equal(expected, _bpResults![index].Line);
    }

    private void ThenBreakpointAtIndexHasId(int index, int expected)
    {
        Assert.Equal(expected, _bpResults![index].Id);
    }

    private void ThenUserBreakpointIdsContains(uint id)
    {
        Assert.Contains(id, _model.UserBreakpointIds);
    }

    private void ThenUserBreakpointIdsDoesNotContain(uint id)
    {
        Assert.DoesNotContain(id, _model.UserBreakpointIds);
    }

    private void ThenRemoveBreakpointWasCalled(uint bpId)
    {
        _wrapper.Received(1).RemoveBreakpoint(_model.Wrapper, bpId);
    }

    private void ThenThreadResultCountIs(int expected)
    {
        Assert.Equal(expected, _threadResults!.Length);
    }

    private void ThenThreadAtIndexHasId(int index, int expected)
    {
        Assert.Equal(expected, _threadResults![index].Id);
    }

    private void ThenThreadAtIndexNameContains(int index, string expected)
    {
        Assert.Contains(expected, _threadResults![index].Name);
    }

    private void ThenStoppedThreadIdIs(int expected)
    {
        Assert.Equal(expected, _stoppedThreadId);
    }

    private void ThenScopeResultCountIs(int expected)
    {
        Assert.Equal(expected, _scopeResults!.Length);
    }

    private void ThenScopeAtIndexHasName(int index, string expected)
    {
        Assert.Equal(expected, _scopeResults![index].Name);
    }

    private void ThenScopeAtIndexHasVariablesReference(int index, int expected)
    {
        Assert.Equal(expected, _scopeResults![index].VariablesReference);
    }

    private void ThenVariableResultCountIs(int expected)
    {
        Assert.Equal(expected, _variableResults!.Length);
    }

    private void ThenVariableAtIndexHasName(int index, string expected)
    {
        Assert.Equal(expected, _variableResults![index].Name);
    }

    private void ThenVariableAtIndexHasValue(int index, string expected)
    {
        Assert.Equal(expected, _variableResults![index].Value);
    }

    private void ThenVariableAtIndexHasType(int index, string expected)
    {
        Assert.Equal(expected, _variableResults![index].Type);
    }

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly ISourceFileService _sourceFiles = Substitute.For<ISourceFileService>();
    private readonly IManagedDebugger _managedDebugger = Substitute.For<IManagedDebugger>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly NativeDebuggerService _testee;

    private NativeDebuggerModel? _createdModel;
    private string? _bpFilePath;
    private SourceBreakpoint[]? _bpRequested;
    private Breakpoint[]? _bpResults;
    private DapThread[]? _threadResults;
    private Scope[]? _scopeResults;
    private Variable[]? _variableResults;
    private int _stoppedThreadId;

    public NativeDebuggerServiceTests()
    {
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new NativeDebuggerService(
            _server, _transport, _log, _logStore, _sourceFiles,
            _managedDebugger, Substitute.For<IProfilerPipeService>(), _wrapper);
        _model = new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        };
    }

    public void Dispose()
    {
        _model.Commands.CompleteAdding();
        _model.Commands.Dispose();
        _model.Stopped.Dispose();
        _model.EngineReady.Dispose();
    }

    #endregion
}
