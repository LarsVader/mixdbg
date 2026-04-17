using MixDbg.Engine.DbgEng;
using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class SteppingServiceTests : IDisposable
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

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_ClearsManagedVariables()
    {
        WhenExecutingContinueOnEngine();

        _corDebug.Received(1).ClearManagedVariables(_model.CorWrapper);
    }

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_SetsLastContinuedBpId()
    {
        _model.LastHitBpId = 7;

        WhenExecutingContinueOnEngine();

        Assert.Equal(7u, _model.LastContinuedBpId);
    }

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_SetsContinueTimestamp()
    {
        WhenExecutingContinueOnEngine();

        Assert.True(_model.ContinueTimestampTicks > 0);
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

    [Fact]
    public void ExecuteStepOnEngine_WhenCalled_ClearsManagedVariables()
    {
        WhenExecutingStepOnEngine(EngineExecutionStatus.StepOver);

        _corDebug.Received(1).ClearManagedVariables(_model.CorWrapper);
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

    [Fact]
    public void ExecuteStepOutOnEngine_WhenCalled_ClearsManagedVariables()
    {
        WhenExecutingStepOutOnEngine();

        _corDebug.Received(1).ClearManagedVariables(_model.CorWrapper);
    }

    // ── Managed step-over ────────────────────────────────────

    [Fact]
    public void ExecuteStepOnEngine_WhenManagedFrame_SetsActiveManagedStep()
    {
        // Current IP 0x5010 → native offset 0x10 → IL offset 10.
        // Next SP: IL 20 → native offset 0x20 → address 0x5020.
        GivenManagedMethodInJitMap(0x5000, tokenHex: "06000001", assemblyName: "App");
        GivenStackFramesForStep([new NativeStackFrame(StackOffset: 0, InstructionOffset:0x5010), new NativeStackFrame(StackOffset: 0, InstructionOffset:0x3000)]);
        GivenAssemblyPathForMethod("App", @"C:\src\App.dll");
        GivenILToNativeMapping(0x06000001, "App", codeStart: 0x5000, [(0, 0), (10, 0x10), (20, 0x20)]);
        GivenSequencePoints(@"C:\src\App.dll", 0x06000001, [(0, @"C:\src\App.cs", 10), (10, @"C:\src\App.cs", 11), (20, @"C:\src\App.cs", 12)]);
        GivenAddHardwareBreakpointSucceeds(0x5020, bpId: 99);

        WhenExecutingStepOnEngine(EngineExecutionStatus.StepOver);

        Assert.NotNull(_model.ActiveManagedStep);
        Assert.Contains(99u, _model.ActiveManagedStep!.TempBreakpointIds);
        Assert.Contains(99u, _model.UserBreakpointIds);
    }

    [Fact]
    public void ExecuteStepOnEngine_WhenManagedFrame_CallsGoNotStepOver()
    {
        GivenManagedMethodInJitMap(0x5000, tokenHex: "06000001", assemblyName: "App");
        GivenStackFramesForStep([new NativeStackFrame(StackOffset: 0, InstructionOffset:0x5010), new NativeStackFrame(StackOffset: 0, InstructionOffset:0x3000)]);
        GivenAssemblyPathForMethod("App", @"C:\src\App.dll");
        GivenILToNativeMapping(0x06000001, "App", codeStart: 0x5000, [(0, 0), (10, 0x10), (20, 0x20)]);
        GivenSequencePoints(@"C:\src\App.dll", 0x06000001, [(0, @"C:\src\App.cs", 10), (10, @"C:\src\App.cs", 11), (20, @"C:\src\App.cs", 12)]);
        GivenAddHardwareBreakpointSucceeds(0x5020, bpId: 99);

        WhenExecutingStepOnEngine(EngineExecutionStatus.StepOver);

        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.Go);
    }

    [Fact]
    public void ExecuteStepOnEngine_WhenNativeFrame_UsesDbgEngStepOver()
    {
        // No JIT method map entries → native step.
        WhenExecutingStepOnEngine(EngineExecutionStatus.StepOver);

        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.StepOver);
    }

    [Fact]
    public void ExecuteStepOnEngine_WhenManagedEndOfMethod_SetsReturnAddressBp()
    {
        // IL offset 20 is at the current position; no next sequence point after it.
        GivenManagedMethodInJitMap(0x5000, tokenHex: "06000001", assemblyName: "App");
        GivenStackFramesForStep([new NativeStackFrame(StackOffset: 0, InstructionOffset:0x5020), new NativeStackFrame(StackOffset: 0, InstructionOffset:0x3000)]);
        GivenAssemblyPathForMethod("App", @"C:\src\App.dll");
        GivenILToNativeMapping(0x06000001, "App", codeStart: 0x5000, [(0, 0), (10, 0x10), (20, 0x20)]);
        GivenSequencePoints(@"C:\src\App.dll", 0x06000001, [(0, @"C:\src\App.cs", 10), (10, @"C:\src\App.cs", 11), (20, @"C:\src\App.cs", 12)]);
        GivenAddHardwareBreakpointSucceeds(0x3000, bpId: 50);

        WhenExecutingStepOnEngine(EngineExecutionStatus.StepOver);

        // Should set BP at caller return address (0x3000), not at next IL offset.
        Assert.NotNull(_model.ActiveManagedStep);
        Assert.Contains(50u, _model.ActiveManagedStep!.TempBreakpointIds);
    }

    // ── Managed step-out ───────────────────────────────────

    [Fact]
    public void ExecuteStepOutOnEngine_WhenManagedFrame_SetsBpAtReturnAddress()
    {
        GivenManagedMethodInJitMap(0x5000, tokenHex: "06000001", assemblyName: "App");
        GivenStackFramesForStep([new NativeStackFrame(StackOffset: 0, InstructionOffset:0x5010), new NativeStackFrame(StackOffset: 0, InstructionOffset:0x3000)]);
        GivenAddHardwareBreakpointSucceeds(0x3000, bpId: 77);

        WhenExecutingStepOutOnEngine();

        Assert.NotNull(_model.ActiveManagedStep);
        Assert.Contains(77u, _model.ActiveManagedStep!.TempBreakpointIds);
        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.Go);
    }

    [Fact]
    public void ExecuteStepOutOnEngine_WhenNativeFrame_UsesGuCommand()
    {
        // No JIT method map → native.
        WhenExecutingStepOutOnEngine();

        ThenExecuteCommandWasCalledWith("gu");
    }

    // ── Managed step cancel on continue ─────────────────────

    [Fact]
    public void ExecuteContinueOnEngine_WhenActiveManagedStep_RemovesTempBps()
    {
        GivenActiveManagedStep(bpIds: [10, 11]);
        _ = _model.UserBreakpointIds.Add(10);
        _ = _model.UserBreakpointIds.Add(11);

        WhenExecutingContinueOnEngine();

        _ = _wrapper.Received(1).RemoveBreakpoint(_model.Wrapper, 10);
        _ = _wrapper.Received(1).RemoveBreakpoint(_model.Wrapper, 11);
        Assert.Null(_model.ActiveManagedStep);
        Assert.DoesNotContain(10u, _model.UserBreakpointIds);
        Assert.DoesNotContain(11u, _model.UserBreakpointIds);
    }

    #region Given

    private void GivenManagedMethodInJitMap(ulong startAddr, string tokenHex, string assemblyName)
    {
        int token = int.Parse(tokenHex, System.Globalization.NumberStyles.HexNumber);
        JitMethodInfo info = new(token, startAddr, 0x100, assemblyName);
        lock (_model.JitMethodMap)
        {
            _model.JitMethodMap[startAddr] = info;
            _model.JitMethodMapByToken[(token, assemblyName)] = info;
        }
    }

    private void GivenStackFramesForStep(NativeStackFrame[] frames) =>
        _ = _wrapper.GetStackTrace(_model.Wrapper, Arg.Any<int>()).Returns(frames);

    private void GivenAssemblyPathForMethod(string assemblyName, string path) =>
        _ = _managedDebugger.FindAssemblyPath(_model, assemblyName).Returns(path);

    private void GivenILToNativeMapping(int token, string assembly, ulong codeStart, (int IL, int Native)[] map) =>
        _model.JitMethodMappings[(token, assembly)] = new JitMethodMapping(
            codeStart, [.. map.Select(m => (m.IL, m.Native))]);

    private void GivenSequencePoints(string assemblyPath, int methodToken,
        (int ILOffset, string File, int Line)[] points) =>
        _ = _pdbMapper.GetMethodSequencePoints(assemblyPath, methodToken).Returns(points);

    private void GivenAddHardwareBreakpointSucceeds(ulong address, uint bpId) =>
        _ = _wrapper.AddHardwareBreakpoint(_model.Wrapper, address, 1).Returns((bpId, true));

    private void GivenActiveManagedStep(uint[] bpIds)
    {
        _model.ActiveManagedStep = new ManagedStepState();
        foreach (uint id in bpIds)
            _model.ActiveManagedStep.TempBreakpointIds.Add(id);
    }

    #endregion

    #region When

    private void WhenExecutingContinueOnEngine() => _testee.ExecuteContinueOnEngine(_model);

    private void WhenExecutingStepOnEngine(EngineExecutionStatus stepKind) => _testee.ExecuteStepOnEngine(_model, stepKind);

    private void WhenExecutingStepOutOnEngine() => _testee.ExecuteStepOutOnEngine(_model);

    #endregion

    #region Then

    private void ThenConfigDoneIsTrue() => Assert.True(_model.ConfigDone);

    private void ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus status) => _wrapper.Received().SetExecutionStatus(_model.Wrapper, status);

    private void ThenExecuteCommandWasCalledWith(string command) => _ = _wrapper.Received(1).ExecuteCommand(_model.Wrapper, command);

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly IManagedDebugger _managedDebugger = Substitute.For<IManagedDebugger>();
    private readonly ICorDebugWrapper _corDebug = Substitute.For<ICorDebugWrapper>();
    private readonly IPdbSourceMapper _pdbMapper = Substitute.For<IPdbSourceMapper>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly SteppingService _testee;

    public SteppingServiceTests()
    {
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new SteppingService(_log, _logStore, _managedDebugger, _corDebug, _pdbMapper, _wrapper);
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
