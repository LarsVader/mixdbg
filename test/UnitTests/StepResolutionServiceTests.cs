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

        ThenStopReasonIsNull();
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
    public void DetermineStopReason_WhenTempBpAtDeeperStack_SuppressesAndReturnsNull()
    {
        GivenActiveManagedStep(tempBpIds: [42], originStackPointer: 0x1000);
        GivenHitUserBreakpointWithBpId(42);
        GivenStackTraceReturns(stackOffset: 0x0F00);

        WhenDeterminingStopReason();

        ThenStopReasonIsNull();
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

        ThenStopReasonIsNull();
        ThenHitUserBreakpointIsCleared();
        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.StepOver);
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

    private void GivenStepOriginStackPointer(ulong rsp) => _model.StepOriginStackPointer = rsp;

    private void GivenStepOriginLocation(string file, int line) => _model.StepOriginLocation = (file, line);

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

    private void ThenStopReasonIsNull() => Assert.Null(_stopReasonResult);

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

    private StopReason? _stopReasonResult;
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
