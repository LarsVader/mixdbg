using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MixDbg.Tests;

public sealed class ManagedBreakpointResolverServiceTests : IDisposable
{
    // ── TryResolveDeferredBreakpoints ──────────────────────

    [Fact]
    public void TryResolveDeferredBreakpoints_WhenNoDeferredBPs_ReturnsEmpty()
    {
        WhenResolvingDeferredBreakpoints();

        ThenResolvedCountIs(0);
    }

    [Fact]
    public void TryResolveDeferredBreakpoints_WhenXclrReturnsAddress_AndHwBpSucceeds_ReturnsVerified()
    {
        GivenDeferredBreakpoint(@"C:\src\Program.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenXclrDataReturnsAddress(token: 0x06000001, assembly: "TestAssembly", address: 0x7FFF0000);
        GivenSetManagedCodeBreakpointSucceeds(address: 0x7FFF0000, bpId: 42);

        WhenResolvingDeferredBreakpoints();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, true);
        ThenResolvedBreakpointHasId(0, 1);
        ThenResolvedBreakpointHasLine(0, 10);
        ThenDeferredBreakpointCountIs(0);
    }

    [Fact]
    public void TryResolveDeferredBreakpoints_WhenXclrReturnsAddress_ButHwBpFails_ReturnsUnverified()
    {
        GivenDeferredBreakpoint(@"C:\src\Program.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenXclrDataReturnsAddress(token: 0x06000001, assembly: "TestAssembly", address: 0x7FFF0000);
        GivenSetManagedCodeBreakpointFails(address: 0x7FFF0000);

        WhenResolvingDeferredBreakpoints();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, false);
        ThenResolvedBreakpointHasMessage(0, "Failed to set managed breakpoint");
        ThenDeferredBreakpointCountIs(0);
    }

