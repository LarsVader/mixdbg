using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Threads;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class EngineQueryServiceTests : IDisposable
{
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

    private void GivenThreadsExist((uint engineId, uint systemId)[] threads) => _ = _wrapper.GetThreads(_model.Wrapper).Returns(threads);

    private void GivenNoThreadsExist() => _ = _wrapper.GetThreads(_model.Wrapper).Returns([]);

    private void GivenEventThreadId(uint threadId) => _ = _wrapper.GetEventThreadId(_model.Wrapper).Returns(threadId);

    private void GivenSetScopeAndGetLocalsReturns(int variablesReference) => _ = _wrapper.SetScopeAndGetLocals(_model.Wrapper, Arg.Any<int>())
            .Returns(variablesReference);

    private void GivenGetVariablesReturns(VariableInfo[] vars) => _ = _wrapper.GetVariables(_model.Wrapper, Arg.Any<int>())
            .Returns(vars);

    #endregion

    #region When

    private void WhenExecutingContinueOnEngine() => _testee.ExecuteContinueOnEngine(_model);

    private void WhenExecutingStepOnEngine(EngineExecutionStatus stepKind) => _testee.ExecuteStepOnEngine(_model, stepKind);

    private void WhenExecutingStepOutOnEngine() => _testee.ExecuteStepOutOnEngine(_model);

    private void WhenGettingThreadsOnEngine() => _threadResults = _testee.GetThreadsOnEngine(_model);

    private void WhenGettingStoppedThreadIdOnEngine() => _stoppedThreadId = _testee.GetStoppedThreadIdOnEngine(_model);

    private void WhenGettingScopesOnEngine(int frameId) => _scopeResults = _testee.GetScopesOnEngine(_model, frameId);

    private void WhenGettingVariablesOnEngine(int variablesReference) => _variableResults = _testee.GetVariablesOnEngine(_model, variablesReference);

    #endregion

    #region Then

    private void ThenConfigDoneIsTrue() => Assert.True(_model.ConfigDone);

    private void ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus status) => _wrapper.Received().SetExecutionStatus(_model.Wrapper, status);

    private void ThenExecuteCommandWasCalledWith(string command) => _ = _wrapper.Received(1).ExecuteCommand(_model.Wrapper, command);

    private void ThenThreadResultCountIs(int expected) => Assert.Equal(expected, _threadResults!.Length);

    private void ThenThreadAtIndexHasId(int index, int expected) => Assert.Equal(expected, _threadResults![index].Id);

    private void ThenThreadAtIndexNameContains(int index, string expected) => Assert.Contains(expected, _threadResults![index].Name);

    private void ThenStoppedThreadIdIs(int expected) => Assert.Equal(expected, _stoppedThreadId);

    private void ThenScopeResultCountIs(int expected) => Assert.Equal(expected, _scopeResults!.Length);

    private void ThenScopeAtIndexHasName(int index, string expected) => Assert.Equal(expected, _scopeResults![index].Name);

    private void ThenScopeAtIndexHasVariablesReference(int index, int expected) => Assert.Equal(expected, _scopeResults![index].VariablesReference);

    private void ThenVariableResultCountIs(int expected) => Assert.Equal(expected, _variableResults!.Length);

    private void ThenVariableAtIndexHasName(int index, string expected) => Assert.Equal(expected, _variableResults![index].Name);

    private void ThenVariableAtIndexHasValue(int index, string expected) => Assert.Equal(expected, _variableResults![index].Value);

    private void ThenVariableAtIndexHasType(int index, string expected) => Assert.Equal(expected, _variableResults![index].Type);

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly IManagedDebugger _managedDebugger = Substitute.For<IManagedDebugger>();
    private readonly IManagedBreakpointService _managedBp = Substitute.For<IManagedBreakpointService>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly EngineQueryService _testee;

    private DapThread[]? _threadResults;
    private Scope[]? _scopeResults;
    private Variable[]? _variableResults;
    private int _stoppedThreadId;

    public EngineQueryServiceTests()
    {
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new EngineQueryService(_log, _logStore, _managedDebugger, _managedBp, _wrapper);
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
