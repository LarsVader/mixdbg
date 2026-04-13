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
        // Model has no deferred breakpoints by default.
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

    // ── HandleJitNotifications ────────────────────────────

    [Fact]
    public void HandleJitNotifications_WhenNoDeferredBPs_ReturnsEmpty()
    {
        GivenJitNotification(token: 0x06000001, address: 0x1000, assembly: "Asm");

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(0);
    }

    [Fact]
    public void HandleJitNotifications_WhenNoNotifications_ReturnsEmpty()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(0);
    }

    [Fact]
    public void HandleJitNotifications_WhenTokenAndAssemblyMatch_SetsHardwareBP()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "TestAssembly");
        GivenSetManagedCodeBreakpointSucceeds(address: 0x5000, bpId: 20);

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, true);
        ThenResolvedBreakpointHasId(0, 1);
        ThenDeferredBreakpointCountIs(0);
    }

    [Fact]
    public void HandleJitNotifications_WhenHooksActiveAndMappingExists_SkipsBP()
    {
        _model.ProfilerHooksActive = true;
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 5, bpId: 1, assembly: "TestAssembly");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "TestAssembly");
        GivenJitMethodMapping("TestAssembly", 0x06000001, codeStart: 0x5000, ilMap: [(0, 0), (5, 10)]);

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(0);
        _ = _bpService.DidNotReceive().SetManagedCodeBreakpoint(
            Arg.Any<NativeDebuggerModel>(), Arg.Any<ulong>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public void HandleJitNotifications_WhenHooksNotActive_SetsHardwareBP()
    {
        _model.ProfilerHooksActive = false;
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "TestAssembly");
        GivenSetManagedCodeBreakpointSucceeds(address: 0x5000, bpId: 20);

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, true);
    }

    [Fact]
    public void HandleJitNotifications_WhenHwBpFails_ReturnsUnverified()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "TestAssembly");
        GivenSetManagedCodeBreakpointFails(address: 0x5000);

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, false);
        ThenResolvedBreakpointHasMessage(0, "Failed to set hardware breakpoint");
    }

    [Fact]
    public void HandleJitNotifications_WhenResolved_SetsProfilerAckEvent()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "TestAssembly");
        GivenSetManagedCodeBreakpointSucceeds(address: 0x5000, bpId: 20);
        _model.ProfilerAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        WhenHandlingJitNotifications();

        ThenProfilerAckEventIsSignaled();
    }

    [Fact]
    public void HandleJitNotifications_WhenNoMatch_DoesNotSetAckEvent()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm1");
        GivenJitNotification(token: 0x06000099, address: 0x5000, assembly: "OtherAsm");

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(0);
    }

    [Fact]
    public void HandleJitNotifications_CaseInsensitiveAssemblyMatch()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "testassembly");
        GivenSetManagedCodeBreakpointSucceeds(address: 0x5000, bpId: 20);

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(1);
        ThenResolvedBreakpointIsVerified(0, true);
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

        // Should not throw.
        WhenTryBindManagedBreakpointsOnModuleLoad();
    }

    // ── ProcessPendingManagedBreakpoints ──────────────────

    [Fact]
    public void ProcessPendingManagedBreakpoints_WhenDeferredAndJitNotifications_HandlesThem()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "Asm");
        GivenSetManagedCodeBreakpointSucceeds(address: 0x5000, bpId: 20);

        WhenProcessingPendingManagedBreakpoints();

        _server.Received().SendEvent(_transport, "breakpoint", Arg.Any<BreakpointEventBody>());
    }

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
        // No JIT notifications, so HandleJitNotifications won't produce results either.

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

    // ── HandleEnterBreakpoint ─────────────────────────────

    [Fact]
    public void HandleEnterBreakpoint_WhenHooksNotActive_ReturnsFalse()
    {
        _model.ProfilerHooksActive = false;
        _model.PendingEnterBreakpoint = true;

        bool result = WhenHandlingEnterBreakpoint();

        Assert.False(result);
    }

    [Fact]
    public void HandleEnterBreakpoint_WhenNoPendingEnter_ReturnsFalse()
    {
        _model.ProfilerHooksActive = true;
        _model.PendingEnterBreakpoint = false;

        bool result = WhenHandlingEnterBreakpoint();

        Assert.False(result);
    }

    [Fact]
    public void HandleEnterBreakpoint_WhenMappingExists_SetsTransientBPAtMappedAddress()
    {
        GivenEnterBreakpointState(token: 0x06000001, address: 0x7FFF0000, assembly: "TestAssembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 10, bpId: 1, assembly: "TestAssembly");
        GivenJitMethodMapping("TestAssembly", 0x06000001, codeStart: 0x1000, ilMap: [(0, 0), (10, 20)]);

        bool result = WhenHandlingEnterBreakpoint();

        Assert.True(result);
        // IL offset 10 maps to native offset 20, so address = 0x1000 + 20 = 0x1014.
        _bpService.Received(1).SetTransientBreakpoint(_model, 0x1000 + 20, @"C:\src\A.cs", 10);
    }

    [Fact]
    public void HandleEnterBreakpoint_WhenNoMapping_FallsBackToEnterAddress()
    {
        GivenEnterBreakpointState(token: 0x06000001, address: 0x7FFF0000, assembly: "TestAssembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 10, bpId: 1, assembly: "TestAssembly");
        // No JitMethodMappings entry.

        bool result = WhenHandlingEnterBreakpoint();

        Assert.True(result);
        _bpService.Received(1).SetTransientBreakpoint(_model, 0x7FFF0000, @"C:\src\A.cs", 10);
    }

    [Fact]
    public void HandleEnterBreakpoint_ACKsProfiler()
    {
        GivenEnterBreakpointState(token: 0x06000001, address: 0x7FFF0000, assembly: "TestAssembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");

        _ = WhenHandlingEnterBreakpoint();

        ThenProfilerAckEventIsSignaled();
    }

    [Fact]
    public void HandleEnterBreakpoint_WhenMultipleDeferredBpsMatchSameMethod_SetsAllTransientBps()
    {
        GivenEnterBreakpointState(token: 0x06000001, address: 0x7FFF0000, assembly: "TestAssembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 10, bpId: 1, assembly: "TestAssembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 20, token: 0x06000001, ilOffset: 30, bpId: 2, assembly: "TestAssembly");
        GivenJitMethodMapping("TestAssembly", 0x06000001, codeStart: 0x1000, ilMap: [(0, 0), (10, 20), (30, 50)]);

        _ = WhenHandlingEnterBreakpoint();

        _bpService.Received(1).SetTransientBreakpoint(_model, 0x1000 + 20, @"C:\src\A.cs", 10);
        _bpService.Received(1).SetTransientBreakpoint(_model, 0x1000 + 50, @"C:\src\A.cs", 20);
    }

    [Fact]
    public void HandleEnterBreakpoint_WhenNoMatch_RehooksProfiler()
    {
        GivenEnterBreakpointState(token: 0x06000099, address: 0x7FFF0000, assembly: "TestAssembly");
        // No matching deferred breakpoint for token 0x06000099.

        bool result = WhenHandlingEnterBreakpoint();

        Assert.True(result);
        ThenProfilerAckEventIsSignaled();
        ThenProfilerRehookEventIsSignaled();
    }

    [Fact]
    public void HandleEnterBreakpoint_WhenMatch_DoesNotRehook()
    {
        GivenEnterBreakpointState(token: 0x06000001, address: 0x7FFF0000, assembly: "TestAssembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");

        _ = WhenHandlingEnterBreakpoint();

        ThenProfilerRehookEventIsNotSignaled();
    }

    [Fact]
    public void HandleEnterBreakpoint_ClearsPendingEnterFlag()
    {
        GivenEnterBreakpointState(token: 0x06000001, address: 0x7FFF0000, assembly: "TestAssembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");

        _ = WhenHandlingEnterBreakpoint();

        Assert.False(_model.PendingEnterBreakpoint);
    }

    [Fact]
    public void HandleEnterBreakpoint_CaseInsensitiveAssemblyMatch()
    {
        GivenEnterBreakpointState(token: 0x06000001, address: 0x7FFF0000, assembly: "testassembly");
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "TestAssembly");

        bool result = WhenHandlingEnterBreakpoint();

        Assert.True(result);
        _bpService.Received(1).SetTransientBreakpoint(_model, 0x7FFF0000, @"C:\src\A.cs", 10);
    }

    // ── HandleJitNotifications: duplicate deferred match ──

    [Fact]
    public void HandleJitNotifications_WhenTwoDeferredForSameToken_MatchesBoth()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: "Asm");
        GivenDeferredBreakpoint(@"C:\src\B.cs", line: 20, token: 0x06000001, ilOffset: 5, bpId: 2, assembly: "Asm");
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "Asm");
        GivenSetManagedCodeBreakpointSucceeds(address: 0x5000, bpId: 20);

        WhenHandlingJitNotifications();

        // Both deferred BPs match the same JIT notification.
        ThenResolvedCountIs(2);
        ThenResolvedBreakpointHasId(0, 1);
        ThenResolvedBreakpointHasId(1, 2);
        ThenDeferredBreakpointCountIs(0);
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

        // Should not throw — exception is swallowed by ResolveAndNotify.
        WhenProcessingPendingManagedBreakpoints();
    }

    // ── HandleJitNotifications: deferred with null assembly ──

    [Fact]
    public void HandleJitNotifications_WhenDeferredAssemblyIsNull_SkipsBP()
    {
        GivenDeferredBreakpoint(@"C:\src\A.cs", line: 10, token: 0x06000001, ilOffset: 0, bpId: 1, assembly: null!);
        _model.DeferredManagedBreakpoints[0] = new DeferredManagedBreakpoint(
            @"C:\src\A.cs", 10, 0x06000001, 0, 1, null);
        GivenJitNotification(token: 0x06000001, address: 0x5000, assembly: "Asm");

        WhenHandlingJitNotifications();

        ThenResolvedCountIs(0);
        ThenDeferredBreakpointCountIs(1);
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

    private void GivenSetManagedCodeBreakpointFails(ulong address) =>
        _ = _bpService.SetManagedCodeBreakpoint(_model, address, Arg.Any<string>(), Arg.Any<int>())
            .Returns((uint?)null);

    private void GivenJitNotification(int token, ulong address, string assembly) =>
        _model.JitNotifications.Enqueue(new JitNotification(token, address, 100, assembly));

    private void GivenJitMethodMapping(string assembly, int token, ulong codeStart, (int ILOffset, int NativeOffset)[] ilMap)
    {
        string key = $"{assembly}:{token:X8}";
        _model.JitMethodMappings[key] = new JitMethodMapping
        {
            CodeStart = codeStart,
            ILToNativeMap = [.. ilMap.Select(m => (m.ILOffset, m.NativeOffset))],
        };
    }

    private void GivenEnterBreakpointState(int token, ulong address, string assembly)
    {
        _model.ProfilerHooksActive = true;
        _model.PendingEnterBreakpoint = true;
        _model.EnterBreakpointToken = token;
        _model.EnterBreakpointAddress = address;
        _model.EnterBreakpointAssembly = assembly;
        _model.ProfilerAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _model.ProfilerRehookEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
    }

    #endregion

    #region When

    private void WhenResolvingDeferredBreakpoints() =>
        _results = _testee.TryResolveDeferredBreakpoints(_model);

    private void WhenHandlingJitNotifications() =>
        _results = _testee.HandleJitNotifications(_model);

    private void WhenOnModuleLoad() =>
        _results = _testee.OnModuleLoad(_model);

    private void WhenTryBindManagedBreakpointsOnModuleLoad() =>
        _testee.TryBindManagedBreakpointsOnModuleLoad(_model);

    private void WhenProcessingPendingManagedBreakpoints() =>
        _testee.ProcessPendingManagedBreakpoints(_model);

    private bool WhenHandlingEnterBreakpoint() =>
        _testee.HandleEnterBreakpoint(_model);

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

    private void ThenProfilerRehookEventIsSignaled() =>
        Assert.True(_model.ProfilerRehookEvent!.WaitOne(0));

    private void ThenProfilerRehookEventIsNotSignaled() =>
        Assert.False(_model.ProfilerRehookEvent?.WaitOne(0) ?? false);

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
        _model.ProfilerRehookEvent?.Dispose();
        try { _model.Commands.CompleteAdding(); } catch (ObjectDisposedException) { }
        try { _model.Commands.Dispose(); } catch (ObjectDisposedException) { }
        try { _model.Stopped.Dispose(); } catch (ObjectDisposedException) { }
        try { _model.EngineReady.Dispose(); } catch (ObjectDisposedException) { }
    }

    #endregion
}
