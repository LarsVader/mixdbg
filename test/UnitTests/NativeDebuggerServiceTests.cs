using System.Runtime.InteropServices;
using MixDbg.Models.Dap;
using MixDbg.Engine.DbgEng;
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

        ThenSetExecutionStatusWasCalledWith(DebugStatus.Go);
    }

    [Fact]
    public void ExecuteContinueOnEngine_WhenCalled_ClearsCachedStackTrace()
    {
        _model.CachedStackTraceResult = [new StackFrame { Id = 1 }];

        WhenExecutingContinueOnEngine();

        Assert.Null(_model.CachedStackTraceResult);
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
        WhenExecutingStepOnEngine(DebugStatus.StepOver);

        ThenSetExecutionStatusWasCalledWith(DebugStatus.StepOver);
    }

    [Fact]
    public void ExecuteStepOnEngine_WhenStepInto_CallsSetExecutionStatusStepInto()
    {
        WhenExecutingStepOnEngine(DebugStatus.StepInto);

        ThenSetExecutionStatusWasCalledWith(DebugStatus.StepInto);
    }

    // ── ExecuteStepOutOnEngine ──────────────────────────────

    [Fact]
    public void ExecuteStepOutOnEngine_WhenCalled_CallsExecuteGu()
    {
        WhenExecutingStepOutOnEngine();

        ThenExecuteWasCalledWith("gu");
    }

    // ── Terminate ──────────────────────────────────────────

    [Fact]
    public void Terminate_WhenCalled_SetsTerminated()
    {
        WhenTerminating();

        ThenModelIsTerminated();
    }

    [Fact]
    public void Terminate_WhenTargetNotExited_CallsTerminateProcesses()
    {
        WhenTerminating();

        ThenTerminateProcessesWasCalled();
    }

    [Fact]
    public void Terminate_WhenTargetAlreadyExited_SkipsTerminateProcesses()
    {
        GivenTargetExited();

        WhenTerminating();

        ThenTerminateProcessesWasNotCalled();
    }

    [Fact]
    public void Terminate_WhenCalled_CallsEndSession()
    {
        WhenTerminating();

        ThenEndSessionWasCalledWith(DebugEnd.ActiveTerminate);
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
    public void Detach_WhenCalled_CallsDetachProcesses()
    {
        WhenDetaching();

        ThenDetachProcessesWasCalled();
    }

    [Fact]
    public void Detach_WhenCalled_CallsEndSession()
    {
        WhenDetaching();

        ThenEndSessionWasCalledWith(DebugEnd.ActiveDetach);
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
        GivenAddBreakpointSucceeds(bpId: 5);
        GivenGetLineByOffsetSucceeds(offset: 0x1000, resolvedLine: 42);
        GivenBreakpointRequest(@"C:\src\main.cpp", [42]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, true);
        ThenBreakpointAtIndexHasLine(0, 42);
        ThenBreakpointAtIndexHasId(0, 5);
        ThenUserBreakpointIdsContains(5);
    }

    [Fact]
    public void SetBreakpointsOnEngine_WhenOffsetResolved_SetsOffsetAndEnablesBreakpoint()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineSucceeds(@"C:\src\main.cpp", line: 10, offset: 0x2000);
        GivenAddBreakpointSucceeds(bpId: 1);
        GivenGetLineByOffsetSucceeds(offset: 0x2000, resolvedLine: 10);
        GivenBreakpointRequest(@"C:\src\main.cpp", [10]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointSetOffsetWasCalled(0x2000);
        ThenBreakpointAddFlagsWasCalled(DebugBreakpointFlag.Enabled);
    }

    // ── SetBreakpointsOnEngine (native, deferred via bu) ────

    [Fact]
    public void SetBreakpointsOnEngine_WhenOffsetFails_UsesDeferredBreakpoint()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineFails(@"C:\src\main.cpp", line: 99);
        GivenBuCommandSucceeds(deferredBpId: 7);
        GivenBreakpointRequest(@"C:\src\main.cpp", [99]);

        WhenSettingBreakpointsOnEngine();

        ThenBreakpointResultCountIs(1);
        ThenBreakpointAtIndexIsVerified(0, true);
        ThenBreakpointAtIndexHasId(0, 7);
        ThenUserBreakpointIdsContains(7);
    }

    [Fact]
    public void SetBreakpointsOnEngine_WhenBuCommandFails_ReturnsUnverified()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineFails(@"C:\src\main.cpp", line: 99);
        GivenBuCommandFails();
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
        GivenGetBreakpointByIdSucceeds(bpId: 3);
        GivenGetOffsetByLineSucceeds(@"C:\src\main.cpp", line: 20, offset: 0x3000);
        GivenAddBreakpointSucceeds(bpId: 8);
        GivenGetLineByOffsetSucceeds(offset: 0x3000, resolvedLine: 20);
        GivenBreakpointRequest(@"C:\src\main.cpp", [20]);

        WhenSettingBreakpointsOnEngine();

        ThenRemoveBreakpointWasCalled();
        ThenUserBreakpointIdsDoesNotContain(3);
        ThenUserBreakpointIdsContains(8);
    }

    // ── SetBreakpointsOnEngine (multiple breakpoints) ───────

    [Fact]
    public void SetBreakpointsOnEngine_WhenMultipleLines_ReturnsAllResults()
    {
        GivenSourceFileIsNative(@"C:\src\main.cpp");
        GivenGetOffsetByLineSucceedsForMultiple(@"C:\src\main.cpp",
            [(10, 0x1000), (20, 0x2000), (30, 0x3000)]);
        GivenAddBreakpointSucceedsMultiple([1, 2, 3]);
        GivenGetLineByOffsetSucceedsForMultiple(
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
        GivenThreadsExist(engineIds: [0, 1, 2], sysIds: [1000, 1001, 1002]);

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
    public void GetScopesOnEngine_WhenInvalidFrameId_ReturnsEmpty()
    {
        GivenCachedStackFrames(0);

        WhenGettingScopesOnEngine(frameId: 99);

        ThenScopeResultCountIs(0);
    }

    [Fact]
    public void GetScopesOnEngine_WhenValidFrame_CallsSetScope()
    {
        GivenCachedStackFrames(2);
        GivenSetScopeSucceeds();
        GivenGetScopeSymbolGroupSucceeds(symbolCount: 3);

        WhenGettingScopesOnEngine(frameId: 1);

        ThenSetScopeWasCalled();
    }

    [Fact]
    public void GetScopesOnEngine_WhenSymbolGroupFails_ReturnsEmpty()
    {
        GivenCachedStackFrames(1);
        GivenSetScopeSucceeds();
        GivenGetScopeSymbolGroupFails();

        WhenGettingScopesOnEngine(frameId: 1);

        ThenScopeResultCountIs(0);
    }

    [Fact]
    public void GetScopesOnEngine_WhenSymbolGroupHasSymbols_ReturnsLocalsScope()
    {
        GivenCachedStackFrames(1);
        GivenSetScopeSucceeds();
        GivenGetScopeSymbolGroupSucceeds(symbolCount: 5);

        WhenGettingScopesOnEngine(frameId: 1);

        ThenScopeResultCountIs(1);
        ThenScopeAtIndexHasName(0, "Locals");
        ThenScopeAtIndexHasPositiveVariablesReference(0);
    }

    [Fact]
    public void GetScopesOnEngine_WhenSymbolGroupIsEmpty_ReturnsEmpty()
    {
        GivenCachedStackFrames(1);
        GivenSetScopeSucceeds();
        GivenGetScopeSymbolGroupSucceeds(symbolCount: 0);

        WhenGettingScopesOnEngine(frameId: 1);

        ThenScopeResultCountIs(0);
    }

    // ── GetVariablesOnEngine ────────────────────────────────

    [Fact]
    public void GetVariablesOnEngine_WhenUnknownRef_ReturnsEmpty()
    {
        WhenGettingVariablesOnEngine(variablesReference: 999);

        ThenVariableResultCountIs(0);
    }

    [Fact]
    public void GetVariablesOnEngine_WhenValidRef_ReturnsSymbolsFromGroup()
    {
        GivenCachedStackFrames(1);
        GivenSetScopeSucceeds();
        GivenGetScopeSymbolGroupSucceeds(symbolCount: 2);
        GivenSymbolGroupReturnsVariable(0, "x", "int", "42");
        GivenSymbolGroupReturnsVariable(1, "y", "float", "3.14");
        GivenSymbolParametersWithNoChildren(2);

        WhenGettingScopesOnEngine(frameId: 1);
        WhenGettingVariablesOnEngine(variablesReference: _scopeResults![0].VariablesReference);

        ThenVariableResultCountIs(2);
        ThenVariableAtIndexHasName(0, "x");
        ThenVariableAtIndexHasValue(0, "42");
        ThenVariableAtIndexHasType(0, "int");
        ThenVariableAtIndexHasName(1, "y");
        ThenVariableAtIndexHasValue(1, "3.14");
    }

    [Fact]
    public void GetVariablesOnEngine_WhenSymbolHasSubElements_AllocatesChildReference()
    {
        GivenCachedStackFrames(1);
        GivenSetScopeSucceeds();
        GivenGetScopeSymbolGroupSucceeds(symbolCount: 1);
        GivenSymbolGroupReturnsVariable(0, "obj", "MyStruct", "{...}");
        GivenSymbolParametersWithChildren(count: 1, subElements: 2);
        GivenExpandSymbolSucceeds(newTotal: 3);

        WhenGettingScopesOnEngine(frameId: 1);
        WhenGettingVariablesOnEngine(variablesReference: _scopeResults![0].VariablesReference);

        ThenVariableResultCountIs(1);
        ThenVariableAtIndexHasPositiveVariablesReference(0);
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

    private void GivenTargetExited()
    {
        _model.TargetExited = true;
    }

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
        _symbols.GetOffsetByLine((uint)line, file, out Arg.Any<ulong>())
            .Returns(ci =>
            {
                ci[2] = offset;
                return 0;
            });
    }

    private void GivenGetOffsetByLineSucceedsForMultiple(string file, (int line, ulong offset)[] mappings)
    {
        _symbols.GetOffsetByLine(Arg.Any<uint>(), file, out Arg.Any<ulong>())
            .Returns(ci =>
            {
                var reqLine = (int)(uint)ci[0];
                var match = mappings.FirstOrDefault(m => m.line == reqLine);
                if (match != default)
                {
                    ci[2] = match.offset;
                    return 0;
                }
                return unchecked((int)0x80004005);
            });
    }

    private void GivenGetOffsetByLineFails(string file, int line)
    {
        _symbols.GetOffsetByLine((uint)line, file, out Arg.Any<ulong>())
            .Returns(unchecked((int)0x80004005));
    }

    private void GivenAddBreakpointSucceeds(uint bpId)
    {
        _control.AddBreakpoint(Arg.Any<uint>(), Arg.Any<uint>(), out Arg.Any<IDebugBreakpoint>())
            .Returns(ci =>
            {
                _mockBp = Substitute.For<IDebugBreakpoint>();
                _mockBp.GetId(out Arg.Any<uint>()).Returns(c => { c[0] = bpId; return 0; });
                ci[2] = _mockBp;
                return 0;
            });
    }

    private void GivenAddBreakpointSucceedsMultiple(uint[] bpIds)
    {
        var idx = 0;
        _control.AddBreakpoint(Arg.Any<uint>(), Arg.Any<uint>(), out Arg.Any<IDebugBreakpoint>())
            .Returns(ci =>
            {
                var id = bpIds[idx++];
                var bp = Substitute.For<IDebugBreakpoint>();
                bp.GetId(out Arg.Any<uint>()).Returns(c => { c[0] = id; return 0; });
                _mockBps.Add(bp);
                ci[2] = bp;
                return 0;
            });
    }

    private void GivenGetLineByOffsetSucceeds(ulong offset, int resolvedLine)
    {
        _symbols.GetLineByOffset(offset, out Arg.Any<uint>(),
                Arg.Any<IntPtr>(), Arg.Any<uint>(), out Arg.Any<uint>(), out Arg.Any<ulong>())
            .Returns(ci =>
            {
                ci[1] = (uint)resolvedLine;
                return 0;
            });
    }

    private void GivenGetLineByOffsetSucceedsForMultiple((ulong offset, int line)[] mappings)
    {
        _symbols.GetLineByOffset(Arg.Any<ulong>(), out Arg.Any<uint>(),
                Arg.Any<IntPtr>(), Arg.Any<uint>(), out Arg.Any<uint>(), out Arg.Any<ulong>())
            .Returns(ci =>
            {
                var reqOffset = (ulong)ci[0];
                var match = mappings.FirstOrDefault(m => m.offset == reqOffset);
                if (match != default)
                {
                    ci[1] = (uint)match.line;
                    return 0;
                }
                return unchecked((int)0x80004005);
            });
    }

    private void GivenBuCommandSucceeds(uint deferredBpId)
    {
        _control.Execute(Arg.Any<uint>(), Arg.Is<string>(s => s.StartsWith("bu")), Arg.Any<uint>())
            .Returns(0);
        _control.GetNumberBreakpoints(out Arg.Any<uint>())
            .Returns(ci => { ci[0] = 1u; return 0; });
        var deferredBp = Substitute.For<IDebugBreakpoint>();
        deferredBp.GetId(out Arg.Any<uint>()).Returns(ci => { ci[0] = deferredBpId; return 0; });
        _control.GetBreakpointByIndex(0u, out Arg.Any<IDebugBreakpoint>())
            .Returns(ci => { ci[1] = deferredBp; return 0; });
    }

    private void GivenBuCommandFails()
    {
        _control.Execute(Arg.Any<uint>(), Arg.Is<string>(s => s.StartsWith("bu")), Arg.Any<uint>())
            .Returns(unchecked((int)0x80004005));
    }

    private void GivenExistingBreakpointForFile(string filePath, int line, uint bpId)
    {
        _model.BreakpointIds[$"{filePath}:{line}"] = bpId;
        _model.UserBreakpointIds.Add(bpId);
    }

    private void GivenGetBreakpointByIdSucceeds(uint bpId)
    {
        var oldBp = Substitute.For<IDebugBreakpoint>();
        _control.GetBreakpointById(bpId, out Arg.Any<IDebugBreakpoint>())
            .Returns(ci => { ci[1] = oldBp; return 0; });
        _oldBreakpoint = oldBp;
    }

    private void GivenThreadsExist(uint[] engineIds, uint[] sysIds)
    {
        _sysObjects.GetNumberThreads(out Arg.Any<uint>())
            .Returns(ci => { ci[0] = (uint)engineIds.Length; return 0; });
        _sysObjects.GetThreadIdsByIndex(0, (uint)engineIds.Length,
                Arg.Any<uint[]>(), Arg.Any<uint[]?>())
            .Returns(ci =>
            {
                var ids = (uint[])ci[2];
                var sys = (uint[]?)ci[3];
                Array.Copy(engineIds, ids, engineIds.Length);
                if (sys != null) Array.Copy(sysIds, sys, sysIds.Length);
                return 0;
            });
    }

    private void GivenNoThreadsExist()
    {
        _sysObjects.GetNumberThreads(out Arg.Any<uint>())
            .Returns(ci => { ci[0] = 0u; return 0; });
    }

    private void GivenEventThreadId(uint threadId)
    {
        _sysObjects.GetEventThread(out Arg.Any<uint>())
            .Returns(ci => { ci[0] = threadId; return 0; });
    }

    private void GivenCachedStackFrames(int count)
    {
        _model.CachedStackFrames = Enumerable.Range(0, count)
            .Select(i => new DEBUG_STACK_FRAME { InstructionOffset = (ulong)(0x1000 + i * 0x100) })
            .ToArray();
    }

    private void GivenSetScopeSucceeds()
    {
        _symbols.SetScope(Arg.Any<ulong>(), Arg.Any<IntPtr>(), Arg.Any<IntPtr>(), Arg.Any<uint>())
            .Returns(0);
    }

    private void GivenGetScopeSymbolGroupSucceeds(uint symbolCount)
    {
        _initialSymbolCount = symbolCount;
        _mockSymbolGroup = Substitute.For<IDebugSymbolGroup2>();
        _mockSymbolGroup.GetNumberSymbols(out Arg.Any<uint>())
            .Returns(ci =>
            {
                // After expansion, return the expanded total if set.
                var expanded = _symbolExpanded && _expandedTotal > 0;
                ci[0] = expanded ? _expandedTotal : _initialSymbolCount;
                return 0;
            });
        _mockSymbolGroup.ExpandSymbol(Arg.Any<uint>(), true)
            .Returns(ci =>
            {
                _symbolExpanded = true;
                return 0;
            });
        _symbols.GetScopeSymbolGroup(
                Arg.Any<uint>(), Arg.Any<IntPtr>(), out Arg.Any<IDebugSymbolGroup2>())
            .Returns(ci => { ci[2] = _mockSymbolGroup; return 0; });
    }

    private void GivenGetScopeSymbolGroupFails()
    {
        _symbols.GetScopeSymbolGroup(
                Arg.Any<uint>(), IntPtr.Zero, out Arg.Any<IDebugSymbolGroup2>())
            .Returns(unchecked((int)0x80004005));
    }

    private void GivenSymbolGroupReturnsVariable(uint index, string name, string type, string value)
    {
        _symbolVariables[index] = (name, type, value);
        SetupSymbolGroupVariableMocks();
    }

    private void SetupSymbolGroupVariableMocks()
    {
        var vars = _symbolVariables;
        _mockSymbolGroup!.GetSymbolName(Arg.Any<uint>(), Arg.Any<IntPtr>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(ci =>
            {
                var idx = (uint)ci[0];
                if (vars.TryGetValue(idx, out var v))
                {
                    WriteAnsiString((IntPtr)ci[1], v.Name);
                    ci[3] = (uint)v.Name.Length + 1;
                    return 0;
                }
                return unchecked((int)0x80004005);
            });
        _mockSymbolGroup!.GetSymbolTypeName(Arg.Any<uint>(), Arg.Any<IntPtr>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(ci =>
            {
                var idx = (uint)ci[0];
                if (vars.TryGetValue(idx, out var v))
                {
                    WriteAnsiString((IntPtr)ci[1], v.Type);
                    ci[3] = (uint)v.Type.Length + 1;
                    return 0;
                }
                return unchecked((int)0x80004005);
            });
        _mockSymbolGroup!.GetSymbolValueText(Arg.Any<uint>(), Arg.Any<IntPtr>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(ci =>
            {
                var idx = (uint)ci[0];
                if (vars.TryGetValue(idx, out var v))
                {
                    WriteAnsiString((IntPtr)ci[1], v.Value);
                    ci[3] = (uint)v.Value.Length + 1;
                    return 0;
                }
                return unchecked((int)0x80004005);
            });
    }

    private void GivenSymbolParametersWithNoChildren(uint count)
    {
        _mockSymbolGroup!.GetSymbolParameters(Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<IntPtr>())
            .Returns(ci =>
            {
                var cnt = (uint)ci[1];
                var buf = (IntPtr)ci[2];
                int size = Marshal.SizeOf<DEBUG_SYMBOL_PARAMETERS>();
                for (int i = 0; i < (int)cnt; i++)
                {
                    var p = new DEBUG_SYMBOL_PARAMETERS { SubElements = 0 };
                    Marshal.StructureToPtr(p, buf + i * size, false);
                }
                return 0;
            });
    }

    private void GivenSymbolParametersWithChildren(uint count, uint subElements)
    {
        _mockSymbolGroup!.GetSymbolParameters(Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<IntPtr>())
            .Returns(ci =>
            {
                var cnt = (uint)ci[1];
                var buf = (IntPtr)ci[2];
                int size = Marshal.SizeOf<DEBUG_SYMBOL_PARAMETERS>();
                for (int i = 0; i < (int)cnt; i++)
                {
                    var p = new DEBUG_SYMBOL_PARAMETERS { SubElements = subElements };
                    Marshal.StructureToPtr(p, buf + i * size, false);
                }
                return 0;
            });
    }

    private void GivenExpandSymbolSucceeds(uint newTotal)
    {
        _expandedTotal = newTotal;
    }

    private static void WriteAnsiString(IntPtr buffer, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value + '\0');
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
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

    private void WhenExecutingStepOnEngine(uint stepKind)
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

    private void ThenSetExecutionStatusWasCalledWith(uint status)
    {
        _control.Received().SetExecutionStatus(status);
    }

    private void ThenPauseRequestedIsTrue()
    {
        Assert.True(_model.PauseRequested);
    }

    private void ThenSetInterruptWasCalled()
    {
        _control.Received(1).SetInterrupt(0);
    }

    private void ThenExecuteWasCalledWith(string command)
    {
        _control.Received(1).Execute(DebugOutCtl.Ignore, command, DebugExecute.NotLogged);
    }

    private void ThenModelIsTerminated()
    {
        Assert.True(_model.Terminated);
    }

    private void ThenTerminateProcessesWasCalled()
    {
        _client.Received(1).TerminateProcesses();
    }

    private void ThenTerminateProcessesWasNotCalled()
    {
        _client.DidNotReceive().TerminateProcesses();
    }

    private void ThenEndSessionWasCalledWith(uint flags)
    {
        _client.Received(1).EndSession(flags);
    }

    private void ThenDetachProcessesWasCalled()
    {
        _client.Received(1).DetachProcesses();
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

    private void ThenBreakpointSetOffsetWasCalled(ulong offset)
    {
        _mockBp!.Received(1).SetOffset(offset);
    }

    private void ThenBreakpointAddFlagsWasCalled(uint flags)
    {
        _mockBp!.Received(1).AddFlags(flags);
    }

    private void ThenRemoveBreakpointWasCalled()
    {
        _control.Received(1).RemoveBreakpoint(Arg.Any<IDebugBreakpoint>());
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

    private void ThenScopeAtIndexHasPositiveVariablesReference(int index)
    {
        Assert.True(_scopeResults![index].VariablesReference > 0);
    }

    private void ThenSetScopeWasCalled()
    {
        _symbols.Received(1).SetScope(
            Arg.Any<ulong>(), Arg.Any<IntPtr>(), Arg.Any<IntPtr>(), Arg.Any<uint>());
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

    private void ThenVariableAtIndexHasPositiveVariablesReference(int index)
    {
        Assert.True(_variableResults![index].VariablesReference > 0);
    }

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly ISourceFileService _sourceFiles = Substitute.For<ISourceFileService>();
    private readonly IManagedDebugger _managedDebugger = Substitute.For<IManagedDebugger>();
    private readonly IDebugClient _client = Substitute.For<IDebugClient>();
    private readonly IDebugControl _control = Substitute.For<IDebugControl>();
    private readonly IDebugSymbols _symbols = Substitute.For<IDebugSymbols>();
    private readonly IDebugSystemObjects _sysObjects = Substitute.For<IDebugSystemObjects>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly NativeDebuggerService _testee;

    private NativeDebuggerModel? _createdModel;
    private IDebugBreakpoint? _mockBp;
    private readonly List<IDebugBreakpoint> _mockBps = [];
    private IDebugBreakpoint? _oldBreakpoint;
    private IDebugSymbolGroup2? _mockSymbolGroup;
    private readonly Dictionary<uint, (string Name, string Type, string Value)> _symbolVariables = new();
    private uint _initialSymbolCount;
    private uint _expandedTotal;
    private bool _symbolExpanded;
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
        _testee = new NativeDebuggerService(_server, _transport, _log, _logStore, _sourceFiles, _managedDebugger, Substitute.For<IProfilerPipeService>());
        _model = new NativeDebuggerModel
        {
            Client = _client,
            Control = _control,
            Symbols = _symbols,
            SysObjects = _sysObjects,
        };

        _control.SetExecutionStatus(Arg.Any<uint>()).Returns(0);
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