    [Fact]
    public void TryResolveDeferredBreakpoints_WhenXclrReturnsZero_SkipsBP()
    {
        GivenDeferredBreakpoint(@"C:\src\Program.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenXclrDataReturnsAddress(token: 0x06000001, assembly: "TestAssembly", address: 0);

        WhenResolvingDeferredBreakpoints();

        ThenResolvedCountIs(0);
        ThenDeferredBreakpointCountIs(1);
    }

    [Fact]
    public void TryResolveDeferredBreakpoints_WhenInitializeDacThrows_StillResolves()
    {
        GivenDeferredBreakpoint(@"C:\src\Program.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        _model.CoreClrPath = @"C:\runtime\coreclr.dll";
        _model.CoreClrBaseAddress = 0x7000;
        _ = _corDebug.InitializeDac(_model.CorWrapper, _model.Wrapper, _model.CoreClrPath, _model.CoreClrBaseAddress)
            .Throws(new InvalidOperationException("DAC init failed"));
        GivenXclrDataReturnsAddress(token: 0x06000001, assembly: "TestAssembly", address: 0x8000);
        GivenSetManagedCodeBreakpointSucceeds(address: 0x8000, bpId: 99);

        WhenResolvingDeferredBreakpoints();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, true);
    }

    [Fact]
    public void TryResolveDeferredBreakpoints_WhenExceptionThrown_HandlesGracefully()
    {
        GivenDeferredBreakpoint(@"C:\src\Program.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        _ = _corDebug.ResolveNativeEntryViaXclrData(_model.CorWrapper, 0x06000001, "TestAssembly")
            .Throws(new InvalidOperationException("DAC error"));

        WhenResolvingDeferredBreakpoints();

        ThenResolvedCountIs(0);
        ThenDeferredBreakpointCountIs(1);
    }

    [Fact]
    public void TryResolveDeferredBreakpoints_RemovesResolvedBPsFromDeferredList()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm1");
        GivenDeferredBreakpoint(@"C:\src\B.cs", line: 20, token: 0x06000002, ilOffset: 0, bpId: 2, assembly: "Asm2");
        GivenXclrDataReturnsAddress(token: 0x06000001, assembly: "Asm1", address: 0x1000);
        GivenXclrDataReturnsAddress(token: 0x06000002, assembly: "Asm2", address: 0);
        GivenSetManagedCodeBreakpointSucceeds(address: 0x1000, bpId: 10);

        WhenResolvingDeferredBreakpoints();

        ThenResolvedCountIs(1);
        ThenDeferredBreakpointCountIs(1);
        ThenDeferredBreakpointContainsToken(0x06000002);
    }

    [Fact]
    public void TryResolveDeferredBreakpoints_FlushesProcessStateAndInitDac()
    {
        GivenDeferredBreakpoint(@"C:\src\Program.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        _model.CoreClrPath = @"C:\clr\coreclr.dll";
        _model.CoreClrBaseAddress = 0x70000000;
        GivenXclrDataReturnsAddress(token: 0x06000001, assembly: "TestAssembly", address: 0);

        WhenResolvingDeferredBreakpoints();

        _corDebug.Received(1).FlushProcessState(_model.CorWrapper);
        _ = _corDebug.Received(1).InitializeDac(_model.CorWrapper, _model.Wrapper, @"C:\clr\coreclr.dll", 0x70000000);
    }

    // ── ProcessProfilerNotifications: JIT-matches-deferred ─────────

    [Fact]
    public void ProcessProfilerNotifications_WhenEmpty_ReturnsFalse()
    {
        bool result = WhenProcessingProfilerNotifications();
        Assert.False(result);
    }

    [Fact]
    public void ProcessProfilerNotifications_OnJitMatchingDeferred_FoldsIntoPlan()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 5, bpId: 1, assembly: "TestAsm");
        GivenQueuedJitNotification(token: 0x06000001, address: 0x5000, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        ThenManagedBpPlanExists(token: 0x06000001, assembly: "TestAsm");
        ThenManagedBpPlanHasSiteCount(token: 0x06000001, assembly: "TestAsm", 1);
        ThenDeferredBreakpointCountIs(0);
        // HW BPs are NOT installed by JIT — they're installed on ENTER.
        _ = _bpService.DidNotReceive().SetManagedCodeBreakpoint(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<ulong>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public void ProcessProfilerNotifications_OnJitWithoutDeferred_DoesNothing()
    {
        GivenQueuedJitNotification(token: 0x06000001, address: 0x5000, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        ThenManagedBpPlanDoesNotExist(token: 0x06000001, assembly: "TestAsm");
    }

    // ── ProcessProfilerNotifications: ENTER behavior ──────────────

    [Fact]
    public void ProcessProfilerNotifications_OnFirstEnter_InstallsHwBpsAndAcks()
    {
        GivenManagedBpPlanWithSites(token: 0x06000001, assembly: "TestAsm",
            sites: [(bpId: 10, ilOffset: 0x00, line: 5), (bpId: 11, ilOffset: 0x10, line: 7)]);
        GivenJitMethodMapping("TestAsm", 0x06000001, codeStart: 0x1000, ilMap: [(0x00, 0x00), (0x10, 0x20)]);
        GivenSetManagedCodeBreakpointSucceedsWithSequential([100u, 101u]);
        GivenProfilerAckEvent();
        GivenQueuedEnter(token: 0x06000001, bodyAddress: 0x1000, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        // Both sites became HW BPs at exact-line addresses.
        _ = _bpService.Received(1).SetManagedCodeBreakpoint(_model, 0x1000UL, Arg.Any<string>(), 5);
        _ = _bpService.Received(1).SetManagedCodeBreakpoint(_model, 0x1020UL, Arg.Any<string>(), 7);
        ThenActiveMethodBreakpointExists(token: 0x06000001, assembly: "TestAsm");
        ThenActiveMethodBreakpointActivationCount(token: 0x06000001, assembly: "TestAsm", expected: 1);
        ThenProfilerAckEventIsSignaled();
    }

    [Fact]
    public void ProcessProfilerNotifications_OnRecursiveEnter_IncrementsCountAndAcksImmediately()
    {
        GivenManagedBpPlanWithSites(token: 0x06000001, assembly: "TestAsm",
            sites: [(bpId: 10, ilOffset: 0, line: 5)]);
        // Simulate an active entry already from a previous ENTER.
        _model.ActiveMethodBreakpoints[(0x06000001, "TestAsm")] =
            new ActiveMethodBreakpoint { ActivationCount = 1 };
        GivenProfilerAckEvent();
        GivenQueuedEnter(token: 0x06000001, bodyAddress: 0x1000, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        // No new HW BP installed for a recursive activation.
        _ = _bpService.DidNotReceive().SetManagedCodeBreakpoint(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<ulong>(), Arg.Any<string>(), Arg.Any<int>());
        ThenActiveMethodBreakpointActivationCount(token: 0x06000001, assembly: "TestAsm", expected: 2);
        ThenProfilerAckEventIsSignaled();
    }

    [Fact]
    public void ProcessProfilerNotifications_OnEnterForUnplannedMethod_AcksNoOp()
    {
        // No plan — e.g. an assembly-level-watch method with no user BP on it.
        GivenProfilerAckEvent();
        GivenQueuedEnter(token: 0x06000099, bodyAddress: 0x1000, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        _ = _bpService.DidNotReceive().SetManagedCodeBreakpoint(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<ulong>(), Arg.Any<string>(), Arg.Any<int>());
        ThenActiveMethodBreakpointDoesNotExist(token: 0x06000099, assembly: "TestAsm");
        ThenProfilerAckEventIsSignaled();
    }

    [Fact]
    public void ProcessProfilerNotifications_OnEnter_WhenHwBpLimitReached_LogsWarning()
    {
        GivenManagedBpPlanWithSites(token: 0x06000001, assembly: "TestAsm",
            sites: [(bpId: 10, ilOffset: 0, line: 5)]);
        GivenJitMethodMapping("TestAsm", 0x06000001, codeStart: 0x1000, ilMap: [(0, 0)]);
        GivenSetManagedCodeBreakpointFailsForAny();
        GivenProfilerAckEvent();
        GivenQueuedEnter(token: 0x06000001, bodyAddress: 0x1000, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        _log.Received().LogWarning(
            _logStore,
            Arg.Is<string>(s => s.Contains("HW BP limit reached", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<string>());
    }

    // ── ProcessProfilerNotifications: LEAVE behavior ──────────────

    [Fact]
    public void ProcessProfilerNotifications_OnLeaveToZero_RemovesHwBps()
    {
        ActiveMethodBreakpoint active = new() { ActivationCount = 1 };
        active.InstalledBpIds.Add(42);
        _ = active.InstalledAddresses.Add(0x2000UL);
        _model.ActiveMethodBreakpoints[(0x06000001, "TestAsm")] = active;
        _ = _model.UserBreakpointIds.Add(42);
        _ = _model.ManagedBreakpointIds.Add(42);
        _ = _model.ManagedBreakpointAddresses.Add(0x2000UL);
        _model.ManagedBreakpointSources[0x2000UL] = (@"C:\src\A.cs", 10);
        _model.BreakpointIds[@"C:\src\A.cs:10"] = 42;
        GivenRemoveBreakpointSucceeds(42);
        GivenQueuedLeave(token: 0x06000001, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        _ = _dbgEng.Received(1).RemoveBreakpoint(_model.Wrapper, 42);
        ThenActiveMethodBreakpointDoesNotExist(token: 0x06000001, assembly: "TestAsm");
        Assert.DoesNotContain(42u, _model.UserBreakpointIds);
        Assert.DoesNotContain(42u, _model.ManagedBreakpointIds);
        Assert.DoesNotContain(0x2000UL, _model.ManagedBreakpointAddresses);
        Assert.False(_model.ManagedBreakpointSources.ContainsKey(0x2000UL));
        Assert.False(_model.BreakpointIds.ContainsKey(@"C:\src\A.cs:10"));
    }

    [Fact]
    public void ProcessProfilerNotifications_OnLeaveWithNested_KeepsHwBps()
    {
        ActiveMethodBreakpoint active = new() { ActivationCount = 2 };
        active.InstalledBpIds.Add(42);
        _model.ActiveMethodBreakpoints[(0x06000001, "TestAsm")] = active;
        _ = _model.UserBreakpointIds.Add(42);
        GivenQueuedLeave(token: 0x06000001, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        // Count decremented but not to 0 — HW BP persists.
        ThenActiveMethodBreakpointActivationCount(token: 0x06000001, assembly: "TestAsm", expected: 1);
        _ = _dbgEng.DidNotReceive().RemoveBreakpoint(Arg.Any<DbgEngWrapperModel>(), Arg.Any<uint>());
        Assert.Contains(42u, _model.UserBreakpointIds);
    }

    [Fact]
    public void ProcessProfilerNotifications_OnTailcall_TreatedAsLeave()
    {
        ActiveMethodBreakpoint active = new() { ActivationCount = 1 };
        active.InstalledBpIds.Add(42);
        _model.ActiveMethodBreakpoints[(0x06000001, "TestAsm")] = active;
        GivenRemoveBreakpointSucceeds(42);
        GivenQueuedTailcall(token: 0x06000001, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        _ = _dbgEng.Received(1).RemoveBreakpoint(_model.Wrapper, 42);
        ThenActiveMethodBreakpointDoesNotExist(token: 0x06000001, assembly: "TestAsm");
    }

    [Fact]
    public void ProcessProfilerNotifications_OnLeaveWithoutActivation_DoesNothing()
    {
        GivenQueuedLeave(token: 0x06000001, tid: 1, assembly: "TestAsm");

        _ = WhenProcessingProfilerNotifications();

        _ = _dbgEng.DidNotReceive().RemoveBreakpoint(Arg.Any<DbgEngWrapperModel>(), Arg.Any<uint>());
    }

    [Fact]
    public void ProcessProfilerNotifications_ReturnsFalseWhenUserBpHit()
    {
        ActiveMethodBreakpoint active = new() { ActivationCount = 1 };
        _model.ActiveMethodBreakpoints[(0x06000001, "TestAsm")] = active;
        _model.HitUserBreakpoint = true;
        GivenQueuedLeave(token: 0x06000001, tid: 1, assembly: "TestAsm");

        bool result = WhenProcessingProfilerNotifications();

        // Even though we drained, a user BP hit takes precedence.
        Assert.False(result);
    }

    [Fact]
    public void ProcessProfilerNotifications_ReturnsTrueWhenDrainedAndNoBpHit()
    {
        GivenProfilerAckEvent();
        GivenQueuedEnter(token: 0x06000099, bodyAddress: 0x1000, tid: 1, assembly: "TestAsm");

        bool result = WhenProcessingProfilerNotifications();

        Assert.True(result);
    }

    // ── OnModuleLoad ──────────────────────────────────────

    [Fact]
    public void OnModuleLoad_WhenNotManagedInitialized_ReturnsEmpty()
    {
        _model.ManagedInitialized = false;

        WhenOnModuleLoad();

        ThenResolvedCountIs(0);
    }

    [Fact]
    public void OnModuleLoad_WhenManagedInitialized_FlushesAndRefreshes()
    {
        _model.ManagedInitialized = true;

        WhenOnModuleLoad();

        _corDebug.Received(1).FlushProcessState(_model.CorWrapper);
        _corDebug.Received(1).RefreshModules(_model.CorWrapper);
    }

    [Fact]
    public void OnModuleLoad_BindsPendingBreakpoints()
    {
        _model.ManagedInitialized = true;
        GivenPendingILBreakpoint(@"C:\src\Program.cs", line: 15, bpId: 3);
        _ = _bpService.TryBindBreakpoint(_model, @"C:\src\Program.cs", 15, 3).Returns(true);

        WhenOnModuleLoad();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, true);
        ThenResolvedBreakpointHasId(0, 3);
        ThenResolvedBreakpointHasLine(0, 15);
        ThenPendingILBreakpointCountIs(0);
    }

    [Fact]
    public void OnModuleLoad_WhenBindFails_LeavesInPending()
    {
        _model.ManagedInitialized = true;
        GivenPendingILBreakpoint(@"C:\src\Program.cs", line: 15, bpId: 3);
        _ = _bpService.TryBindBreakpoint(_model, @"C:\src\Program.cs", 15, 3).Returns(false);

        WhenOnModuleLoad();

        ThenResolvedCountIs(0);
        ThenPendingILBreakpointCountIs(1);
    }

    // ── TryBindManagedBreakpointsOnModuleLoad ─────────────

    [Fact]
    public void TryBindManagedBreakpointsOnModuleLoad_SendsBreakpointEventsForResolved()
    {
        _model.ManagedInitialized = true;
        GivenPendingILBreakpoint(@"C:\src\Program.cs", line: 15, bpId: 3);
        _ = _bpService.TryBindBreakpoint(_model, @"C:\src\Program.cs", 15, 3).Returns(true);

        WhenTryBindManagedBreakpointsOnModuleLoad();

        _server.Received(1).SendEvent(_transport, "breakpoint", Arg.Is<BreakpointEventBody>(b =>
            b.Reason == "changed" && b.Breakpoint.Id == 3));
    }

    [Fact]
    public void TryBindManagedBreakpointsOnModuleLoad_WhenExceptionThrown_HandlesGracefully()
    {
        _model.ManagedInitialized = true;
        _corDebug.When(c => c.FlushProcessState(Arg.Any<CorDebugWrapperModel>()))
            .Do(_ => throw new InvalidOperationException("flush failed"));

        WhenTryBindManagedBreakpointsOnModuleLoad();
    }

    // ── ProcessPendingManagedBreakpoints ──────────────────

    [Fact]
    public void ProcessPendingManagedBreakpoints_WhenHooksNotActive_AndManagedInitialized_ResolvesViaDAC()
    {
        _model.ProfilerHooksActive = false;
        _model.ManagedInitialized = true;
        _model.CoreClrPath = @"C:\clr\coreclr.dll";
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");
        GivenXclrDataReturnsAddress(token: 0x06000001, assembly: "Asm", address: 0x2000);
        GivenSetManagedCodeBreakpointSucceeds(address: 0x2000, bpId: 30);

        WhenProcessingPendingManagedBreakpoints();

        _server.Received().SendEvent(_transport, "breakpoint", Arg.Is<BreakpointEventBody>(b =>
            b.Breakpoint.Id == 1 && b.Breakpoint.Verified));
    }

    [Fact]
    public void ProcessPendingManagedBreakpoints_WhenHooksActive_SkipsDACResolution()
    {
        _model.ProfilerHooksActive = true;
        _model.ManagedInitialized = true;
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");

        WhenProcessingPendingManagedBreakpoints();

        _ = _corDebug.DidNotReceive().ResolveNativeEntryViaXclrData(
            Arg.Any<CorDebugWrapperModel>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public void ProcessPendingManagedBreakpoints_WhenNotManagedInitialized_SkipsDACResolution()
    {
        _model.ProfilerHooksActive = false;
        _model.ManagedInitialized = false;
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");

        WhenProcessingPendingManagedBreakpoints();

        _ = _corDebug.DidNotReceive().ResolveNativeEntryViaXclrData(
            Arg.Any<CorDebugWrapperModel>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    // ── ProcessPendingManagedBreakpoints: exception in resolver ──

    [Fact]
    public void ProcessPendingManagedBreakpoints_WhenResolverThrows_HandlesGracefully()
    {
        _model.ProfilerHooksActive = false;
        _model.ManagedInitialized = true;
        _model.CoreClrPath = @"C:\clr\coreclr.dll";
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");
        _corDebug.When(c => c.FlushProcessState(Arg.Any<CorDebugWrapperModel>()))
            .Do(_ => throw new InvalidOperationException("DAC flush failed"));

        WhenProcessingPendingManagedBreakpoints();
    }

    // ── StartDeferredBreakpointPoller ─────────────────────

    [Fact]
    public void StartDeferredBreakpointPoller_SetsDisposeAction()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");

        WhenStartingDeferredBreakpointPoller();

        Assert.NotNull(_model.DisposeAction);
    }

    [Fact]
    public void StartDeferredBreakpointPoller_DisposeActionCleansUp()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");

        WhenStartingDeferredBreakpointPoller();
        _model.DisposeAction!.Invoke();

        Assert.True(_model.Terminated);
    }

    #region Given

    private void GivenDeferredBreakpoint(string filePath, int line, int token, int ilOffset, int bpId, string assembly)
    {
        _model.DeferredManagedBreakpoints.Add(
            new DeferredManagedBreakpoint(filePath, line, token, ilOffset, bpId, assembly));
        _model.RebuildDeferredBreakpointIndex();
    }

    private void GivenPendingILBreakpoint(string filePath, int line, int bpId) =>
        _model.PendingILBreakpoints.Add(new PendingManagedBreakpoint(filePath, line, bpId));

    private void GivenXclrDataReturnsAddress(int token, string assembly, ulong address) =>
        _ = _corDebug.ResolveNativeEntryViaXclrData(_model.CorWrapper, token, assembly)
            .Returns(address);

    private void GivenSetManagedCodeBreakpointSucceeds(ulong address, uint bpId) =>
        _ = _bpService.SetManagedCodeBreakpoint(_model, address, Arg.Any<string>(), Arg.Any<int>())
            .Returns(bpId);

    private void GivenSetManagedCodeBreakpointSucceedsWithSequential(uint[] bpIds)
    {
        int idx = 0;
        _ = _bpService.SetManagedCodeBreakpoint(
                _model, Arg.Any<ulong>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(_ => idx < bpIds.Length ? (uint?)bpIds[idx++] : null);
    }

    private void GivenSetManagedCodeBreakpointFails(ulong address) =>
        _ = _bpService.SetManagedCodeBreakpoint(_model, address, Arg.Any<string>(), Arg.Any<int>())
            .Returns((uint?)null);

    private void GivenSetManagedCodeBreakpointFailsForAny() =>
        _ = _bpService.SetManagedCodeBreakpoint(_model, Arg.Any<ulong>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns((uint?)null);

    private void GivenRemoveBreakpointSucceeds(uint bpId) =>
        _dbgEng.RemoveBreakpoint(_model.Wrapper, bpId).Returns(true);

    private void GivenQueuedJitNotification(int token, ulong address, string assembly) =>
        _model.ProfilerNotifications.Enqueue(new JitNotification(token, address, 100, assembly));

    private void GivenQueuedEnter(int token, ulong bodyAddress, uint tid, string assembly) =>
        _model.ProfilerNotifications.Enqueue(new EnterNotification(token, bodyAddress, tid, assembly));

    private void GivenQueuedLeave(int token, uint tid, string assembly) =>
        _model.ProfilerNotifications.Enqueue(new LeaveNotification(token, tid, assembly));

    private void GivenQueuedTailcall(int token, uint tid, string assembly) =>
        _model.ProfilerNotifications.Enqueue(new TailcallNotification(token, tid, assembly));

    private void GivenJitMethodMapping(string assembly, int token, ulong codeStart, (int ILOffset, int NativeOffset)[] ilMap) =>
        _model.JitMethodMappings[(token, assembly)] = new JitMethodMapping(
            codeStart, [.. ilMap.Select(m => (m.ILOffset, m.NativeOffset))]);

    private void GivenManagedBpPlanWithSites(int token, string assembly,
        (int bpId, int ilOffset, int line)[] sites)
    {
        ManagedMethodBreakpointPlan plan = new()
        {
            MethodToken = token,
            AssemblyName = assembly,
        };
        foreach ((int bpId, int ilOffset, int line) in sites)
        {
            plan.Sites.Add(new MethodBreakpointSite
            {
                BpId = bpId,
                ILOffset = ilOffset,
                FilePath = @"C:\src\A.cs",
                Line = line,
            });
        }
        _model.ManagedBpPlans[(token, assembly)] = plan;
    }

    private void GivenProfilerAckEvent() =>
        _model.ProfilerAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

    #endregion

    #region When

    private void WhenResolvingDeferredBreakpoints() =>
        _results = _testee.TryResolveDeferredBreakpoints(_model);

    private bool WhenProcessingProfilerNotifications() =>
        _testee.ProcessProfilerNotifications(_model);

    private void WhenOnModuleLoad() =>
        _results = _testee.OnModuleLoad(_model);

    private void WhenTryBindManagedBreakpointsOnModuleLoad() =>
        _testee.TryBindManagedBreakpointsOnModuleLoad(_model);

    private void WhenProcessingPendingManagedBreakpoints() =>
        _testee.ProcessPendingManagedBreakpoints(_model);

    private void WhenStartingDeferredBreakpointPoller() =>
        _testee.StartDeferredBreakpointPoller(_model);

    #endregion

    #region Then

    private void ThenResolvedCountIs(int expected) =>
        Assert.Equal(expected, _results!.Length);

    private void ThenResolvedBreakpointIsVerified(int index, bool expected) =>
        Assert.Equal(expected, _results![index].Verified);

    private void ThenResolvedBreakpointHasId(int index, int expected) =>
        Assert.Equal(expected, _results![index].Id);

    private void ThenResolvedBreakpointHasLine(int index, int expected) =>
        Assert.Equal(expected, _results![index].Line);

    private void ThenResolvedBreakpointHasMessage(int index, string expected) =>
        Assert.Equal(expected, _results![index].Message);

    private void ThenDeferredBreakpointCountIs(int expected) =>
        Assert.Equal(expected, _model.DeferredManagedBreakpoints.Count);

    private void ThenDeferredBreakpointContainsToken(int token) =>
        Assert.Contains(_model.DeferredManagedBreakpoints, d => d.MethodToken == token);

    private void ThenPendingILBreakpointCountIs(int expected) =>
        Assert.Equal(expected, _model.PendingILBreakpoints.Count);

    private void ThenProfilerAckEventIsSignaled() =>
        Assert.True(_model.ProfilerAckEvent!.WaitOne(0));

    private void ThenManagedBpPlanExists(int token, string assembly) =>
        Assert.True(_model.ManagedBpPlans.ContainsKey((token, assembly)),
            $"Expected ManagedBpPlans to contain ({token:X8}, {assembly})");

    private void ThenManagedBpPlanDoesNotExist(int token, string assembly) =>
        Assert.False(_model.ManagedBpPlans.ContainsKey((token, assembly)),
            $"Expected ManagedBpPlans to NOT contain ({token:X8}, {assembly})");

    private void ThenManagedBpPlanHasSiteCount(int token, string assembly, int expected) =>
        Assert.Equal(expected, _model.ManagedBpPlans[(token, assembly)].Sites.Count);

    private void ThenActiveMethodBreakpointExists(int token, string assembly) =>
        Assert.True(_model.ActiveMethodBreakpoints.ContainsKey((token, assembly)));

    private void ThenActiveMethodBreakpointDoesNotExist(int token, string assembly) =>
        Assert.False(_model.ActiveMethodBreakpoints.ContainsKey((token, assembly)));

    private void ThenActiveMethodBreakpointActivationCount(int token, string assembly, int expected) =>
        Assert.Equal(expected, _model.ActiveMethodBreakpoints[(token, assembly)].ActivationCount);

    #endregion

    #region Misc

    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly LogStore _logStore;
    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly DapServerModel _transport;
    private readonly IDbgEngWrapper _dbgEng = Substitute.For<IDbgEngWrapper>();
    private readonly ICorDebugWrapper _corDebug = Substitute.For<ICorDebugWrapper>();
    private readonly IManagedBreakpointService _bpService = Substitute.For<IManagedBreakpointService>();
    private readonly NativeDebuggerModel _model;
    private readonly ManagedBreakpointResolverService _testee;

    private Breakpoint[]? _results;

    public ManagedBreakpointResolverServiceTests()
    {
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test-resolver.log"));
        _model = new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        };
        _testee = new ManagedBreakpointResolverService(
            _log, _logStore, _server, _transport, _dbgEng, _corDebug, _bpService);
    }

    public void Dispose()
    {
        _model.ProfilerAckEvent?.Dispose();
        try { _model.Commands.CompleteAdding(); } catch (ObjectDisposedException) { }
        try { _model.Commands.Dispose(); } catch (ObjectDisposedException) { }
        try { _model.Stopped.Dispose(); } catch (ObjectDisposedException) { }
        try { _model.EngineReady.Dispose(); } catch (ObjectDisposedException) { }
    }

    #endregion
}
