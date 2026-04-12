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
        _log.LogInfo(_logStore, "WaitForEvent...");
        model.InWaitForEvent = true;
        WaitForEventResult waitResult = _wrapper.WaitForEvent(dbgEngWrapperModel);
        model.InWaitForEvent = false;
        _log.LogInfo(_logStore, $"WaitForEvent returned {waitResult}");
        if (waitResult == WaitForEventResult.Failed)
        {
            _log.LogInfo(_logStore, $"WaitForEvent failed, terminated={model.Terminated} exited={model.TargetExited}");
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
            SendStopDapResponseAndProcessCommands(model, dbgEngWrapperModel, reason);
            return true;
        }

        DrainPendingCommands(model);
        _log.LogInfo(_logStore, "System stop — auto-continuing");
        model.Stopped.Reset();
        _wrapper.SetExecutionStatus(dbgEngWrapperModel, EngineExecutionStatus.Go);
        return true;
    }

    private void LogDebuggerEvent(NativeDebuggerModel model, DbgEngWrapperModel dbgEngWrapperModel)
    {
        // Get last event info for logging
        EngineEventInfo evt = _wrapper.GetLastEventInfo(dbgEngWrapperModel);
        _log.LogInfo(_logStore, $"Event: type=0x{evt.Type:X} pid={evt.ProcessId} tid={evt.ThreadId} desc=\"{evt.Description}\"");
        _log.LogInfo(_logStore, $"State: configDone={model.ConfigDone} hitUserBp={model.HitUserBreakpoint} stepping={model.Stepping} pause={model.PauseRequested}");
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

    private static string? DetermineStopReason(NativeDebuggerModel model)
    {
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
            _log.LogInfo(_logStore, "DrainPendingCommands: executing queued command");
            cmd();
            EngineExecutionStatus status = _wrapper.GetExecutionStatus(model.Wrapper);
            if (status != EngineExecutionStatus.Break
                && status != EngineExecutionStatus.NoDebuggee)
            {
                _log.LogInfo(_logStore, "DrainPendingCommands: command caused resume — stopping drain");
                return;
            }
        }
    }

    private void ProcessCommandsUntilResume(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "ProcessCommandsUntilResume: waiting for commands");
        while (!model.Terminated)
        {
            Action cmd;
            try
            {
                cmd = model.Commands.Take();
            }
            catch (InvalidOperationException)
            {
                _log.LogInfo(_logStore, "ProcessCommandsUntilResume: collection completed");
                break;
            }

            _log.LogInfo(_logStore, "ProcessCommandsUntilResume: executing command");
            cmd();

            EngineExecutionStatus status = _wrapper.GetExecutionStatus(model.Wrapper);
            _log.LogInfo(_logStore, $"ProcessCommandsUntilResume: execStatus={status}");
            if (status != EngineExecutionStatus.Break
                && status != EngineExecutionStatus.NoDebuggee)
            {
                _log.LogInfo(_logStore, "ProcessCommandsUntilResume: resuming");
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
