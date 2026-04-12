using MixDbg.Engine.DbgEng;
using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Protocol;
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

    [Fact]
    public void GetScopesOnEngine_WhenNativeZeroAndManagedReturnsRef_ReturnsManagedScope()
    {
        GivenSetScopeAndGetLocalsReturns(0);
        GivenManagedInitialized();
        GivenCachedNativeFrame(frameId: 1, ip: 0x7000);
        GivenTryGetManagedLocalsReturns(0x7000, 100_001);

        WhenGettingScopesOnEngine(frameId: 1);

        ThenScopeResultCountIs(1);
        ThenScopeAtIndexHasName(0, "Locals");
        ThenScopeAtIndexHasVariablesReference(0, 100_001);
    }

    [Fact]
    public void GetScopesOnEngine_WhenNativeZeroAndManagedReturnsZero_ReturnsEmpty()
    {
        GivenSetScopeAndGetLocalsReturns(0);
        GivenManagedInitialized();
        GivenCachedNativeFrame(frameId: 1, ip: 0x8000);
        GivenTryGetManagedLocalsReturns(0x8000, 0);

        WhenGettingScopesOnEngine(frameId: 1);

        ThenScopeResultCountIs(0);
    }

    [Fact]
    public void GetScopesOnEngine_WhenNativeZeroAndManagedNotInitialized_ReturnsEmpty()
    {
        GivenSetScopeAndGetLocalsReturns(0);

        WhenGettingScopesOnEngine(frameId: 1);

        ThenScopeResultCountIs(0);
    }

    // ── GetVariablesOnEngine ────────────────────────────────

    [Fact]
    public void GetVariablesOnEngine_WhenManagedRef_RoutesToCorDebug()
    {
        GivenGetManagedVariablesReturns(100_001, [
            new VariableInfo("x", "42", "int", 0),
        ]);

        WhenGettingVariablesOnEngine(variablesReference: 100_001);

        ThenVariableResultCountIs(1);
        ThenVariableAtIndexHasName(0, "x");
        ThenVariableAtIndexHasValue(0, "42");
    }

    [Fact]
    public void GetVariablesOnEngine_WhenNativeRef_RoutesToWrapper()
    {
        GivenGetVariablesReturns([new VariableInfo("y", "3.14", "float", 0)]);

        WhenGettingVariablesOnEngine(variablesReference: 1);

        ThenVariableResultCountIs(1);
        ThenVariableAtIndexHasName(0, "y");
    }

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

    // ── GetStackTraceOnEngine ─────────────────────────────

    [Fact]
    public void GetStackTraceOnEngine_WhenCachedResultExists_ReturnsCachedResult()
    {
        GivenCachedStackTraceResult([new StackFrame { Id = 1, Name = "cached" }]);

        WhenGettingStackTraceOnEngine();

        ThenStackTraceResultCountIs(1);
        ThenStackFrameAtIndexHasName(0, "cached");
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenNoNativeFrames_ReturnsEmpty()
    {
        GivenNativeStackFrames([]);

        WhenGettingStackTraceOnEngine();

        ThenStackTraceResultCountIs(0);
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenDbgEngSourceResolved_ReturnsFrameWithSource()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x1000)]);
        GivenNameByOffset(0x1000, ("MyFunc", 0UL));
        GivenLineByOffset(0x1000, (10, @"C:\src\file.cpp"));

        WhenGettingStackTraceOnEngine();

        ThenStackTraceResultCountIs(1);
        ThenStackFrameAtIndexHasName(0, "MyFunc");
        ThenStackFrameAtIndexHasLine(0, 10);
        ThenStackFrameAtIndexHasSourcePath(0, @"C:\src\file.cpp");
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenDbgEngFails_UsesProfilerFallback()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x2000)]);
        GivenNameByOffset(0x2000, ("NativeFunc", 0UL));
        GivenLineByOffsetReturnsNull(0x2000);
        GivenJitMethodMapHasEntries();
        GivenProfilerResolvesFrame(0x2000, ("ManagedMethod", new Source { Name = "App.cs", Path = @"C:\src\App.cs" }, 42));

        WhenGettingStackTraceOnEngine();

        ThenStackTraceResultCountIs(1);
        ThenStackFrameAtIndexHasName(0, "ManagedMethod");
        ThenStackFrameAtIndexHasLine(0, 42);
        ThenStackFrameAtIndexHasSourcePath(0, @"C:\src\App.cs");
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenNoSourceResolution_ReturnsHexAddressName()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x3000)]);
        GivenNameByOffsetReturnsNull(0x3000);
        GivenLineByOffsetReturnsNull(0x3000);

        WhenGettingStackTraceOnEngine();

        ThenStackTraceResultCountIs(1);
        ThenStackFrameAtIndexHasName(0, "0x3000");
        ThenStackFrameAtIndexHasLine(0, 0);
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenCalled_CachesResult()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x1000)]);
        GivenNameByOffset(0x1000, ("Func", 0UL));
        GivenLineByOffsetReturnsNull(0x1000);

        WhenGettingStackTraceOnEngine();

        Assert.NotNull(_model.CachedStackTraceResult);
        _ = Assert.Single(_model.CachedStackTraceResult);
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenManagedInitialized_MergesManagedFrames()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x1000)]);
        GivenNameByOffset(0x1000, ("Func", 0UL));
        GivenLineByOffsetReturnsNull(0x1000);
        _model.ManagedInitialized = true;

        WhenGettingStackTraceOnEngine();

        _managedDebugger.Received(1).MergeManagedFrames(_model, Arg.Any<StackFrame[]>());
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenManagedNotInitialized_DoesNotMergeManagedFrames()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x1000)]);
        GivenNameByOffset(0x1000, ("Func", 0UL));
        GivenLineByOffsetReturnsNull(0x1000);

        WhenGettingStackTraceOnEngine();

        _managedDebugger.DidNotReceive().MergeManagedFrames(Arg.Any<NativeDebuggerModel>(), Arg.Any<StackFrame[]>());
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenDisplacementGreaterThanZero_FormatsNameWithOffset()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x1010)]);
        GivenNameByOffset(0x1010, ("MyFunc", 0x10UL));
        GivenLineByOffsetReturnsNull(0x1010);

        WhenGettingStackTraceOnEngine();

        ThenStackFrameAtIndexHasName(0, "MyFunc+0x10");
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenProfilerThrowsException_ReturnsFrameWithoutSource()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x5000)]);
        GivenNameByOffset(0x5000, ("Func", 0UL));
        GivenLineByOffsetReturnsNull(0x5000);
        GivenJitMethodMapHasEntries();
        GivenProfilerResolvesFrameThrows(0x5000, new InvalidOperationException("PDB error"));

        WhenGettingStackTraceOnEngine();

        ThenStackTraceResultCountIs(1);
        ThenStackFrameAtIndexHasName(0, "Func");
        ThenStackFrameAtIndexHasLine(0, 0);
    }

    [Fact]
    public void GetStackTraceOnEngine_WhenProfilerReturnsNull_ReturnsFrameWithoutSource()
    {
        GivenNativeStackFrames([new NativeStackFrame(0x6000)]);
        GivenNameByOffset(0x6000, ("Func", 0UL));
        GivenLineByOffsetReturnsNull(0x6000);
        GivenJitMethodMapHasEntries();
        _ = _managedDebugger.ResolveFrameFromProfilerData(_model, 0x6000)
            .Returns(((string, Source?, int)?)null);

        WhenGettingStackTraceOnEngine();

        ThenStackTraceResultCountIs(1);
        ThenStackFrameAtIndexHasName(0, "Func");
        ThenStackFrameAtIndexHasLine(0, 0);
    }

    // ── Managed step-over ────────────────────────────────────

    [Fact]
    public void ExecuteStepOnEngine_WhenManagedFrame_SetsActiveManagedStep()
    {
        // Current IP 0x5010 → native offset 0x10 → IL offset 10.
        // Next SP: IL 20 → native offset 0x20 → address 0x5020.
        GivenManagedMethodInJitMap(0x5000, tokenHex: "06000001", assemblyName: "App");
        GivenStackFramesForStep([new NativeStackFrame(0x5010), new NativeStackFrame(0x3000)]);
        GivenAssemblyPathForMethod("App", @"C:\src\App.dll");
        GivenILToNativeMapping("App:06000001", codeStart: 0x5000, [(0, 0), (10, 0x10), (20, 0x20)]);
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
        GivenStackFramesForStep([new NativeStackFrame(0x5010), new NativeStackFrame(0x3000)]);
        GivenAssemblyPathForMethod("App", @"C:\src\App.dll");
        GivenILToNativeMapping("App:06000001", codeStart: 0x5000, [(0, 0), (10, 0x10), (20, 0x20)]);
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
        GivenStackFramesForStep([new NativeStackFrame(0x5020), new NativeStackFrame(0x3000)]);
        GivenAssemblyPathForMethod("App", @"C:\src\App.dll");
        GivenILToNativeMapping("App:06000001", codeStart: 0x5000, [(0, 0), (10, 0x10), (20, 0x20)]);
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
        GivenStackFramesForStep([new NativeStackFrame(0x5010), new NativeStackFrame(0x3000)]);
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

    // ── GetStoppedThreadIdOnEngine ──────────────────────────

    [Fact]
    public void GetStoppedThreadIdOnEngine_WhenCalled_ReturnsEventThread()
    {
        GivenEventThreadId(42);

        WhenGettingStoppedThreadIdOnEngine();

        ThenStoppedThreadIdIs(42);
    }

    #region Given

    private void GivenCachedStackTraceResult(StackFrame[] cached) => _model.CachedStackTraceResult = cached;

    private void GivenNativeStackFrames(NativeStackFrame[] frames) => _ = _wrapper.GetStackTrace(_model.Wrapper, Arg.Any<int>()).Returns(frames);

    private void GivenNameByOffset(ulong ip, (string Name, ulong Displacement) result) => _ = _wrapper.GetNameByOffset(_model.Wrapper, ip).Returns(result);

    private void GivenNameByOffsetReturnsNull(ulong ip) => _ = _wrapper.GetNameByOffset(_model.Wrapper, ip).Returns(((string, ulong)?)null);

    private void GivenLineByOffset(ulong ip, (uint Line, string File) result) => _ = _wrapper.GetLineByOffset(_model.Wrapper, ip).Returns(result);

    private void GivenLineByOffsetReturnsNull(ulong ip) => _ = _wrapper.GetLineByOffset(_model.Wrapper, ip).Returns(((uint, string)?)null);

    private void GivenJitMethodMapHasEntries() => _model.JitMethodMap.Add(0x1000, new JitMethodInfo(0x06000001, 0x1000, 100, "TestAssembly"));

    private void GivenProfilerResolvesFrame(ulong ip, (string Name, Source? Source, int Line) result) => _ = _managedDebugger.ResolveFrameFromProfilerData(_model, ip).Returns(result);

    private void GivenProfilerResolvesFrameThrows(ulong ip, Exception ex) => _ = _managedDebugger.ResolveFrameFromProfilerData(_model, ip).Returns<(string, Source?, int)?>(x => throw ex);

    private void GivenThreadsExist((uint engineId, uint systemId)[] threads) => _ = _wrapper.GetThreads(_model.Wrapper).Returns(threads);

    private void GivenNoThreadsExist() => _ = _wrapper.GetThreads(_model.Wrapper).Returns([]);

    private void GivenEventThreadId(uint threadId) => _ = _wrapper.GetEventThreadId(_model.Wrapper).Returns(threadId);

    private void GivenSetScopeAndGetLocalsReturns(int variablesReference) => _ = _wrapper.SetScopeAndGetLocals(_model.Wrapper, Arg.Any<int>())
            .Returns(variablesReference);

    private void GivenGetVariablesReturns(VariableInfo[] vars) => _ = _wrapper.GetVariables(_model.Wrapper, Arg.Any<int>())
            .Returns(vars);

    private void GivenManagedInitialized() => _model.ManagedInitialized = true;

    private void GivenCachedNativeFrame(int frameId, ulong ip)
    {
        Engine.DbgEng.Constants.DEBUG_STACK_FRAME[] frames = new Engine.DbgEng.Constants.DEBUG_STACK_FRAME[frameId];
        frames[frameId - 1].InstructionOffset = ip;
        _model.Wrapper.CachedStackFrames = frames;
    }

    private void GivenTryGetManagedLocalsReturns(ulong ip, int managedRef) =>
        _ = _managedDebugger.TryGetManagedLocals(_model, ip).Returns(managedRef);

    private void GivenGetManagedVariablesReturns(int variablesReference, VariableInfo[] vars) =>
        _ = _corDebug.GetManagedVariables(_model.CorWrapper, variablesReference).Returns(vars);

    private void GivenManagedMethodInJitMap(ulong startAddr, string tokenHex, string assemblyName)
    {
        int token = int.Parse(tokenHex, System.Globalization.NumberStyles.HexNumber);
        lock (_model.JitMethodMap)
        {
            _model.JitMethodMap[startAddr] = new JitMethodInfo(token, startAddr, 0x100, assemblyName);
        }
    }

    private void GivenStackFramesForStep(NativeStackFrame[] frames) =>
        _ = _wrapper.GetStackTrace(_model.Wrapper, Arg.Any<int>()).Returns(frames);

    private void GivenAssemblyPathForMethod(string assemblyName, string path) =>
        _ = _managedDebugger.FindAssemblyPath(_model, assemblyName).Returns(path);

    private void GivenILToNativeMapping(string key, ulong codeStart, (int IL, int Native)[] map) =>
        _model.JitMethodMappings[key] = new JitMethodMapping
        {
            CodeStart = codeStart,
            ILToNativeMap = [.. map.Select(m => (m.IL, m.Native))],
        };

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

    private void WhenGettingStackTraceOnEngine() => _stackTraceResults = _testee.GetStackTraceOnEngine(_model, 20);

    private void WhenExecutingContinueOnEngine() => _testee.ExecuteContinueOnEngine(_model);

    private void WhenExecutingStepOnEngine(EngineExecutionStatus stepKind) => _testee.ExecuteStepOnEngine(_model, stepKind);

    private void WhenExecutingStepOutOnEngine() => _testee.ExecuteStepOutOnEngine(_model);

    private void WhenGettingThreadsOnEngine() => _threadResults = _testee.GetThreadsOnEngine(_model);

    private void WhenGettingStoppedThreadIdOnEngine() => _stoppedThreadId = _testee.GetStoppedThreadIdOnEngine(_model);

    private void WhenGettingScopesOnEngine(int frameId) => _scopeResults = _testee.GetScopesOnEngine(_model, frameId);

    private void WhenGettingVariablesOnEngine(int variablesReference) => _variableResults = _testee.GetVariablesOnEngine(_model, variablesReference);

    #endregion

    #region Then

    private void ThenStackTraceResultCountIs(int expected) => Assert.Equal(expected, _stackTraceResults!.Length);

    private void ThenStackFrameAtIndexHasName(int index, string expected) => Assert.Equal(expected, _stackTraceResults![index].Name);

    private void ThenStackFrameAtIndexHasLine(int index, int expected) => Assert.Equal(expected, _stackTraceResults![index].Line);

    private void ThenStackFrameAtIndexHasSourcePath(int index, string expected) => Assert.Equal(expected, _stackTraceResults![index].Source?.Path);

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
    private readonly ICorDebugWrapper _corDebug = Substitute.For<ICorDebugWrapper>();
    private readonly IPdbSourceMapper _pdbMapper = Substitute.For<IPdbSourceMapper>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly EngineQueryService _testee;

    private StackFrame[]? _stackTraceResults;
    private DapThread[]? _threadResults;
    private Scope[]? _scopeResults;
    private Variable[]? _variableResults;
    private int _stoppedThreadId;

    public EngineQueryServiceTests()
    {
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _testee = new EngineQueryService(_log, _logStore, _managedDebugger, _managedBp, _corDebug, _pdbMapper, _wrapper);
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
