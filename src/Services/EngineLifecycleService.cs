using MixDbg.Models;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless engine lifecycle service. Owns the engine thread, the event loop,
/// and thread-safe control methods. All mutable state lives in
/// <see cref="NativeDebuggerModel"/>.
/// </summary>
internal sealed class EngineLifecycleService(
    IDapServer _server,
    DapServerModel _transport,
    ILoggingService _log,
    LogStore _logStore,
    IManagedDebugger _managedDebugger,
    IManagedBreakpointResolver _bpResolver,
    IProfilerPipeService _profilerPipe,
    IBreakpointService _breakpointService,
    ISteppingService _stepping,
    IStepResolutionService _stepResolution,
    IDbgEngWrapper _wrapper) : IEngineLifecycleService
{
    public NativeDebuggerModel CreateModel()
    {
        NativeDebuggerModel model = new();
        model.DisposeAction = () =>
        {
            model.Terminated = true;
            model.Commands.CompleteAdding();
            _ = (model.EngineThread?.Join(3000));
            _ = (model.ProfilerAckEvent?.Set()); // Unblock profiler if waiting.
            model.ProfilerAckEvent?.Dispose();
            model.ProfilerPipeReader?.Dispose();
            model.ProfilerPipe?.Dispose();
            model.ProfilerCmdPipeWriter?.Dispose();
            model.ProfilerCmdPipe?.Dispose();
            _ = (model.ProfilerReaderThread?.Join(1000));
            _ = (model.ProfilerCmdConnectThread?.Join(1000));
            model.Commands.Dispose();
            model.Stopped.Dispose();
            model.EngineReady.Dispose();

            // Clear profiler env vars so they don't leak to other processes.
            Environment.SetEnvironmentVariable("CORECLR_ENABLE_PROFILING", null);
            Environment.SetEnvironmentVariable("CORECLR_PROFILER", null);
            Environment.SetEnvironmentVariable("CORECLR_PROFILER_PATH", null);
            Environment.SetEnvironmentVariable("MIXDBG_PIPE_NAME", null);
            Environment.SetEnvironmentVariable("MIXDBG_ACK_EVENT", null);
            Environment.SetEnvironmentVariable("MIXDBG_WATCH_TOKENS", null);
            Environment.SetEnvironmentVariable("MIXDBG_CMD_PIPE", null);
        };
        return model;
    }

    /// <summary>Requests the target to break. Safe from any thread.</summary>
    public void Break(NativeDebuggerModel model)
    {
        model.PauseRequested = true;
        if (model.InWaitForEvent)
            _wrapper.SetInterrupt(model.Wrapper);
        else
            model.Wrapper.InterruptRequested = true;
    }

    /// <summary>Terminates the debugged process and wakes the engine thread to exit.</summary>
    public void Terminate(NativeDebuggerModel model)
    {
        model.Terminated = true;
        if (!model.TargetExited)
        {
            _wrapper.TerminateSession(model.Wrapper);
        }
        else
        {
            try { _wrapper.TerminateSession(model.Wrapper); } catch { }
        }
        model.Commands.Add(WakeEngineThread);
    }

    /// <summary>Detaches from the debugged process and wakes the engine thread to exit.</summary>
    public void Detach(NativeDebuggerModel model)
    {
        model.Terminated = true;
        _wrapper.DetachSession(model.Wrapper);
        model.Commands.Add(WakeEngineThread);
    }

    public void StartEngineThread(NativeDebuggerModel model)
    {
        model.EngineThread = new Thread(() =>
        {
            try{
                // All dbgeng COM calls must happen on this thread.
                CreateEngine(model);
                AttachOrCreateProcess(model);
                model.EngineReady.Set(); // Signal the main thread that init is done.

                // Soft-failure path: AttachToRunningProcess can set
                // EngineInitError without throwing (timeouts, drain failures,
                // missing coreclr). Surface it the same way as a thrown
                // exception and skip the loop — running EngineLoopStep against
                // a half-initialized engine would call WaitForEvent on a
                // dbgeng instance whose attach didn't complete.
                if (model.EngineInitError != null)
                {
                    _server.SendEvent(_transport, "output", new OutputEventBody
                    {
                        Category = "stderr",
                        Output = $"[mixdbg] Engine error: {model.EngineInitError.Message}\n",
                    });
                    _server.SendEvent(_transport, "terminated", new TerminatedEventBody());
                    return;
                }

                _log.LogInfo(_logStore, "EngineLoop started — initializing dbgeng on engine thread");

                while (!model.Terminated)
                {
                    if(!EngineLoopStep(model, model.Wrapper)){
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(_logStore, $"EngineLoop EXCEPTION: {ex}");
                // If init failed, unblock the main thread.
                model.EngineInitError = ex;
                model.EngineReady.Set();
                _server.SendEvent(_transport, "output", new OutputEventBody
                {
                    Category = "stderr",
                    Output = $"[mixdbg] Engine error: {ex.Message}\n",
                });
                _server.SendEvent(_transport, "terminated", new TerminatedEventBody());
            }
        })
        {
            Name = "dbgeng-engine",
            IsBackground = true,
        };
        model.EngineThread.Start();
    }

    /// <summary>No-op command that unblocks the engine thread's <c>Commands.Take()</c>.</summary>
    private static void WakeEngineThread() { }

    private bool EngineLoopStep(NativeDebuggerModel model, DbgEngWrapperModel dbgEngWrapperModel)
    {
        // Check for deferred interrupt requests from non-engine threads.
        // These are set when InWaitForEvent was false during the request.
        if (dbgEngWrapperModel.InterruptRequested)
        {
            dbgEngWrapperModel.InterruptRequested = false;
            _wrapper.SetInterrupt(dbgEngWrapperModel);
        }

        WaitForEventResult waitResult;
        if (model.SkipNextWaitForEvent)
        {
            // AttachOrCreateProcess already consumed the initial event; reuse
            // the stopped state instead of blocking on a fresh WaitForEvent.
            model.SkipNextWaitForEvent = false;
            waitResult = WaitForEventResult.EventOccurred;
            _log.LogVerbose(_logStore, "WaitForEvent skipped (attach drained initial event)");
        }
        else
        {
            _log.LogVerbose(_logStore, "WaitForEvent...");
            model.InWaitForEvent = true;
            waitResult = _wrapper.WaitForEvent(dbgEngWrapperModel);
            model.InWaitForEvent = false;
            _log.LogVerbose(_logStore, $"WaitForEvent returned {waitResult}");
        }
        if (waitResult == WaitForEventResult.Failed)
        {
            _log.LogVerbose(_logStore, $"WaitForEvent failed, terminated={model.Terminated} exited={model.TargetExited}");
            return false;
        }
        model.Stopped.Set(); // Target is now stopped.

        LogDebuggerEvent(model, dbgEngWrapperModel);
        if (model.TargetExited)
        {
            _server.SendEvent(_transport, "terminated", new TerminatedEventBody());
            return false;
        }
        // Initialize managed debugging when CLR is first detected. Must run
        // BEFORE the !ConfigDone gate so the attach path (where the CLR is
        // already loaded and no CLR-notification exception will fire) gets
        // managed init done before configurationDone resumes the target —
        // otherwise pending managed BPs sit unresolved while the user code
        // runs through the breakpoint location. In launch mode this just
        // means init can happen during the pre-configDone event replay too,
        // which is safe (the process is still stopped at the dbgeng break).
        if (model.ClrLoaded && !model.ManagedInitialized)
            _managedDebugger.TryInitializeManaged(model);

        if (!model.ConfigDone)
        {
            // Attach mode: still drain profiler notifications even before
            // configurationDone arrives. Otherwise a watched JIT in this
            // window (after EngineReady.Set, before the DAP client sends
            // configurationDone) stalls 500 ms on m_hAckEvent — and on a
            // busy target the timeout fires, the rejitted body executes,
            // and the first hit is silently lost. The call modifies model
            // state and may install dbgeng HW BPs (FoldJitIntoPlans →
            // InstallEagerHardwareBp) but does NOT call SetExecutionStatus,
            // so the engine stays at the attach breakin.
            if (model.IsAttach)
                _ = _bpResolver.ProcessProfilerNotifications(model);
            ProcessCommandsUntilResume(model);
            return true;
        }

        // Drain profiler notifications first (ENTER installs HW BPs at exact IL
        // offsets, LEAVE removes them). Must run before DAC fallback so the proper
        // JIT→plan→ENTER path takes precedence over entry-point-only resolution.
        // Returns true only when this was a bookkeeping-only stop — auto-resume.
        if (_bpResolver.ProcessProfilerNotifications(model))
        {
            model.Stopped.Reset();
            _wrapper.SetExecutionStatus(dbgEngWrapperModel, EngineExecutionStatus.Go);
            return true;
        }

        // DAC fallback for deferred BPs not yet resolved by profiler notifications.
        _bpResolver.ProcessPendingManagedBreakpoints(model);

        StopReason reason = _stepResolution.DetermineStopReason(model);
        if (reason != StopReason.Continue)
        {
            // After a native step, if the instruction pointer has no useful source
            // (e.g. closing brace, same line, sourceless JIT thunk), auto-continue.
            if (reason == StopReason.Step && model.ActiveManagedStep == null
                && model.StepOriginLocation != null)
            {
                StepAutoAction action = _stepResolution.CheckStepLanding(model);
                if (action == StepAutoAction.ReStep)
                {
                    model.StepReStepCount++;
                    if (model.StepReStepCount > 100)
                    {
                        // Safety valve: stop here rather than looping forever through
                        // sourceless code. Fall through to send a stopped event.
                        _log.LogWarning(_logStore, "Re-step limit exceeded — stopping");
                    }
                    else
                    {
                        _log.LogInfo(_logStore, "Step landing not useful — re-stepping");
                        model.Stepping = true;
                        model.Stopped.Reset();
                        _wrapper.SetExecutionStatus(dbgEngWrapperModel, model.StepOriginKind);
                        return true;
                    }
                }
                if (action == StepAutoAction.StepOut)
                {
                    _log.LogInfo(_logStore, "Step on sourceless line — auto-stepping-out");
                    model.CachedStackTraceResult = null;
                    model.CachedThreadsResult = null;
                    _stepping.ExecuteStepOutOnEngine(model);
                    return true;
                }
            }

            model.StepOriginLocation = null;
            model.StepOriginStackPointer = 0;
            model.StepOriginKind = default;
            model.StepReStepCount = 0;
            model.StepIntoEnteredCallee = false;
            SendStopDapResponseAndProcessCommands(model, dbgEngWrapperModel, reason);
            return true;
        }

        DrainPendingCommands(model);
        // If Stepping is still set, a BP was suppressed at depth — re-step instead of Go.
        EngineExecutionStatus resumeStatus = model.Stepping
            ? (model.StepOriginKind is EngineExecutionStatus.StepOver or EngineExecutionStatus.StepInto
                ? model.StepOriginKind
                : EngineExecutionStatus.StepOver)
            : EngineExecutionStatus.Go;
        _log.LogVerbose(_logStore, $"System stop — auto-continuing with {resumeStatus}");
        model.Stopped.Reset();
        _wrapper.SetExecutionStatus(dbgEngWrapperModel, resumeStatus);
        return true;
    }

    private void LogDebuggerEvent(NativeDebuggerModel model, DbgEngWrapperModel dbgEngWrapperModel)
    {
        // Get last event info for logging
        EngineEventInfo evt = _wrapper.GetLastEventInfo(dbgEngWrapperModel);
        _log.LogVerbose(_logStore, $"Event: type=0x{evt.Type:X} pid={evt.ProcessId} tid={evt.ThreadId} desc=\"{evt.Description}\"");
        _log.LogVerbose(_logStore, $"State: configDone={model.ConfigDone} hitUserBp={model.HitUserBreakpoint} stepping={model.Stepping} pause={model.PauseRequested}");
    }

    private void SendStopDapResponseAndProcessCommands(NativeDebuggerModel model, DbgEngWrapperModel dbgEngWrapperModel, StopReason reason)
    {
        uint threadId = _wrapper.GetCurrentThreadId(dbgEngWrapperModel);
        string dapReason = reason.ToDapString();
        _log.LogInfo(_logStore, $"User stop: reason={dapReason} threadId={threadId}");
        _server.SendEvent(_transport, "stopped", new StoppedEventBody
        {
            Reason = dapReason,
            ThreadId = (int)threadId,
            AllThreadsStopped = true,
        });
        ProcessCommandsUntilResume(model);
    }

    /// <summary>
    /// Attach path. Runs on the engine thread before <c>EngineReady</c> is
    /// signaled — any failure is reported by setting
    /// <see cref="NativeDebuggerModel.EngineInitError"/> and signaling
    /// <see cref="NativeDebuggerModel.EngineReady"/>, which makes
    /// <see cref="Handlers.Lifecycle.AttachRequestHandlerService"/>
    /// surface the error to the DAP client instead of hanging.
    /// </summary>
    private void AttachToRunningProcess(NativeDebuggerModel model, DbgEngWrapperModel dbgEngWrapperModel)
    {
        _log.LogInfo(_logStore, $"Attach: pid={model.AttachPid}");

        // Send AttachProfiler IPC BEFORE dbgeng attach. dbgeng suspends every
        // thread in the target; the diagnostic-IPC server (which loads the
        // profiler DLL and sends back the HRESULT response) is itself a
        // runtime thread, so suspending first deadlocks the IPC round-trip.
        bool profilerSetUp = false;
        try
        {
            _profilerPipe.SetupProfilerPipeForAttach(model, (int)model.AttachPid);
            // Start the reader BEFORE dbgeng attach so JIT notifications
            // emitted between profiler-load and dbgeng-attach are captured.
            _profilerPipe.StartProfilerReader(model);
            profilerSetUp = true;
        }
        catch (Exception ex)
        {
            // Native debugging can still proceed without the profiler — the
            // user will lose managed BP support but can still debug native
            // code. Surface the failure as a console output and continue.
            _log.LogError(_logStore, $"Attach: profiler attach failed: {ex.Message}");
            _server.SendEvent(_transport, "output", new OutputEventBody
            {
                Category = "stderr",
                Output = $"[mixdbg] Profiler attach failed: {ex.Message}\n",
            });
        }

        if (profilerSetUp)
        {
            // The diagnostic-IPC AttachProfiler response means "attach command
            // accepted" — the actual InitializeForAttach call happens on a
            // runtime thread *after* the IPC returns. If we let dbgeng attach
            // immediately, that runtime thread gets suspended before
            // InitializeForAttach runs and the profiler is left half-loaded.
            //
            // ProfilerInitComplete flips only after InitializeForAttach has
            // run to completion and emitted READY:attach (event mask set,
            // ack/cmd pipes opened, watch list parsed). Gating on this — not
            // on ProfilerConnected, which flips on the very first CreateFileW
            // — guarantees dbgeng won't suspend the runtime profiler-init
            // thread while it's still mid-setup.
            long profilerReadyDeadline = Environment.TickCount64 + 5000;
            while (!model.ProfilerInitComplete && Environment.TickCount64 < profilerReadyDeadline)
                Thread.Sleep(50);

            if (!model.ProfilerInitComplete)
            {
                // Attaching with dbgeng now would deadlock the runtime's
                // profiler-init thread. Refuse the attach so the DAP client
                // gets a clear error instead of a hung session.
                model.EngineInitError = new InvalidOperationException(
                    "Profiler did not finish InitializeForAttach within 5 s after AttachProfiler IPC succeeded. " +
                    "Refusing dbgeng attach to avoid suspending the runtime profiler-init thread mid-load.");
                _log.LogError(_logStore, $"Attach: {model.EngineInitError.Message}");
                return;
            }
            _log.LogInfo(_logStore, "Attach: profiler ready; proceeding with dbgeng attach");
        }

        _wrapper.AttachProcess(dbgEngWrapperModel, model.AttachPid);

        // Drain the dbgeng attach-replay (CreateProcess + LoadModule callbacks
        // for every already-loaded module + initial breakin) BEFORE signaling
        // EngineReady. The launch path gets managed-debug initialization "for
        // free" via the CLR notification exception that fires during early
        // CLR startup; attach has no such notification because the CLR is
        // already initialized. Without this drain, ClrLoaded is set during
        // the dbgeng replay but the loop already Continues (config-done) past
        // the initial breakin before TryInitializeManaged ever gets a chance,
        // leaving pending managed BPs unresolved until the next module-load
        // event — which can be many seconds later for an idle process.
        _log.LogInfo(_logStore, "Attach: draining initial event so managed init can run synchronously");
        model.InWaitForEvent = true;
        WaitForEventResult initialResult = _wrapper.WaitForEvent(dbgEngWrapperModel);
        model.InWaitForEvent = false;
        if (initialResult == WaitForEventResult.Failed || model.TargetExited || model.Terminated)
        {
            model.EngineInitError = new InvalidOperationException(
                $"Attach failed during initial WaitForEvent (result={initialResult}, " +
                $"targetExited={model.TargetExited}, terminated={model.Terminated})");
            _log.LogError(_logStore, $"Attach: {model.EngineInitError.Message}");
            return;
        }

        // The first WaitForEvent only delivers the CreateProcess event;
        // module-load callbacks (and therefore ClrLoaded) only fire after
        // we resume the engine and let it process the OS's queued debug
        // events. Cycle Go → Interrupt → WaitForEvent until coreclr is
        // observed (or we give up). Each iteration lets dbgeng drain the
        // queued LoadModule callbacks before our SetInterrupt forces a
        // break.
        //
        // The cap is generous because on processes with many loaded
        // modules (large frameworks, plugin hosts) coreclr may take more
        // than the first few iterations to surface. Each iteration costs
        // ~50 ms target-time + the WaitForEvent round-trip; 50 iterations
        // is ~3 s worst-case, which is the right ceiling for "is this a
        // .NET process?" vs "is the host just slow?".
        const int DrainIterationCap = 50;
        for (int i = 0; i < DrainIterationCap && !model.ClrLoaded; i++)
        {
            try { _wrapper.SetExecutionStatus(dbgEngWrapperModel, EngineExecutionStatus.Go); }
            catch (Exception ex)
            {
                model.EngineInitError = new InvalidOperationException(
                    $"Attach drain failed at SetExecutionStatus(Go): {ex.Message}", ex);
                return;
            }
            Thread.Sleep(50);
            try { _wrapper.SetInterrupt(dbgEngWrapperModel); }
            catch (Exception ex)
            {
                model.EngineInitError = new InvalidOperationException(
                    $"Attach drain failed at SetInterrupt: {ex.Message}", ex);
                return;
            }
            model.InWaitForEvent = true;
            WaitForEventResult drainResult = _wrapper.WaitForEvent(dbgEngWrapperModel);
            model.InWaitForEvent = false;
            if (drainResult == WaitForEventResult.Failed || model.TargetExited || model.Terminated)
            {
                model.EngineInitError = new InvalidOperationException(
                    $"Attach drain failed at iteration {i} " +
                    $"(result={drainResult}, targetExited={model.TargetExited}, terminated={model.Terminated})");
                _log.LogError(_logStore, $"Attach: {model.EngineInitError.Message}");
                return;
            }

            // Drain any profiler notifications that arrived during this Go
            // window. Without this, JIT notifications for watched methods
            // sit in the queue while the C++ profiler thread blocks on
            // m_hAckEvent (500 ms timeout in attach mode). When the timeout
            // expires the method body executes — without a HW BP — and the
            // user's first hit is silently lost. ProcessProfilerNotifications
            // parses queued JIT/ENTER/LEAVE entries, signals the ACK event
            // so the runtime unblocks, and (for any deferred BP that already
            // exists) installs HW BPs eagerly via FoldJitIntoPlans →
            // InstallEagerHardwareBp. It mutates model state and may install
            // dbgeng HW BPs, but does NOT call SetExecutionStatus, so the
            // drain's Go/Interrupt cadence is preserved.
            _ = _bpResolver.ProcessProfilerNotifications(model);
        }

        _log.LogInfo(_logStore, $"Attach: drain finished; ClrLoaded={model.ClrLoaded}");

        // If profiler attach succeeded the target is definitely a .NET process,
        // so coreclr MUST be observable in the drain. Silently proceeding with
        // ClrLoaded=false leaves the user with no managed debugging and no
        // explanation — bail with a clear error instead. (When profiler attach
        // failed earlier we're in pure-native mode and ClrLoaded=false is OK.)
        if (profilerSetUp && !model.ClrLoaded)
        {
            model.EngineInitError = new InvalidOperationException(
                "Attach drain completed without observing coreclr load events. " +
                "The profiler attached but managed debugging cannot start — the target may be in an unexpected state.");
            _log.LogError(_logStore, $"Attach: {model.EngineInitError.Message}");
            return;
        }

        if (model.ClrLoaded && !model.ManagedInitialized)
            _managedDebugger.TryInitializeManaged(model);

        // Tell the engine loop to skip its first WaitForEvent — we already
        // consumed the attach breakin here. Without this the loop would
        // block forever in WaitForEvent waiting for the next debug event,
        // and queued DAP commands (setBreakpoints / configurationDone)
        // would never run.
        model.SkipNextWaitForEvent = true;
        model.Stopped.Set();
    }

    private void AttachOrCreateProcess(NativeDebuggerModel model)
    {
        DbgEngWrapperModel dbgEngWrapperModel = model.Wrapper;

        if (model.IsAttach)
        {
            AttachToRunningProcess(model, dbgEngWrapperModel);
            return;
        }

        if (model.LaunchCwd != null)
            _wrapper.InitializeSymbols(dbgEngWrapperModel, null, model.LaunchCwd);

        // Disable tiered compilation and ReadyToRun so JIT produces stable
        // native addresses for hardware breakpoints on managed methods.
        Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", "0");
        Environment.SetEnvironmentVariable("DOTNET_ReadyToRun", "0");

        // Set up CLR profiler for real-time JIT notifications.
        _profilerPipe.SetupProfilerPipe(model);

        string cmdLine = model.LaunchProgram!;
        if (model.LaunchArgs is { Length: > 0 })
            cmdLine += " " + string.Join(" ", model.LaunchArgs);

        _log.LogInfo(_logStore, $"Launch: CreateProcess({cmdLine})");
        _wrapper.CreateProcess(dbgEngWrapperModel, cmdLine);
        _log.LogInfo(_logStore, "Launch: CreateProcess succeeded");

        // Start reading profiler notifications in the background.
        _profilerPipe.StartProfilerReader(model);
    }

    /// <summary>
    /// Non-blocking drain of pending commands. Called during ENTER processing and
    /// system stops so that mid-session DAP requests (like setBreakpoints) don't
    /// stall behind auto-continued stops.
    /// </summary>
    private void DrainPendingCommands(NativeDebuggerModel model)
    {
        while (model.Commands.TryTake(out Action? cmd))
        {
            _log.LogVerbose(_logStore, "DrainPendingCommands: executing queued command");
            cmd();
            EngineExecutionStatus status = _wrapper.GetExecutionStatus(model.Wrapper);
            if (status != EngineExecutionStatus.Break
                && status != EngineExecutionStatus.NoDebuggee)
            {
                _log.LogVerbose(_logStore, "DrainPendingCommands: command caused resume — stopping drain");
                return;
            }
        }
    }

    private void ProcessCommandsUntilResume(NativeDebuggerModel model)
    {
        _log.LogVerbose(_logStore, "ProcessCommandsUntilResume: waiting for commands");
        while (!model.Terminated)
        {
            Action cmd;
            try
            {
                cmd = model.Commands.Take();
            }
            catch (InvalidOperationException)
            {
                _log.LogVerbose(_logStore, "ProcessCommandsUntilResume: collection completed");
                break;
            }

            _log.LogVerbose(_logStore, "ProcessCommandsUntilResume: executing command");
            cmd();

            // Step completed inside the command (managed step-into loop, or native "gu").
            // Send stopped event and stay in the loop — engine is already stopped.
            if (model.ManagedStepIntoCompleted)
            {
                model.ManagedStepIntoCompleted = false;
                StopReason reason;
                if (model.HitUserBreakpoint)
                {
                    model.HitUserBreakpoint = false;
                    reason = StopReason.Breakpoint;
                }
                else
                {
                    model.Stepping = false;
                    reason = StopReason.Step;
                }
                uint threadId = _wrapper.GetCurrentThreadId(model.Wrapper);
                string dapReason = reason.ToDapString();
                _log.LogInfo(_logStore, $"ProcessCommandsUntilResume: step-into done, reason={dapReason} threadId={threadId}");
                _server.SendEvent(_transport, "stopped", new StoppedEventBody
                {
                    Reason = dapReason,
                    ThreadId = (int)threadId,
                    AllThreadsStopped = true,
                });
                continue;
            }

            EngineExecutionStatus status = _wrapper.GetExecutionStatus(model.Wrapper);

            // The "gu" command blocks until step-out completes, leaving the engine in
            // Break state. If Stepping is set and status is Break, the step-out finished
            // inside the command — send stopped event and stay in the loop.
            if (model.Stepping && status == EngineExecutionStatus.Break)
            {
                model.Stepping = false;
                uint threadId = _wrapper.GetCurrentThreadId(model.Wrapper);
                _log.LogInfo(_logStore, $"ProcessCommandsUntilResume: step-out (gu) done, threadId={threadId}");
                _server.SendEvent(_transport, "stopped", new StoppedEventBody
                {
                    Reason = StopReason.Step.ToDapString(),
                    ThreadId = (int)threadId,
                    AllThreadsStopped = true,
                });
                continue;
            }
            _log.LogVerbose(_logStore, $"ProcessCommandsUntilResume: execStatus={status}");
            if (status != EngineExecutionStatus.Break
                && status != EngineExecutionStatus.NoDebuggee)
            {
                _log.LogVerbose(_logStore, "ProcessCommandsUntilResume: resuming");
                model.Stopped.Reset();
                break;
            }
        }
    }

    private void CreateEngine(NativeDebuggerModel model)
    {
        DbgEngWrapperModel wrapperModel = _wrapper.CreateModel();
        model.Wrapper = wrapperModel;
        model.InterruptAction = () => _wrapper.SetInterrupt(wrapperModel);

        _wrapper.CreateEngine(wrapperModel);

        wrapperModel.OnBreakpointHit += bpId => _breakpointService.HandleBreakpointHit(model, bpId);
        wrapperModel.OnExitProcess += code => OnExitProcess(model, code);
        wrapperModel.OnCreateProcess += OnCreateProcess;
        wrapperModel.OnLoadModule += (mod, img, baseOffset) => OnLoadModule(model, mod, img, baseOffset);
        wrapperModel.OnClrNotification += () => OnClrNotification(model, wrapperModel);
        wrapperModel.OnExceptionBreakpoint += addr => _breakpointService.HandleExceptionBreakpoint(model, addr);

        _wrapper.InitializeSymbols(wrapperModel, model.SymbolPath, null);
    }

    private static void OnClrNotification(NativeDebuggerModel model, DbgEngWrapperModel wrapperModel)
    {
        // When deferred managed breakpoints exist after configDone,
        // break so the engine loop can recreate the DAC and check for JIT.
        if (model.ConfigDone && model.DeferredManagedBreakpoints.Count > 0)
            wrapperModel.ClrNotificationShouldBreak = true;
    }

    private void OnLoadModule(NativeDebuggerModel model, string? mod, string? img, ulong baseOffset)
    {
        _log.LogInfo(_logStore, $"Module loaded: {mod ?? "(null)"} img={img ?? "(null)"} base=0x{baseOffset:X}");

        if (!model.ClrLoaded && mod != null &&
                mod.Equals("coreclr", StringComparison.OrdinalIgnoreCase))
        {
            model.ClrLoaded = true;
            model.CoreClrPath = img;
            model.CoreClrBaseAddress = baseOffset;
            _log.LogInfo(_logStore, $"CLR detected: coreclr at {img} base=0x{baseOffset:X}");
        }

        // After managed init, notify on managed assembly loads so pending BPs can bind.
        if (model.ManagedInitialized && img != null &&
                (img.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                 img.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            _bpResolver.TryBindManagedBreakpointsOnModuleLoad(model);
        }
    }

    private void OnCreateProcess(string? name)
        => _server.SendEvent(_transport, "output", new OutputEventBody
        {
            Category = "console",
            Output = $"[mixdbg] Process created: {name}\n",
        });

    private void OnExitProcess(NativeDebuggerModel model, uint exitCode)
    {
        model.TargetExited = true;
        _server.SendEvent(_transport, "output", new OutputEventBody
        {
            Category = "console",
            Output = $"[mixdbg] Process exited with code {exitCode}\n",
        });
    }
}
