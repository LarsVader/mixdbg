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
    IEngineQueryService _engineQuery,
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
            _ = (model.ProfilerRehookEvent?.Set()); // Unblock rehook watcher.
            model.ProfilerAckEvent?.Dispose();
            model.ProfilerRehookEvent?.Dispose();
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
            Environment.SetEnvironmentVariable("MIXDBG_REHOOK_EVENT", null);
            Environment.SetEnvironmentVariable("MIXDBG_WATCH_TOKENS", null);
            Environment.SetEnvironmentVariable("MIXDBG_WATCH_ASSEMBLIES", null);
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

        // Process JIT notifications and resolve deferred managed breakpoints.
        _bpResolver.ProcessPendingManagedBreakpoints(model);

        if (_bpResolver.HandleEnterBreakpoint(model))
        {
            model.Stopped.Reset();
            _wrapper.SetExecutionStatus(dbgEngWrapperModel, EngineExecutionStatus.Go);
            return true;
        }
        if (DetermineStopReason(model) is string reason)
        {
            // After a native step, if the IP has no useful source (e.g. closing brace,
            // same line, sourceless JIT thunk), auto-continue instead of stopping.
            if (reason == "step" && model.ActiveManagedStep == null
                && model.StepOriginLocation != null)
            {
                StepAutoAction action = CheckStepLanding(model);
                if (action == StepAutoAction.ReStep)
                {
                    _log.LogInfo(_logStore, "Step on same line — re-stepping");
                    model.Stepping = true;
                    model.Stopped.Reset();
                    _wrapper.SetExecutionStatus(dbgEngWrapperModel, EngineExecutionStatus.StepOver);
                    return true;
                }
                if (action == StepAutoAction.StepOut)
                {
                    _log.LogInfo(_logStore, "Step on sourceless/brace line — auto-stepping-out");
                    model.CachedStackTraceResult = null;
                    _engineQuery.ExecuteStepOutOnEngine(model);
                    return true;
                }
            }

            model.StepOriginLocation = null;
            SendStopDapResponseAndProcessCommands(model, dbgEngWrapperModel, reason);
            return true;
        }

        DrainPendingCommands(model);
        _log.LogVerbose(_logStore, "System stop — auto-continuing");
        model.Stopped.Reset();
        _wrapper.SetExecutionStatus(dbgEngWrapperModel, EngineExecutionStatus.Go);
        return true;
    }

    private void LogDebuggerEvent(NativeDebuggerModel model, DbgEngWrapperModel dbgEngWrapperModel)
    {
        // Get last event info for logging
        EngineEventInfo evt = _wrapper.GetLastEventInfo(dbgEngWrapperModel);
        _log.LogVerbose(_logStore, $"Event: type=0x{evt.Type:X} pid={evt.ProcessId} tid={evt.ThreadId} desc=\"{evt.Description}\"");
        _log.LogVerbose(_logStore, $"State: configDone={model.ConfigDone} hitUserBp={model.HitUserBreakpoint} stepping={model.Stepping} pause={model.PauseRequested}");
    }

    private void SendStopDapResponseAndProcessCommands(NativeDebuggerModel model, DbgEngWrapperModel dbgEngWrapperModel, string reason)
    {
        uint threadId = _wrapper.GetCurrentThreadId(dbgEngWrapperModel);
        _log.LogInfo(_logStore, $"User stop: reason={reason} threadId={threadId}");
        _server.SendEvent(_transport, "stopped", new StoppedEventBody
        {
            Reason = reason,
            ThreadId = (int)threadId,
            AllThreadsStopped = true,
        });
        ProcessCommandsUntilResume(model);
    }

    private string? DetermineStopReason(NativeDebuggerModel model)
    {
        // Check for active managed step (temp BP approach for step-over/out).
        if (model.ActiveManagedStep != null)
        {
            if (model.HitUserBreakpoint)
            {
                bool isTempBp = model.ActiveManagedStep.TempBreakpointIds.Contains(model.LastHitBpId);
                // Also check for step-into deferred BP: transient BP from ENTER hook
                // set by HandleEnterBreakpoint for a deferred BP with BpId=-1.
                bool isStepIntoEnterBp = !isTempBp
                    && model.DeferredManagedBreakpoints.Exists(d => d.BpId == -1);
                model.HitUserBreakpoint = false;

                if (isTempBp || isStepIntoEnterBp)
                {
                    // Step BP fired — step complete.
                    // Remove step-into deferred BPs (BpId=-1) so they don't fire again.
                    _ = model.DeferredManagedBreakpoints.RemoveAll(d => d.BpId == -1);
                    model.RebuildDeferredBreakpointIndex();
                    CompleteManagedStep(model);
                    return "step";
                }

                // Real user BP hit during managed step — cancel step, report breakpoint.
                _ = model.DeferredManagedBreakpoints.RemoveAll(d => d.BpId == -1);
                model.RebuildDeferredBreakpointIndex();
                CompleteManagedStep(model);
                return "breakpoint";
            }

            // Non-BP stop during managed step (e.g. exception) — cancel step.
            if (model.Stepping || model.PauseRequested)
            {
                CompleteManagedStep(model);
                // Fall through to normal handling below.
            }
        }

        string? reason = null;
        if (model.HitUserBreakpoint)
        {
            model.HitUserBreakpoint = false;
            reason = "breakpoint";
        }
        else if (model.Stepping)
        {
            model.Stepping = false;
            reason = "step";
        }
        else if (model.PauseRequested)
        {
            model.PauseRequested = false;
            reason = "pause";
        }

        return reason;
    }

    /// <summary>
    /// Completes a managed step by removing temp breakpoints and clearing state.
    /// </summary>
    private void CompleteManagedStep(NativeDebuggerModel model)
    {
        if (model.ActiveManagedStep == null)
            return;

        foreach (uint bpId in model.ActiveManagedStep.TempBreakpointIds)
        {
            _ = _wrapper.RemoveBreakpoint(model.Wrapper, bpId);
            _ = model.UserBreakpointIds.Remove(bpId);
        }

        _log.LogInfo(_logStore,
            $"Managed step complete: removed {model.ActiveManagedStep.TempBreakpointIds.Count} temp BPs");
        model.ActiveManagedStep = null;
        model.StepOriginLocation = null;
    }

    /// <summary>
    /// Checks whether the current IP is on a source line that has no useful code —
    /// e.g. a closing brace, a sourceless JIT thunk, or a frame with no source at all.
    /// Used to auto-step-out after a native step lands on a trivial line.
    /// </summary>
    /// <summary>
    /// After a native step completes, checks whether the current IP is on a useful
    /// source line. Returns <see cref="StepAutoAction.None"/> if normal,
    /// <see cref="StepAutoAction.ReStep"/> if on the same line (no progress),
    /// or <see cref="StepAutoAction.StepOut"/> if on a closing brace or sourceless frame.
    /// </summary>
    private StepAutoAction CheckStepLanding(NativeDebuggerModel model)
    {
        NativeStackFrame[] frames = _wrapper.GetStackTrace(model.Wrapper, 1);
        if (frames.Length == 0)
            return StepAutoAction.None;

        ulong ip = frames[0].InstructionOffset;
        (uint Line, string File)? lineInfo = _wrapper.GetLineByOffset(model.Wrapper, ip);

        // No source at all — sourceless (JIT thunk, etc.).
        if (lineInfo == null || lineInfo.Value.Line == 0)
        {
            _log.LogVerbose(_logStore, $"CheckStepLanding: ip=0x{ip:X} no source → StepOut");
            return StepAutoAction.StepOut;
        }

        string file = lineInfo.Value.File;
        int line = (int)lineInfo.Value.Line;

        // Same source line as before the step — no progress (e.g. multi-instruction
        // statement like "return a + b;"). Re-step to continue advancing.
        if (model.StepOriginLocation is var (origFile, origLine)
            && origLine == line
            && string.Equals(origFile, file, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogVerbose(_logStore, $"CheckStepLanding: same line {line} → ReStep");
            return StepAutoAction.ReStep;
        }

        // Check if this is a closing brace — no meaningful code, step out.
        try
        {
            if (!model.SourceFileCache.TryGetValue(file, out string[]? lines))
            {
                if (File.Exists(file))
                {
                    lines = File.ReadAllLines(file);
                    model.SourceFileCache[file] = lines;
                }
            }

            if (lines != null)
            {
                int lineIndex = line - 1;
                if (lineIndex >= 0 && lineIndex < lines.Length)
                {
                    string trimmed = lines[lineIndex].Trim();
                    if (trimmed == "}" || trimmed == "};")
                    {
                        _log.LogVerbose(_logStore, $"CheckStepLanding: closing brace at {file}:{line} → StepOut");
                        return StepAutoAction.StepOut;
                    }
                }
            }
        }
        catch { }

        return StepAutoAction.None;
    }

    private enum StepAutoAction { None, ReStep, StepOut }

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
                string reason;
                if (model.HitUserBreakpoint)
                {
                    model.HitUserBreakpoint = false;
                    reason = "breakpoint";
                }
                else
                {
                    model.Stepping = false;
                    reason = "step";
                }
                uint threadId = _wrapper.GetCurrentThreadId(model.Wrapper);
                _log.LogInfo(_logStore, $"ProcessCommandsUntilResume: step-into done, reason={reason} threadId={threadId}");
                _server.SendEvent(_transport, "stopped", new StoppedEventBody
                {
                    Reason = reason,
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
                    Reason = "step",
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
