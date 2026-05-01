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
            _ = (model.ProfilerReaderThread?.Join(1000));
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
            Environment.SetEnvironmentVariable("MIXDBG_REHOOK_EVENT", null);
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
        _log.LogVerbose(_logStore, "WaitForEvent...");
        model.InWaitForEvent = true;
        WaitForEventResult waitResult = _wrapper.WaitForEvent(dbgEngWrapperModel);
        model.InWaitForEvent = false;
        _log.LogVerbose(_logStore, $"WaitForEvent returned {waitResult}");
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
        if (!model.ConfigDone)
        {
            ProcessCommandsUntilResume(model);
            return true;
        }
        // Initialize managed debugging when CLR is first detected.
        if (model.ClrLoaded && !model.ManagedInitialized)
            _managedDebugger.TryInitializeManaged(model);

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

    private void AttachOrCreateProcess(NativeDebuggerModel model)
    {
        DbgEngWrapperModel dbgEngWrapperModel = model.Wrapper;

        if (model.IsAttach)
        {
            _log.LogInfo(_logStore, $"Attach: pid={model.AttachPid}");
            _wrapper.AttachProcess(dbgEngWrapperModel, model.AttachPid);
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
