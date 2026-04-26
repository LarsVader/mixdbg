using MixDbg.Models;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class StepResolutionServiceTests
{
    // ── DetermineStopReason: simple stop reasons ──────────

    [Fact]
    public void DetermineStopReason_WhenHitUserBreakpoint_ReturnsBreakpoint()
    {
        GivenHitUserBreakpoint();

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Breakpoint);
    }

    [Fact]
    public void DetermineStopReason_WhenStepping_ReturnsStep()
    {
        GivenStepping();

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Step);
    }

    [Fact]
    public void DetermineStopReason_WhenPauseRequested_ReturnsPause()
    {
        GivenPauseRequested();

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Pause);
    }

    [Fact]
    public void DetermineStopReason_WhenNoFlags_ReturnsNull()
    {
        WhenDeterminingStopReason();

        ThenStopReasonIsContinue();
    }

    // ── DetermineStopReason: priority ─────────────────────

    [Fact]
    public void DetermineStopReason_WhenBreakpointAndStepping_BreakpointWins()
    {
        GivenHitUserBreakpoint();
        GivenStepping();

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Breakpoint);
    }

    [Fact]
    public void DetermineStopReason_WhenSteppingAndPause_StepWins()
    {
        GivenStepping();
        GivenPauseRequested();

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Step);
    }

    // ── DetermineStopReason: ActiveManagedStep ────────────

    [Fact]
    public void DetermineStopReason_WhenActiveManagedStepAndTempBpHit_ReturnsStep()
    {
        GivenActiveManagedStep(tempBpIds: [42]);
        GivenHitUserBreakpointWithBpId(42);

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Step);
    }

    [Fact]
    public void DetermineStopReason_WhenActiveManagedStepAndRealBpHit_ReturnsBreakpoint()
    {
        GivenActiveManagedStep(tempBpIds: [42]);
        GivenHitUserBreakpointWithBpId(99);

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Breakpoint);
    }

    [Fact]
    public void DetermineStopReason_WhenRealBpHitButOneShotExistsForDifferentMethod_ReturnsBreakpoint()
    {
        GivenActiveManagedStep(tempBpIds: [42]);
        GivenHitUserBreakpointWithBpId(99); // Real user BP, not temp.
        // A one-shot site exists for a different method — should NOT match.
        GivenOneShotSiteForDifferentMethod(installedBpId: 77);

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Breakpoint);
    }

    [Fact]
    public void DetermineStopReason_WhenTempBpAtDeeperStack_SuppressesAndReturnsNull()
    {
        GivenActiveManagedStep(tempBpIds: [42], originStackPointer: 0x1000);
        GivenHitUserBreakpointWithBpId(42);
        GivenStackTraceReturns(stackOffset: 0x0F00);

        WhenDeterminingStopReason();

        ThenStopReasonIsContinue();
        ThenHitUserBreakpointIsCleared();
    }

    [Fact]
    public void DetermineStopReason_WhenTempBpAtSameOrHigherStack_ReturnsStep()
    {
        GivenActiveManagedStep(tempBpIds: [42], originStackPointer: 0x1000);
        GivenHitUserBreakpointWithBpId(42);
        GivenStackTraceReturns(stackOffset: 0x1000);

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Step);
    }

    [Fact]
    public void DetermineStopReason_WhenActiveManagedStepAndStepping_CompletesStepAndFallsThrough()
    {
        GivenActiveManagedStep(tempBpIds: [42]);
        GivenStepping();

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Step);
        ThenActiveManagedStepIsCleared();
    }

    // ── DetermineStopReason: native BP suppression during step ────

    [Fact]
    public void DetermineStopReason_WhenBpDuringNativeStepAtDeeperStack_SuppressesAndReturnsNull()
    {
        GivenHitUserBreakpoint();
        GivenStepping();
        GivenStepOriginStackPointer(0x1000);
        GivenStackTraceReturns(stackOffset: 0x0F00);

        WhenDeterminingStopReason();

        ThenStopReasonIsContinue();
        ThenHitUserBreakpointIsCleared();
        // Stepping stays true so the caller knows to re-step instead of Go.
        Assert.True(_model.Stepping);
    }

    // ── DetermineStopReason: clears flags ─────────────────

    [Fact]
    public void DetermineStopReason_WhenStepping_ClearsStepping()
    {
        GivenStepping();

        WhenDeterminingStopReason();

        ThenSteppingIsCleared();
    }

    [Fact]
    public void DetermineStopReason_WhenPauseRequested_ClearsPauseRequested()
    {
        GivenPauseRequested();

        WhenDeterminingStopReason();

        ThenPauseRequestedIsCleared();
    }

    [Fact]
    public void DetermineStopReason_WhenBpHitDuringStep_ClearsStepping()
    {
        GivenHitUserBreakpoint();
        GivenStepping();

        WhenDeterminingStopReason();

        ThenStopReasonIs(StopReason.Breakpoint);
        ThenSteppingIsCleared();
    }

    [Fact]
    public void DetermineStopReason_WhenHitUserBreakpoint_ClearsHitUserBreakpoint()
    {
        GivenHitUserBreakpoint();

        WhenDeterminingStopReason();

        ThenHitUserBreakpointIsCleared();
    }

    // ── CompleteManagedStep ───────────────────────────────

    [Fact]
    public void CompleteManagedStep_RemovesTempBpsAndClearsState()
    {
        GivenActiveManagedStep(tempBpIds: [10, 20]);

        WhenCompletingManagedStep();

        ThenBreakpointWasRemoved(10);
        ThenBreakpointWasRemoved(20);
        ThenActiveManagedStepIsCleared();
        ThenStepOriginLocationIsCleared();
    }

    [Fact]
    public void CompleteManagedStep_WhenNoActiveStep_DoesNothing()
    {
        WhenCompletingManagedStep();

        ThenNoBreakpointsWereRemoved();
    }

    // ── CheckStepLanding ──────────────────────────────────

    [Fact]
    public void CheckStepLanding_WhenNoFrames_ReturnsNone()
    {
        GivenStackTraceReturnsEmpty();

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.None);
    }

    [Fact]
    public void CheckStepLanding_WhenDeeperStack_ReturnsReStep()
    {
        GivenStepOriginStackPointer(0x1000);
        GivenStackTraceReturns(stackOffset: 0x0F00, instructionOffset: 0x5000);
        GivenLineByOffsetReturns(10, "test.cs");

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.ReStep);
    }

    [Fact]
    public void CheckStepLanding_WhenNoSourceInfo_ReturnsStepOut()
    {
        GivenStackTraceReturns(instructionOffset: 0x5000);
        GivenLineByOffsetReturnsNull();

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.StepOut);
    }

    [Fact]
    public void CheckStepLanding_WhenSameLine_ReturnsReStep()
    {
        GivenStepOriginLocation("test.cs", 42);
        GivenStackTraceReturns(instructionOffset: 0x5000);
        GivenLineByOffsetReturns(42, "test.cs");

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.ReStep);
    }

    [Fact]
    public void CheckStepLanding_WhenDifferentLine_ReturnsNone()
    {
        GivenStepOriginLocation("test.cs", 42);
        GivenStackTraceReturns(instructionOffset: 0x5000);
        GivenLineByOffsetReturns(43, "test.cs");

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.None);
    }

    // ── Step-into: sourceless prologue re-step ────────────

    [Fact]
    public void CheckStepLanding_WhenStepIntoAndNoSourcePastOrigin_ReturnsReStep()
    {
        GivenStepOriginLocation("test.cpp", 29);
        GivenStepOriginKind(EngineExecutionStatus.StepInto);
        GivenStackTraceReturns(instructionOffset: 0x6000);
        GivenLineByOffsetReturnsNull();

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.ReStep);
    }

    [Fact]
    public void CheckStepLanding_WhenStepIntoAndHiddenSeqPointPastOrigin_ReturnsReStep()
    {
        GivenStepOriginLocation("test.cpp", 29);
        GivenStepOriginKind(EngineExecutionStatus.StepInto);
        GivenSourceFileCache("test.cpp", ["line1", "line2"]);  // 2 lines in file
        GivenStackTraceReturns(instructionOffset: 0x6000);
        GivenLineByOffsetReturns(15732480, "test.cpp");  // Hidden seq point (beyond file)

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.ReStep);
    }

    [Fact]
    public void CheckStepLanding_WhenStepIntoPastOrigin_SwitchesToStepOver()
    {
        GivenStepOriginLocation("test.cpp", 29);
        GivenStepOriginKind(EngineExecutionStatus.StepInto);
        GivenSourceFileCache("test.cpp", ["// line 1", "{"]); // line 2 = opening brace
        GivenStackTraceReturns(instructionOffset: 0x6000);
        GivenLineByOffsetReturns(2, "test.cpp");  // Opening brace — different line than origin.

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.ReStep);
        Assert.Equal(EngineExecutionStatus.StepOver, _model.StepOriginKind);
    }

    [Fact]
    public void CheckStepLanding_WhenStepIntoOnOriginLine_KeepsStepInto()
    {
        GivenStepOriginLocation("test.cpp", 29);
        GivenStepOriginKind(EngineExecutionStatus.StepInto);
        GivenStackTraceReturns(instructionOffset: 0x6000);
        GivenLineByOffsetReturns(29, "test.cpp");

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.ReStep);
        Assert.Equal(EngineExecutionStatus.StepInto, _model.StepOriginKind);
    }

    [Fact]
    public void CheckStepLanding_WhenStepIntoLandsOnRealStatementInCallee_ReturnsNone()
    {
        GivenStepOriginLocation("caller.cpp", 29);
        GivenStepOriginKind(EngineExecutionStatus.StepInto);
        GivenSourceFileCache("callee.cpp", ["int x = 0;", "return x;"]);
        GivenStackTraceReturns(instructionOffset: 0x7000);
        GivenLineByOffsetReturns(1, "callee.cpp");  // Real statement in callee

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.None);
        // Should have switched to StepOver since we're past origin
        Assert.Equal(EngineExecutionStatus.StepOver, _model.StepOriginKind);
    }

    [Fact]
    public void CheckStepLanding_WhenStepOverAndOpeningBrace_ReturnsReStep()
    {
        GivenStepOriginLocation("test.cpp", 10);
        GivenStepOriginKind(EngineExecutionStatus.StepOver);
        GivenSourceFileCache("test.cpp", ["if (x)", "{"]);  // line 2 = opening brace
        GivenStackTraceReturns(instructionOffset: 0x6000);
        GivenLineByOffsetReturns(2, "test.cpp");

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.ReStep);
    }

    [Fact]
    public void CheckStepLanding_WhenHiddenSeqPointWithoutOrigin_ReturnsStepOut()
    {
        GivenSourceFileCache("test.cpp", ["line1", "line2"]);  // 2 lines in file
        GivenStackTraceReturns(instructionOffset: 0x6000);
        GivenLineByOffsetReturns(15732480, "test.cpp");  // Beyond file length

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.StepOut);
    }

    [Fact]
    public void CheckStepLanding_WhenStepOverAndNoSourcePastOrigin_ReturnsStepOut()
    {
        GivenStepOriginLocation("test.cpp", 10);
        GivenStepOriginKind(EngineExecutionStatus.StepOver);
        GivenStackTraceReturns(instructionOffset: 0x6000);
        GivenLineByOffsetReturnsNull();

        WhenCheckingStepLanding();

        ThenStepAutoActionIs(StepAutoAction.StepOut);
    }

    // ── StopReason.ToDapString ────────────────────────────

    [Theory]
    [InlineData(StopReason.Breakpoint, "breakpoint")]
    [InlineData(StopReason.Step, "step")]
    [InlineData(StopReason.Pause, "pause")]
    public void ToDapString_ReturnsExpectedString(StopReason reason, string expected)
        => Assert.Equal(expected, reason.ToDapString());

    #region Given

    private void GivenHitUserBreakpoint() => _model.HitUserBreakpoint = true;

    private void GivenStepping() => _model.Stepping = true;

    private void GivenPauseRequested() => _model.PauseRequested = true;

    private void GivenHitUserBreakpointWithBpId(uint bpId)
    {
        _model.HitUserBreakpoint = true;
        _model.LastHitBpId = bpId;
    }

    private void GivenActiveManagedStep(uint[] tempBpIds, ulong originStackPointer = 0)
    {
        _model.ActiveManagedStep = new ManagedStepState { OriginStackPointer = originStackPointer };
        foreach (uint id in tempBpIds)
        {
            _model.ActiveManagedStep.TempBreakpointIds.Add(id);
            _ = _model.UserBreakpointIds.Add(id);
        }
    }

    private void GivenOneShotSiteForDifferentMethod(uint installedBpId)
    {
        ManagedMethodBreakpointPlan plan = new()
        {
            MethodToken = 0x06000099,
            AssemblyName = "OtherAssembly",
        };
        plan.Sites.Add(new MethodBreakpointSite
        {
            BpId = 1,
            ILOffset = 0,
            FilePath = "other.cs",
            Line = 10,
            IsStepIntoOneShot = true,
        });
        _model.ManagedBpPlans[(0x06000099, "OtherAssembly")] = plan;
        _model.BreakpointIds["other.cs:10"] = installedBpId;
    }

    private void GivenStepOriginStackPointer(ulong rsp) => _model.StepOriginStackPointer = rsp;

    private void GivenStepOriginLocation(string file, int line) => _model.StepOriginLocation = (file, line);

    private void GivenStepOriginKind(EngineExecutionStatus kind) => _model.StepOriginKind = kind;

    private void GivenSourceFileCache(string file, string[] lines) => _model.SourceFileCache[file] = lines;

    private void GivenStackTraceReturns(ulong stackOffset = 0x2000, ulong instructionOffset = 0x5000)
        => _ = _wrapper.GetStackTrace(_model.Wrapper, 1)
            .Returns([new NativeStackFrame(instructionOffset, stackOffset)]);

    private void GivenStackTraceReturnsEmpty()
        => _ = _wrapper.GetStackTrace(_model.Wrapper, 1)
            .Returns([]);

    private void GivenLineByOffsetReturns(uint line, string file)
        => _ = _wrapper.GetLineByOffset(_model.Wrapper, Arg.Any<ulong>())
            .Returns((line, file));

    private void GivenLineByOffsetReturnsNull()
        => _ = _wrapper.GetLineByOffset(_model.Wrapper, Arg.Any<ulong>())
            .Returns(((uint Line, string File)?)null);

    #endregion

    #region When

    private void WhenDeterminingStopReason() => _stopReasonResult = _testee.DetermineStopReason(_model);

    private void WhenCompletingManagedStep() => _testee.CompleteManagedStep(_model);

    private void WhenCheckingStepLanding() => _stepAutoActionResult = _testee.CheckStepLanding(_model);

    #endregion

    #region Then

    private void ThenStopReasonIs(StopReason expected) => Assert.Equal(expected, _stopReasonResult);

    private void ThenStopReasonIsContinue() => Assert.Equal(StopReason.Continue, _stopReasonResult);

    private void ThenStepAutoActionIs(StepAutoAction expected) => Assert.Equal(expected, _stepAutoActionResult);

    private void ThenHitUserBreakpointIsCleared() => Assert.False(_model.HitUserBreakpoint);

    private void ThenSteppingIsCleared() => Assert.False(_model.Stepping);

    private void ThenPauseRequestedIsCleared() => Assert.False(_model.PauseRequested);

    private void ThenActiveManagedStepIsCleared() => Assert.Null(_model.ActiveManagedStep);

    private void ThenStepOriginLocationIsCleared() => Assert.Null(_model.StepOriginLocation);

    private void ThenBreakpointWasRemoved(uint bpId)
        => _wrapper.Received(1).RemoveBreakpoint(_model.Wrapper, bpId);

    private void ThenNoBreakpointsWereRemoved()
        => _wrapper.DidNotReceive().RemoveBreakpoint(Arg.Any<DbgEngWrapperModel>(), Arg.Any<uint>());

    private void ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus status)
        => _wrapper.Received().SetExecutionStatus(_model.Wrapper, status);

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly StepResolutionService _testee;

    private StopReason _stopReasonResult;
    private StepAutoAction _stepAutoActionResult;

    public StepResolutionServiceTests()
    {
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test-step-resolution.log"));
        _model = new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        };
        _testee = new StepResolutionService(_log, _logStore, _wrapper);
    }

    #endregion
}
