using MixDbg.Models;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Services;
using MixDbg.Services.Interfaces;

using NSubstitute;

namespace MixDbg.Tests;

public sealed class EngineLifecycleServiceTests : IDisposable
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

    // ── Break ──────────────────────────────────────────────

    [Fact]
    public void Break_WhenCalled_SetsPauseRequested()
    {
        WhenBreaking();

        ThenPauseRequestedIsTrue();
    }

    [Fact]
    public void Break_WhenInWaitForEvent_CallsSetInterruptDirectly()
    {
        _model.InWaitForEvent = true;

        WhenBreaking();

        ThenSetInterruptWasCalled();
    }

    [Fact]
    public void Break_WhenNotInWaitForEvent_SetsInterruptRequestedFlag()
    {
        _model.InWaitForEvent = false;

        WhenBreaking();

        Assert.True(_model.Wrapper.InterruptRequested);
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

    // ── Terminate_WhenTargetExited ────────────────────────

    [Fact]
    public void Terminate_WhenTargetExited_StillCallsTerminateSession()
    {
        GivenTargetExited();

        WhenTerminating();

        ThenTerminateSessionWasCalled();
    }

    [Fact]
    public void Terminate_WhenTargetExitedAndTerminateThrows_DoesNotThrow()
    {
        GivenTargetExited();
        GivenTerminateSessionThrows();

        WhenTerminating();

        ThenModelIsTerminated();
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

    // ── StartEngineThread ──────────────────────────────────

    [Fact]
    public void StartEngineThread_WhenWaitForEventFails_TerminatesLoop()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();

        ThenEngineThreadExits();
    }

    [Fact]
    public void StartEngineThread_WhenInitThrows_SetsEngineInitError()
    {
        GivenWrapperCreateModelReturns();
        GivenCreateEngineThrows(new InvalidOperationException("COM init failed"));

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();

        ThenEngineInitErrorIs<InvalidOperationException>();
    }

    [Fact]
    public void StartEngineThread_WhenInitThrows_SendsTerminatedEvent()
    {
        GivenWrapperCreateModelReturns();
        GivenCreateEngineThrows(new InvalidOperationException("COM init failed"));

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenTerminatedEventWasSent();
    }

    [Fact]
    public void StartEngineThread_WhenInitThrows_SendsOutputEventWithError()
    {
        GivenWrapperCreateModelReturns();
        GivenCreateEngineThrows(new InvalidOperationException("COM init failed"));

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenOutputEventWasSentWithCategory("stderr");
    }

    [Fact]
    public void StartEngineThread_WhenInitSucceeds_SignalsEngineReady()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();

        ThenEngineReadyIsSet();
    }

    // ── EngineLoopStep: WaitForEvent failed ────────────────

    [Fact]
    public void EngineLoopStep_WhenWaitForEventFails_ExitsLoop()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();

        ThenEngineThreadExits();
    }

    // ── EngineLoopStep: TargetExited ───────────────────────

    [Fact]
    public void EngineLoopStep_WhenTargetExited_SendsTerminatedEvent()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventSetsTargetExitedThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Exit"));

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenTerminatedEventWasSent();
    }

    // ── EngineLoopStep: pre-configDone ─────────────────────

    [Fact]
    public void EngineLoopStep_WhenNotConfigDone_ProcessesCommandsAndContinues()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Initial break"));
        // ProcessCommandsUntilResume will pick up the queued command.
        // After the resume command, execution status is Go so loop exits ProcessCommands.
        GivenGetExecutionStatusReturns(EngineExecutionStatus.Go);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        // Queue a command to resume (and terminate the loop so test ends).
        WhenQueuingCommand(() => _model.Terminated = true);
        WhenWaitingForEngineThreadToExit();

        ThenModelIsTerminated();
    }

    // ── EngineLoopStep: CLR init ───────────────────────────

    [Fact]
    public void EngineLoopStep_WhenClrLoadedAndNotInitialized_CallsTryInitializeManaged()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenClrLoadedButNotInitialized();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Module load"));
        GivenNoStopReason();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenTryInitializeManagedWasCalled();
    }

    [Fact]
    public void EngineLoopStep_WhenClrNotLoaded_DoesNotCallTryInitializeManaged()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Break"));
        GivenNoStopReason();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenTryInitializeManagedWasNotCalled();
    }

    // ── EngineLoopStep: managed BP resolution ──────────────

    [Fact]
    public void EngineLoopStep_WhenConfigDone_CallsProcessPendingManagedBreakpoints()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Break"));
        GivenNoStopReason();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenProcessPendingManagedBreakpointsWasCalled();
    }

    // ── EngineLoopStep: enter breakpoint ───────────────────

    [Fact]
    public void EngineLoopStep_WhenProfilerNotificationHandled_AutoContinues()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Break"));
        GivenProcessProfilerNotificationsReturns(true);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.Go);
    }

    // ── EngineLoopStep: stop reasons ───────────────────────

    [Fact]
    public void EngineLoopStep_WhenHitUserBreakpoint_SendsStoppedEventWithBreakpointReason()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenWaitForEventSucceedsThenTerminates();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Breakpoint"));
        GivenProcessProfilerNotificationsReturns(false);
        GivenHitUserBreakpoint();
        GivenGetCurrentThreadIdReturns(42);
        // ProcessCommandsUntilResume needs a command that sets Go.
        GivenGetExecutionStatusReturns(EngineExecutionStatus.Go);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        // Queue resume command to exit ProcessCommands.
        WhenQueuingCommand(() => { });
        WhenWaitingForEngineThreadToExit();

        ThenStoppedEventWasSentWithReason("breakpoint");
    }

    [Fact]
    public void EngineLoopStep_WhenStepping_SendsStoppedEventWithStepReason()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenWaitForEventSucceedsThenTerminates();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Step"));
        GivenProcessProfilerNotificationsReturns(false);
        GivenStepping();
        GivenGetCurrentThreadIdReturns(42);
        GivenGetExecutionStatusReturns(EngineExecutionStatus.Go);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenQueuingCommand(() => { });
        WhenWaitingForEngineThreadToExit();

        ThenStoppedEventWasSentWithReason("step");
    }

    [Fact]
    public void EngineLoopStep_WhenPauseRequested_SendsStoppedEventWithPauseReason()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenWaitForEventSucceedsThenTerminates();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Pause"));
        GivenProcessProfilerNotificationsReturns(false);
        GivenPauseRequested();
        GivenGetCurrentThreadIdReturns(42);
        GivenGetExecutionStatusReturns(EngineExecutionStatus.Go);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenQueuingCommand(() => { });
        WhenWaitingForEngineThreadToExit();

        ThenStoppedEventWasSentWithReason("pause");
    }

    // ── EngineLoopStep: auto-continue ──────────────────────

    [Fact]
    public void EngineLoopStep_WhenNoStopReason_AutoContinues()
    {
        GivenWrapperCreateModelReturns();
        GivenConfigDone();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "System break"));
        GivenProcessProfilerNotificationsReturns(false);
        GivenNoStopReason();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus.Go);
    }

    // ── DetermineStopReason priority ───────────────────────
    // Priority logic is tested in StepResolutionServiceTests.

    // ── DetermineStopReason delegation to StepResolutionService ────────
    // Detailed DetermineStopReason logic is tested in StepResolutionServiceTests.
    // These verify EngineLifecycleService correctly delegates and forwards the result.

    // ── AttachOrCreateProcess ──────────────────────────────

    [Fact]
    public void StartEngineThread_WhenAttach_CallsAttachProcess()
    {
        GivenWrapperCreateModelReturns();
        GivenAttachMode(pid: 1234);
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenAttachProcessWasCalled(1234);
    }

    [Fact]
    public void StartEngineThread_WhenAttach_DoesNotCallCreateProcess()
    {
        GivenWrapperCreateModelReturns();
        GivenAttachMode(pid: 1234);
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenCreateProcessWasNotCalled();
    }

    [Fact]
    public void StartEngineThread_WhenProfilerInitNeverCompletes_SetsEngineInitErrorAndSkipsDbgEngAttach()
    {
        GivenWrapperCreateModelReturns();
        GivenAttachModeWithoutProfilerReady(pid: 1234);

        WhenStartingEngineThread();
        // The deadline is 5 s; allow extra slack for thread startup + soft-fail bail.
        Assert.True(_model.EngineReady.Wait(TimeSpan.FromSeconds(15)),
            "EngineReady not signaled within 15 s");
        WhenWaitingForEngineThreadToExit();

        Assert.NotNull(_model.EngineInitError);
        Assert.Contains("InitializeForAttach", _model.EngineInitError!.Message);
        // Engine must NOT have called dbgeng AttachProcess — that's the whole
        // point of the wait gate.
        _wrapper.DidNotReceive().AttachProcess(Arg.Any<DbgEngWrapperModel>(), Arg.Any<uint>());
    }

    [Fact]
    public void StartEngineThread_WhenLaunch_CallsCreateProcess()
    {
        GivenWrapperCreateModelReturns();
        GivenLaunchMode("C:\\app.exe");
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenCreateProcessWasCalled("C:\\app.exe");
    }

    [Fact]
    public void StartEngineThread_WhenLaunchWithArgs_IncludesArgsInCommandLine()
    {
        GivenWrapperCreateModelReturns();
        GivenLaunchMode("C:\\app.exe", args: ["--flag", "value"]);
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenCreateProcessWasCalled("C:\\app.exe --flag value");
    }

    [Fact]
    public void StartEngineThread_WhenLaunchWithCwd_InitializesSymbolsWithCwd()
    {
        GivenWrapperCreateModelReturns();
        GivenLaunchMode("C:\\app.exe", cwd: "C:\\myproject");
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenInitializeSymbolsWasCalledWithSourcePath("C:\\myproject");
    }

    [Fact]
    public void StartEngineThread_WhenLaunch_CallsSetupProfilerPipe()
    {
        GivenWrapperCreateModelReturns();
        GivenLaunchMode("C:\\app.exe");
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenSetupProfilerPipeWasCalled();
    }

    [Fact]
    public void StartEngineThread_WhenLaunch_CallsStartProfilerReader()
    {
        GivenWrapperCreateModelReturns();
        GivenLaunchMode("C:\\app.exe");
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenStartProfilerReaderWasCalled();
    }

    // ── ProcessCommandsUntilResume ─────────────────────────

    [Fact]
    public void ProcessCommands_WhenExecutionStatusIsGo_Resumes()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Initial break"));
        // First call: Go -> resume immediately after the command.
        GivenGetExecutionStatusReturns(EngineExecutionStatus.Go);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenQueuingCommand(() => { }); // The no-op command + Go status -> resume.
        WhenWaitingForEngineThreadToExit();

        // If we got here, ProcessCommandsUntilResume exited correctly.
        Assert.True(true);
    }

    [Fact]
    public void ProcessCommands_WhenExecutionStatusIsBreak_ContinuesWaiting()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Initial break"));
        // First cmd: Break (keep waiting), second cmd: Go (resume).
        int callCount = 0;
        _ = _wrapper.GetExecutionStatus(Arg.Any<DbgEngWrapperModel>())
            .Returns(_ => ++callCount == 1 ? EngineExecutionStatus.Break : EngineExecutionStatus.Go);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenQueuingCommand(() => { }); // First: Break -> keep waiting.
        Thread.Sleep(50); // Let engine process first command.
        WhenQueuingCommand(() => { }); // Second: Go -> resume.
        WhenWaitingForEngineThreadToExit();

        Assert.True(true);
    }

    [Fact]
    public void ProcessCommands_WhenCollectionCompleted_ExitsLoop()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Initial break"));
        GivenGetExecutionStatusReturns(EngineExecutionStatus.Break);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        // Complete the collection to trigger InvalidOperationException in Take().
        Thread.Sleep(50);
        _model.Commands.CompleteAdding();
        WhenWaitingForEngineThreadToExit();

        Assert.True(true);
    }

    [Fact]
    public void ProcessCommands_WhenTerminated_ExitsLoop()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Initial break"));
        // GetExecutionStatus returns Break so ProcessCommands loops.
        // But model is terminated, so the while-loop exits.
        GivenGetExecutionStatusReturns(EngineExecutionStatus.Break);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        // Terminate the model to break the outer while loop.
        WhenQueuingCommand(() => _model.Terminated = true);
        WhenWaitingForEngineThreadToExit();

        ThenModelIsTerminated();
    }

    [Fact]
    public void ProcessCommands_WhenExecutionStatusIsNoDebuggee_ContinuesWaiting()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventSucceedsThenFails();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Initial break"));
        int callCount = 0;
        _ = _wrapper.GetExecutionStatus(Arg.Any<DbgEngWrapperModel>())
            .Returns(_ => ++callCount == 1 ? EngineExecutionStatus.NoDebuggee : EngineExecutionStatus.Go);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenQueuingCommand(() => { }); // NoDebuggee -> keep waiting.
        Thread.Sleep(50);
        WhenQueuingCommand(() => { }); // Go -> resume.
        WhenWaitingForEngineThreadToExit();

        Assert.True(true);
    }

    // ── CreateEngine ───────────────────────────────────────

    [Fact]
    public void CreateEngine_WhenCalled_InitializesSymbols()
    {
        GivenWrapperCreateModelReturns();
        GivenSymbolPath("srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols");
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        ThenInitializeSymbolsWasCalledWithSymbolPath("srv*C:\\Symbols*https://msdl.microsoft.com/download/symbols");
    }

    [Fact]
    public void CreateEngine_WhenCalled_SetsWrapperOnModel()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();

        ThenModelWrapperIsTheCreatedOne();
    }

    // ── OnLoadModule (via wrapper events) ──────────────────

    [Fact]
    public void OnLoadModule_WhenCoreclrLoads_SetsClrLoaded()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent("coreclr", @"C:\dotnet\coreclr.dll", 0x7FF00000);
        WhenWaitingForEngineThreadToExit();

        ThenClrLoadedIsTrue();
        ThenCoreClrPathIs(@"C:\dotnet\coreclr.dll");
        ThenCoreClrBaseAddressIs(0x7FF00000UL);
    }

    [Fact]
    public void OnLoadModule_WhenCoreclrAlreadyLoaded_DoesNotOverwrite()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        // Fire the first coreclr load.
        WhenFiringLoadModuleEvent("coreclr", @"C:\first\coreclr.dll", 0x1000);
        // Fire again — should not overwrite.
        WhenFiringLoadModuleEvent("coreclr", @"C:\second\coreclr.dll", 0x2000);
        WhenWaitingForEngineThreadToExit();

        ThenCoreClrPathIs(@"C:\first\coreclr.dll");
        ThenCoreClrBaseAddressIs(0x1000UL);
    }

    [Fact]
    public void OnLoadModule_WhenNonCoreclr_DoesNotSetClrLoaded()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent("ntdll", @"C:\Windows\ntdll.dll", 0x7FF00000);
        WhenWaitingForEngineThreadToExit();

        ThenClrLoadedIsFalse();
    }

    [Fact]
    public void OnLoadModule_WhenModNameIsNull_DoesNotSetClrLoaded()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent(null, @"C:\something.dll", 0x1000);
        WhenWaitingForEngineThreadToExit();

        ThenClrLoadedIsFalse();
    }

    [Fact]
    public void OnLoadModule_WhenManagedInitializedAndDllLoads_CallsTryBindManagedBreakpoints()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);
        GivenManagedInitialized();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent("MyLib", @"C:\MyLib.dll", 0x1000);
        WhenWaitingForEngineThreadToExit();

        ThenTryBindManagedBreakpointsOnModuleLoadWasCalled();
    }

    [Fact]
    public void OnLoadModule_WhenManagedInitializedAndExeLoads_CallsTryBindManagedBreakpoints()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);
        GivenManagedInitialized();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent("MyApp", @"C:\MyApp.exe", 0x2000);
        WhenWaitingForEngineThreadToExit();

        ThenTryBindManagedBreakpointsOnModuleLoadWasCalled();
    }

    [Fact]
    public void OnLoadModule_WhenNotManagedInitialized_DoesNotCallTryBind()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent("MyLib", @"C:\MyLib.dll", 0x1000);
        WhenWaitingForEngineThreadToExit();

        ThenTryBindManagedBreakpointsOnModuleLoadWasNotCalled();
    }

    [Fact]
    public void OnLoadModule_WhenManagedInitializedButImageIsNull_DoesNotCallTryBind()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);
        GivenManagedInitialized();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent("MyLib", null, 0x1000);
        WhenWaitingForEngineThreadToExit();

        ThenTryBindManagedBreakpointsOnModuleLoadWasNotCalled();
    }

    [Fact]
    public void OnLoadModule_WhenManagedInitializedAndNonDllExe_DoesNotCallTryBind()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);
        GivenManagedInitialized();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringLoadModuleEvent("ntdll", @"C:\Windows\ntdll.sys", 0x1000);
        WhenWaitingForEngineThreadToExit();

        ThenTryBindManagedBreakpointsOnModuleLoadWasNotCalled();
    }

    // ── OnExitProcess (via wrapper events) ─────────────────

    [Fact]
    public void OnExitProcess_WhenFired_SetsTargetExited()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringExitProcessEvent(0);
        WhenWaitingForEngineThreadToExit();

        ThenTargetExitedIsTrue();
    }

    [Fact]
    public void OnExitProcess_WhenFired_SendsOutputEvent()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringExitProcessEvent(42);
        WhenWaitingForEngineThreadToExit();

        ThenOutputEventWasSentWithCategory("console");
    }

    // ── OnCreateProcess (via wrapper events) ───────────────

    [Fact]
    public void OnCreateProcess_WhenFired_SendsOutputEvent()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringCreateProcessEvent("MyApp.exe");
        WhenWaitingForEngineThreadToExit();

        ThenOutputEventWasSentWithCategory("console");
    }

    // ── OnClrNotification (via wrapper events) ─────────────

    [Fact]
    public void OnClrNotification_WhenConfigDoneAndDeferredBPs_SetsClrNotificationShouldBreak()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);
        GivenConfigDone();
        GivenDeferredManagedBreakpointExists();

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringClrNotificationEvent();
        WhenWaitingForEngineThreadToExit();

        ThenClrNotificationShouldBreakIsTrue();
    }

    [Fact]
    public void OnClrNotification_WhenNotConfigDone_DoesNotSetClrNotificationShouldBreak()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);
        // ConfigDone is false by default.

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringClrNotificationEvent();
        WhenWaitingForEngineThreadToExit();

        ThenClrNotificationShouldBreakIsFalse();
    }

    [Fact]
    public void OnClrNotification_WhenNoDeferredBPs_DoesNotSetClrNotificationShouldBreak()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);
        GivenConfigDone();
        // No deferred BPs.

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringClrNotificationEvent();
        WhenWaitingForEngineThreadToExit();

        ThenClrNotificationShouldBreakIsFalse();
    }

    // ── OnBreakpointHit (via wrapper events) ───────────────

    [Fact]
    public void OnBreakpointHit_WhenFired_CallsHandleBreakpointHit()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringBreakpointHitEvent(7);
        WhenWaitingForEngineThreadToExit();

        ThenHandleBreakpointHitWasCalledWith(7);
    }

    // ── OnExceptionBreakpoint (via wrapper events) ─────────

    [Fact]
    public void OnExceptionBreakpoint_WhenFired_CallsHandleExceptionBreakpoint()
    {
        GivenWrapperCreateModelReturns();
        GivenWaitForEventReturns(WaitForEventResult.Failed);

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenFiringExceptionBreakpointEvent(0xDEADBEEF);
        WhenWaitingForEngineThreadToExit();

        ThenHandleExceptionBreakpointWasCalledWith(0xDEADBEEF);
    }

    // ── EngineLoopStep: WaitForEvent timeout ───────────────

    [Fact]
    public void EngineLoopStep_WhenWaitForEventTimesOut_ContinuesLoop()
    {
        GivenWrapperCreateModelReturns();
        GivenLaunchMode("C:\\app.exe");
        GivenConfigDone();
        GivenGetLastEventInfoReturns(new EngineEventInfo(0, 1, 1, "Timeout"));
        GivenProcessProfilerNotificationsReturns(false);
        GivenNoStopReason();
        int callCount = 0;
        _ = _wrapper.WaitForEvent(Arg.Any<DbgEngWrapperModel>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1) return WaitForEventResult.Timeout;
                _model.Terminated = true; // Stop loop on second iteration.
                return WaitForEventResult.Failed;
            });

        WhenStartingEngineThread();
        WhenWaitingForEngineReady();
        WhenWaitingForEngineThreadToExit();

        // Timeout is not Failed, so loop should have continued (callCount >= 2).
        Assert.True(callCount >= 2);
    }

    #region Given

    private void GivenWrapperCreateModelReturns()
    {
        _createdWrapperModel = new DbgEngWrapperModel();
        _ = _wrapper.CreateModel().Returns(_createdWrapperModel);
    }

    private void GivenWaitForEventReturns(WaitForEventResult result)
        => _ = _wrapper.WaitForEvent(Arg.Any<DbgEngWrapperModel>()).Returns(result);

    private void GivenWaitForEventSucceedsThenFails()
    {
        int callCount = 0;
        _ = _wrapper.WaitForEvent(Arg.Any<DbgEngWrapperModel>())
            .Returns(_ => ++callCount == 1
                ? WaitForEventResult.EventOccurred
                : WaitForEventResult.Failed);
    }

    private void GivenWaitForEventSucceedsThenTerminates()
    {
        int callCount = 0;
        _ = _wrapper.WaitForEvent(Arg.Any<DbgEngWrapperModel>())
            .Returns(_ =>
            {
                if (++callCount == 1) return WaitForEventResult.EventOccurred;
                _model.Terminated = true;
                return WaitForEventResult.Failed;
            });
    }

    private void GivenWaitForEventSetsTargetExitedThenFails()
    {
        int callCount = 0;
        _ = _wrapper.WaitForEvent(Arg.Any<DbgEngWrapperModel>())
            .Returns(_ =>
            {
                if (++callCount == 1)
                {
                    _model.TargetExited = true;
                    return WaitForEventResult.EventOccurred;
                }
                return WaitForEventResult.Failed;
            });
    }

    private void GivenCreateEngineThrows(Exception ex)
        => _wrapper.When(w => w.CreateEngine(Arg.Any<DbgEngWrapperModel>()))
            .Do(_ => throw ex);

    private void GivenGetLastEventInfoReturns(EngineEventInfo info)
        => _ = _wrapper.GetLastEventInfo(Arg.Any<DbgEngWrapperModel>()).Returns(info);

    private void GivenGetExecutionStatusReturns(EngineExecutionStatus status)
        => _ = _wrapper.GetExecutionStatus(Arg.Any<DbgEngWrapperModel>()).Returns(status);

    private void GivenGetCurrentThreadIdReturns(uint threadId)
        => _ = _wrapper.GetCurrentThreadId(Arg.Any<DbgEngWrapperModel>()).Returns(threadId);

    private void GivenProcessProfilerNotificationsReturns(bool result)
        => _ = _bpResolver.ProcessProfilerNotifications(Arg.Any<NativeDebuggerModel>()).Returns(result);

    private void GivenConfigDone() => _model.ConfigDone = true;

    private void GivenClrLoadedButNotInitialized()
    {
        _model.ClrLoaded = true;
        _model.ManagedInitialized = false;
    }

    private void GivenManagedInitialized() => _model.ManagedInitialized = true;

    private void GivenHitUserBreakpoint()
        => _ = _stepResolution.DetermineStopReason(Arg.Any<NativeDebuggerModel>())
            .Returns(StopReason.Breakpoint);

    private void GivenStepping()
        => _ = _stepResolution.DetermineStopReason(Arg.Any<NativeDebuggerModel>())
            .Returns(StopReason.Step);

    private void GivenPauseRequested()
        => _ = _stepResolution.DetermineStopReason(Arg.Any<NativeDebuggerModel>())
            .Returns(StopReason.Pause);

    private void GivenNoStopReason()
        => _ = _stepResolution.DetermineStopReason(Arg.Any<NativeDebuggerModel>())
            .Returns(StopReason.Continue);

    private void GivenTargetExited() => _model.TargetExited = true;

    private void GivenTerminateSessionThrows()
        => _wrapper.When(w => w.TerminateSession(Arg.Any<DbgEngWrapperModel>()))
            .Do(_ => throw new InvalidOperationException("Session already gone"));

    private void GivenAttachMode(uint pid)
    {
        _model.IsAttach = true;
        _model.AttachPid = pid;

        // Production code waits up to 5 s after SetupProfilerPipeForAttach for
        // the profiler to flip model.ProfilerInitComplete (set when the reader
        // observes READY:attach, signalling that InitializeForAttach finished).
        // Substitute.For<IProfilerPipeService> does nothing by default, so the
        // wait would always exhaust its timeout and the engine thread would
        // miss the test's own EngineReady deadline. Simulate the READY:attach
        // arrival by flipping the flag synchronously here.
        _profilerPipe.When(p => p.SetupProfilerPipeForAttach(Arg.Any<NativeDebuggerModel>(), Arg.Any<int>()))
            .Do(ci =>
            {
                NativeDebuggerModel m = ci.ArgAt<NativeDebuggerModel>(0);
                m.ProfilerConnected = true;
                m.ProfilerInitComplete = true;
            });
    }

    /// <summary>
    /// Attach mode where the profiler IPC succeeds but InitializeForAttach
    /// never finishes (READY:attach never arrives). Exercises the 5 s
    /// timeout path in <c>EngineLifecycleService.AttachToRunningProcess</c>.
    /// </summary>
    private void GivenAttachModeWithoutProfilerReady(uint pid)
    {
        _model.IsAttach = true;
        _model.AttachPid = pid;
        // SetupProfilerPipeForAttach succeeds (returns) but does NOT flip
        // ProfilerInitComplete — the production code's wait loop must time
        // out and surface an EngineInitError instead of hanging or letting
        // dbgeng AttachProcess run against a half-loaded profiler.
    }

    private void GivenLaunchMode(string program, string? cwd = null, string[]? args = null)
    {
        _model.IsAttach = false;
        _model.LaunchProgram = program;
        _model.LaunchCwd = cwd;
        _model.LaunchArgs = args;
    }

    private void GivenSymbolPath(string path) => _model.SymbolPath = path;

    private void GivenDeferredManagedBreakpointExists()
    {
        _model.DeferredManagedBreakpoints.Add(
            new DeferredManagedBreakpoint("test.cs", 10, 0x06000001, 0, 1, "TestAssembly"));
        _model.RebuildDeferredBreakpointIndex();
    }

    #endregion

    #region When

    private void WhenCreatingModel() => _createdModel = _testee.CreateModel();

    private void WhenDisposingCreatedModel() => _createdModel!.Dispose();

    private void WhenBreaking() => _testee.Break(_model);

    private void WhenTerminating() => _testee.Terminate(_model);

    private void WhenDetaching() => _testee.Detach(_model);

    private void WhenStartingEngineThread() => _testee.StartEngineThread(_model);

    private void WhenWaitingForEngineReady()
        => Assert.True(_model.EngineReady.Wait(TimeSpan.FromSeconds(5)), "EngineReady not signaled");

    private void WhenWaitingForEngineThreadToExit()
        => Assert.True(_model.EngineThread!.Join(TimeSpan.FromSeconds(5)), "Engine thread did not exit");

    private void WhenQueuingCommand(Action cmd) => _model.Commands.Add(cmd);

    private void WhenFiringLoadModuleEvent(string? mod, string? img, ulong baseOffset)
        => _createdWrapperModel!.RaiseLoadModule(mod, img, baseOffset);

    private void WhenFiringExitProcessEvent(uint exitCode)
        => _createdWrapperModel!.RaiseExitProcess(exitCode);

    private void WhenFiringCreateProcessEvent(string? name)
        => _createdWrapperModel!.RaiseCreateProcess(name);

    private void WhenFiringClrNotificationEvent()
        => _createdWrapperModel!.RaiseClrNotification();

    private void WhenFiringBreakpointHitEvent(uint bpId)
        => _createdWrapperModel!.RaiseBreakpointHit(bpId);

    private void WhenFiringExceptionBreakpointEvent(ulong address)
        => _createdWrapperModel!.RaiseExceptionBreakpoint(address);

    #endregion

    #region Then

    private void ThenCreatedModelIsNotNull() => Assert.NotNull(_createdModel);

    private void ThenCreatedModelDisposeActionIsSet() => Assert.NotNull(_createdModel!.DisposeAction);

    private void ThenCreatedModelIsTerminated() => Assert.True(_createdModel!.Terminated);

    private void ThenCommandWasQueued() => Assert.True(_model.Commands.Count > 0);

    private void ThenPauseRequestedIsTrue() => Assert.True(_model.PauseRequested);

    private void ThenSetInterruptWasCalled() => _wrapper.Received(1).SetInterrupt(_model.Wrapper);

    private void ThenModelIsTerminated() => Assert.True(_model.Terminated);

    private void ThenTerminateSessionWasCalled() => _wrapper.Received(1).TerminateSession(Arg.Any<DbgEngWrapperModel>());

    private void ThenDetachSessionWasCalled() => _wrapper.Received(1).DetachSession(_model.Wrapper);

    private void ThenEngineThreadExits()
        => Assert.True(_model.EngineThread!.Join(TimeSpan.FromSeconds(5)));

    private void ThenEngineInitErrorIs<T>() where T : Exception
        => Assert.IsType<T>(_model.EngineInitError);

    private void ThenEngineReadyIsSet() => Assert.True(_model.EngineReady.IsSet);

    private void ThenTerminatedEventWasSent()
        => _server.Received().SendEvent(_transport, "terminated", Arg.Any<TerminatedEventBody>());

    private void ThenOutputEventWasSentWithCategory(string category)
        => _server.Received().SendEvent(_transport, "output",
            Arg.Is<OutputEventBody>(b => b.Category == category));

    private void ThenStoppedEventWasSentWithReason(string reason)
        => _server.Received().SendEvent(_transport, "stopped",
            Arg.Is<StoppedEventBody>(b => b.Reason == reason));

    private void ThenSetExecutionStatusWasCalledWith(EngineExecutionStatus status)
        => _wrapper.Received().SetExecutionStatus(Arg.Any<DbgEngWrapperModel>(), status);

    private void ThenTryInitializeManagedWasCalled()
        => _managedDebugger.Received(1).TryInitializeManaged(Arg.Any<NativeDebuggerModel>());

    private void ThenTryInitializeManagedWasNotCalled()
        => _managedDebugger.DidNotReceive().TryInitializeManaged(Arg.Any<NativeDebuggerModel>());

    private void ThenProcessPendingManagedBreakpointsWasCalled()
        => _bpResolver.Received().ProcessPendingManagedBreakpoints(Arg.Any<NativeDebuggerModel>());

    private void ThenAttachProcessWasCalled(uint pid)
        => _wrapper.Received(1).AttachProcess(Arg.Any<DbgEngWrapperModel>(), pid);

    private void ThenCreateProcessWasNotCalled()
        => _wrapper.DidNotReceive().CreateProcess(Arg.Any<DbgEngWrapperModel>(), Arg.Any<string>());

    private void ThenCreateProcessWasCalled(string cmdLine)
        => _wrapper.Received(1).CreateProcess(Arg.Any<DbgEngWrapperModel>(), cmdLine);

    private void ThenInitializeSymbolsWasCalledWithSourcePath(string sourcePath)
        => _wrapper.Received().InitializeSymbols(Arg.Any<DbgEngWrapperModel>(), Arg.Any<string?>(), sourcePath);

    private void ThenInitializeSymbolsWasCalledWithSymbolPath(string symbolPath)
        => _wrapper.Received().InitializeSymbols(Arg.Any<DbgEngWrapperModel>(), symbolPath, Arg.Any<string?>());

    private void ThenSetupProfilerPipeWasCalled()
        => _profilerPipe.Received(1).SetupProfilerPipe(Arg.Any<NativeDebuggerModel>());

    private void ThenStartProfilerReaderWasCalled()
        => _profilerPipe.Received(1).StartProfilerReader(Arg.Any<NativeDebuggerModel>());

    private void ThenModelWrapperIsTheCreatedOne()
        => Assert.Same(_createdWrapperModel, _model.Wrapper);

    private void ThenClrLoadedIsTrue() => Assert.True(_model.ClrLoaded);

    private void ThenClrLoadedIsFalse() => Assert.False(_model.ClrLoaded);

    private void ThenCoreClrPathIs(string expected) => Assert.Equal(expected, _model.CoreClrPath);

    private void ThenCoreClrBaseAddressIs(ulong expected) => Assert.Equal(expected, _model.CoreClrBaseAddress);

    private void ThenTargetExitedIsTrue() => Assert.True(_model.TargetExited);

    private void ThenClrNotificationShouldBreakIsTrue()
        => Assert.True(_createdWrapperModel!.ClrNotificationShouldBreak);

    private void ThenClrNotificationShouldBreakIsFalse()
        => Assert.False(_createdWrapperModel!.ClrNotificationShouldBreak);

    private void ThenTryBindManagedBreakpointsOnModuleLoadWasCalled()
        => _bpResolver.Received(1).TryBindManagedBreakpointsOnModuleLoad(Arg.Any<NativeDebuggerModel>());

    private void ThenTryBindManagedBreakpointsOnModuleLoadWasNotCalled()
        => _bpResolver.DidNotReceive().TryBindManagedBreakpointsOnModuleLoad(Arg.Any<NativeDebuggerModel>());

    private void ThenHandleBreakpointHitWasCalledWith(uint bpId)
        => _breakpointService.Received(1).HandleBreakpointHit(Arg.Any<NativeDebuggerModel>(), bpId);

    private void ThenHandleExceptionBreakpointWasCalledWith(ulong address)
        => _breakpointService.Received(1).HandleExceptionBreakpoint(Arg.Any<NativeDebuggerModel>(), address);

    #endregion

    #region Misc

    private readonly IDapServer _server = Substitute.For<IDapServer>();
    private readonly ILoggingService _log = Substitute.For<ILoggingService>();
    private readonly IManagedDebugger _managedDebugger = Substitute.For<IManagedDebugger>();
    private readonly IManagedBreakpointResolver _bpResolver = Substitute.For<IManagedBreakpointResolver>();
    private readonly IDbgEngWrapper _wrapper = Substitute.For<IDbgEngWrapper>();
    private readonly IProfilerPipeService _profilerPipe = Substitute.For<IProfilerPipeService>();
    private readonly IBreakpointService _breakpointService = Substitute.For<IBreakpointService>();
    private readonly ISteppingService _stepping = Substitute.For<ISteppingService>();
    private readonly IStepResolutionService _stepResolution = Substitute.For<IStepResolutionService>();
    private readonly DapServerModel _transport;
    private readonly LogStore _logStore;
    private readonly NativeDebuggerModel _model;
    private readonly EngineLifecycleService _testee;

    private NativeDebuggerModel? _createdModel;
    private DbgEngWrapperModel? _createdWrapperModel;

    public EngineLifecycleServiceTests()
    {
        _transport = new DapServerModel(Stream.Null, Stream.Null);
        _logStore = new LogStore(Path.Combine(Path.GetTempPath(), "test.log"));
        _profilerPipe = Substitute.For<IProfilerPipeService>();
        _breakpointService = Substitute.For<IBreakpointService>();
        _testee = new EngineLifecycleService(
            _server, _transport, _log, _logStore,
            _managedDebugger, _bpResolver, _profilerPipe,
            _breakpointService, _stepping, _stepResolution, _wrapper);
        _model = new NativeDebuggerModel
        {
            Wrapper = new DbgEngWrapperModel(),
            CorWrapper = new CorDebugWrapperModel(),
        };
    }

    public void Dispose()
    {
        _model.Terminated = true;
        try { _model.Commands.CompleteAdding(); } catch { }
        _ = _model.EngineThread?.Join(TimeSpan.FromSeconds(2));
        _model.Commands.Dispose();
        _model.Stopped.Dispose();
        _model.EngineReady.Dispose();
    }

    #endregion
}
