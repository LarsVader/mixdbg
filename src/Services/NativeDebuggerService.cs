using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless dbgeng wrapper service. All mutable state lives in
/// <see cref="NativeDebuggerModel"/>.
/// </summary>
internal sealed class NativeDebuggerService(
    IDapServer _server,
    DapServerModel _transport,
    ILoggingService _log,
    LogStore _logStore,
    ISourceFileService _sourceFiles,
    IManagedDebugger _managedDebugger,
    IProfilerPipeService _profilerPipe,
    IDbgEngWrapper _wrapper) : INativeDebugger
{
    public NativeDebuggerModel CreateModel()
    {
        var model = new NativeDebuggerModel();
        model.DisposeAction = () =>
        {
            model.Terminated = true;
            model.Commands.CompleteAdding();
            model.EngineThread?.Join(3000);
            model.ProfilerAckEvent?.Set(); // Unblock profiler if waiting.
            model.ProfilerRehookEvent?.Set(); // Unblock rehook watcher.
            model.ProfilerAckEvent?.Dispose();
            model.ProfilerRehookEvent?.Dispose();
            model.ProfilerPipeReader?.Dispose();
            model.ProfilerPipe?.Dispose();
            model.ProfilerReaderThread?.Join(1000);
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

    // ── Thread-safe methods (callable from any thread) ──

    /// <summary>Requests the target to break. Thread-safe — uses SetInterrupt.</summary>
    public void Break(NativeDebuggerModel model)
    {
        model.PauseRequested = true;
        _wrapper.SetInterrupt(model.Wrapper);
    }

    /// <summary>Terminates the debugged process and wakes the engine thread to exit.</summary>
    public void Terminate(NativeDebuggerModel model)
    {
        model.Terminated = true;
        if (!model.TargetExited)
            _wrapper.TerminateSession(model.Wrapper);
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

    // ── Private ─────────────────────────────────────────

    private void CreateEngine(NativeDebuggerModel model)
    {
        var wrapperModel = _wrapper.CreateModel();
        model.Wrapper = wrapperModel;

        _wrapper.CreateEngine(wrapperModel);

        // Subscribe to engine events.
        wrapperModel.OnBreakpointHit += bpId => OnBreakpoint(model, bpId);
        wrapperModel.OnExitProcess += code => OnExitProcess(model, code);
        wrapperModel.OnCreateProcess += name =>
        {
            _server.SendEvent(_transport, "output", new OutputEventBody
            {
                Category = "console",
                Output = $"[mixdbg] Process created: {name}\n",
            });
        };
        wrapperModel.OnLoadModule += (mod, img, baseOffset) =>
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
                _managedDebugger.TryBindManagedBreakpointsOnModuleLoad(model);
            }
        };
        wrapperModel.OnClrNotification += () =>
        {
            // When deferred managed breakpoints exist after configDone,
            // break so the engine loop can recreate the DAC and check for JIT.
            if (model.ConfigDone && model.DeferredManagedBreakpoints.Count > 0)
                wrapperModel.ClrNotificationShouldBreak = true;
        };
        wrapperModel.OnExceptionBreakpoint += addr =>
        {
            // Check if this EXCEPTION_BREAKPOINT is from a managed IL breakpoint.
            if (model.ManagedInitialized &&
                (model.ManagedBreakpointAddresses.Contains(addr) ||
                 (model.CorWrapper?.HasLegacyBreakpoints == true && !model.UserBreakpointIds.Contains(model.LastHitBpId))))
            {
                model.HitUserBreakpoint = true;
                _log.LogInfo(_logStore, $"Managed breakpoint hit at 0x{addr:X}");
            }
        };

        _wrapper.InitializeSymbols(wrapperModel, model.SymbolPath, null);
    }

    public void StartEngineThread(NativeDebuggerModel model)
    {
        model.EngineThread = new Thread(() => EngineLoop(model))
        {
            Name = "dbgeng-engine",
            IsBackground = true,
        };
        model.EngineThread.Start();
    }

    private void EngineLoop(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "EngineLoop started — initializing dbgeng on engine thread");
        try
        {
            // All dbgeng COM calls must happen on this thread.
            CreateEngine(model);

            var w = model.Wrapper;

            if (model.IsAttach)
            {
                _log.LogInfo(_logStore, $"Attach: pid={model.AttachPid}");
                _wrapper.AttachProcess(w, model.AttachPid);
            }
            else
            {
                if (model.LaunchCwd != null)
                    _wrapper.InitializeSymbols(w, null, model.LaunchCwd);

                // Disable tiered compilation and ReadyToRun so JIT produces stable
                // native addresses for hardware breakpoints on managed methods.
                Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", "0");
                Environment.SetEnvironmentVariable("DOTNET_ReadyToRun", "0");

                // Set up CLR profiler for real-time JIT notifications.
                _profilerPipe.SetupProfilerPipe(model);

                var cmdLine = model.LaunchProgram!;
                if (model.LaunchArgs is { Length: > 0 })
                    cmdLine += " " + string.Join(" ", model.LaunchArgs);
                _log.LogInfo(_logStore, $"Launch: CreateProcess({cmdLine})");
                _wrapper.CreateProcess(w, cmdLine);
                _log.LogInfo(_logStore, "Launch: CreateProcess succeeded");

                // Start reading profiler notifications in the background.
                _profilerPipe.StartProfilerReader(model);
            }

            // Signal the main thread that init is done.
            model.EngineReady.Set();

            while (!model.Terminated)
            {
                _log.LogInfo(_logStore, "WaitForEvent...");
                int hr = _wrapper.WaitForEvent(w);
                _log.LogInfo(_logStore, $"WaitForEvent returned hr=0x{hr:X8}");
                if (hr < 0)
                {
                    _log.LogInfo(_logStore, $"WaitForEvent failed hr=0x{hr:X8}, terminated={model.Terminated} exited={model.TargetExited}");
                    break;
                }

                // Target is now stopped.
                model.Stopped.Set();

                // Initialize managed debugging when CLR is first detected.
                if (model.ClrLoaded && !model.ManagedInitialized)
                    _managedDebugger.TryInitializeManaged(model);

                if (model.TargetExited)
                {
                    _log.LogInfo(_logStore, "Target exited, sending terminated event");
                    _server.SendEvent(_transport, "terminated", new TerminatedEventBody());
                    break;
                }

                // Get last event info for logging
                var evt = _wrapper.GetLastEventInfo(w);
                _log.LogInfo(_logStore, $"Event: type=0x{evt.Type:X} pid={evt.ProcessId} tid={evt.ThreadId} desc=\"{evt.Description}\"");
                _log.LogInfo(_logStore, $"State: configDone={model.ConfigDone} hitUserBp={model.HitUserBreakpoint} stepping={model.Stepping} pause={model.PauseRequested}");

                if (!model.ConfigDone)
                {
                    _log.LogInfo(_logStore, "Pre-configDone: processing commands until resume");
                    ProcessCommandsUntilResume(model);
                    continue;
                }

                // Process JIT notifications and resolve deferred managed breakpoints.
                _managedDebugger.ProcessPendingManagedBreakpoints(model);

                // Handle ENTER notification from profiler — set transient BP, ACK, auto-continue.
                if (_managedDebugger.HandleEnterBreakpoint(model))
                {
                    model.Stopped.Reset();
                    _wrapper.SetExecutionStatus(w, EngineExecutionStatus.Go);
                    continue;
                }

                // After configurationDone: determine stop reason.
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

                if (reason != null)
                {
                    var threadId = _wrapper.GetCurrentThreadId(w);
                    _log.LogInfo(_logStore, $"User stop: reason={reason} threadId={threadId}");
                    _server.SendEvent(_transport, "stopped", new StoppedEventBody
                    {
                        Reason = reason,
                        ThreadId = (int)threadId,
                        AllThreadsStopped = true,
                    });
                    ProcessCommandsUntilResume(model);
                }
                else
                {
                    _log.LogInfo(_logStore, "System stop — auto-continuing");
                    model.Stopped.Reset();
                    _wrapper.SetExecutionStatus(w, EngineExecutionStatus.Go);
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
    }

    /// <summary>
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

            var status = _wrapper.GetExecutionStatus(model.Wrapper);
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

    private void OnBreakpoint(NativeDebuggerModel model, uint bpId)
    {
        model.LastHitBpId = bpId;
        model.HitUserBreakpoint = model.UserBreakpointIds.Contains(bpId)
            || model.ManagedBreakpointIds.Contains(bpId);
        _log.LogInfo(_logStore, $"OnBreakpoint: id={bpId} isUser={model.HitUserBreakpoint} (native: [{string.Join(",", model.UserBreakpointIds)}] managed: [{string.Join(",", model.ManagedBreakpointIds)}])");

        // Send verified update so nvim-dap clears the "rejected" marker.
        if (model.HitUserBreakpoint)
        {
            // Find the source:line for this breakpoint ID
            var entry = model.BreakpointIds.FirstOrDefault(kv => kv.Value == bpId);
            if (entry.Key != null)
            {
                var parts = entry.Key.Split(':', 2);
                var path = parts[0];
                var line = int.TryParse(parts[1], out var l) ? l : 0;
                _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                {
                    Reason = "changed",
                    Breakpoint = new Breakpoint
                    {
                        Id = (int)bpId,
                        Verified = true,
                        Line = line,
                        Source = new Source
                        {
                            Name = Path.GetFileName(path),
                            Path = path,
                        },
                    },
                });
            }
        }
    }

    private void OnExitProcess(NativeDebuggerModel model, uint exitCode)
    {
        model.TargetExited = true;
        _server.SendEvent(_transport, "output", new OutputEventBody
        {
            Category = "console",
            Output = $"[mixdbg] Process exited with code {exitCode}\n",
        });
    }

    public Breakpoint[] SetBreakpointsOnEngine(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested)
    {
        _log.LogInfo(_logStore, $"SetBreakpointsOnEngine: file={filePath} count={requested.Length}");
        foreach (var r in requested)
            _log.LogInfo(_logStore, $"  requested: line={r.Line}");

        // Delegate managed files to the managed debugger.
        if (!_sourceFiles.IsNativeFile(filePath))
        {
            if (model.ManagedInitialized)
            {
                _log.LogInfo(_logStore, $"  Delegating to managed debugger: {filePath}");
                return _managedDebugger.SetManagedBreakpoints(model, filePath, requested);
            }

            // CLR not loaded yet — store as pending, return optimistic verified: true.
            _log.LogInfo(_logStore, $"  CLR not ready, storing as pending managed bp: {filePath}");
            model.PendingManagedBreakpoints.Add(new SetBreakpointsArguments
            {
                Source = new Source { Name = Path.GetFileName(filePath), Path = filePath },
                Breakpoints = requested,
            });
            return requested.Select((bp, i) => new Breakpoint
            {
                Id = ++model.NextBpId,
                Verified = true,
                Line = bp.Line,
                Source = new Source { Name = Path.GetFileName(filePath), Path = filePath },
                Message = "Pending — managed debugger not yet initialized",
            }).ToArray();
        }

        var w = model.Wrapper;

        // Remove old breakpoints for this file
        var keysToRemove = model.BreakpointIds.Keys
            .Where(k => k.StartsWith(filePath + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keysToRemove)
        {
            if (model.BreakpointIds.TryGetValue(key, out var oldId))
            {
                _wrapper.RemoveBreakpoint(w, oldId);
                model.UserBreakpointIds.Remove(oldId);
                model.BreakpointIds.Remove(key);
            }
        }

        var results = new Breakpoint[requested.Length];
        for (int i = 0; i < requested.Length; i++)
        {
            var req = requested[i];
            var key = $"{filePath}:{req.Line}";

            var (offset, resolved) = _wrapper.GetOffsetByLine(w, (uint)req.Line, filePath);
            _log.LogInfo(_logStore, $"  GetOffsetByLine({req.Line}, {filePath}) -> resolved={resolved} offset=0x{offset:X}");
            if (!resolved)
            {
                // GetOffsetByLine failed — module probably not loaded yet.
                // Use deferred breakpoint via bu command instead.
                _log.LogInfo(_logStore, $"  Trying deferred breakpoint: bu `{filePath}:{req.Line}`");
                var (deferredId, buOk) = _wrapper.AddDeferredBreakpoint(w, filePath, req.Line);
                _log.LogInfo(_logStore, $"  bu result: ok={buOk} id={deferredId}");

                if (buOk)
                {
                    model.BreakpointIds[key] = deferredId;
                    model.UserBreakpointIds.Add(deferredId);
                    results[i] = new Breakpoint
                    {
                        Id = (int)deferredId,
                        Verified = true,
                        Line = req.Line,
                        Source = new Source
                        {
                            Name = Path.GetFileName(filePath),
                            Path = filePath,
                        },
                    };
                }
                else
                {
                    results[i] = new Breakpoint
                    {
                        Id = ++model.NextBpId,
                        Verified = false,
                        Line = req.Line,
                        Message = "Could not resolve source line",
                    };
                }
                continue;
            }

            var (bpId, bpOk) = _wrapper.AddCodeBreakpoint(w, offset);
            if (!bpOk)
            {
                results[i] = new Breakpoint
                {
                    Id = ++model.NextBpId,
                    Verified = false,
                    Line = req.Line,
                    Message = "Failed to create breakpoint",
                };
                continue;
            }

            model.BreakpointIds[key] = bpId;
            model.UserBreakpointIds.Add(bpId);

            // Resolve back to verify the actual line
            int actualLine = req.Line;
            var lineInfo = _wrapper.GetLineByOffset(w, offset);
            if (lineInfo != null)
                actualLine = (int)lineInfo.Value.Line;

            results[i] = new Breakpoint
            {
                Id = (int)bpId,
                Verified = true,
                Line = actualLine,
                Source = new Source
                {
                    Name = Path.GetFileName(filePath),
                    Path = filePath,
                },
            };
        }
        return results;
    }

    public StackFrame[] GetStackTraceOnEngine(NativeDebuggerModel model, int maxFrames)
    {
        // Cache the stack trace result per stop. Repeated stackTrace requests from
        // nvim-dap (one per thread) all return the event thread's stack anyway,
        // but the redundant GetStackTrace + symbol lookups degrade the DAC,
        // breaking CreateRuntime for deferred breakpoint resolution.
        if (model.CachedStackTraceResult != null)
            return model.CachedStackTraceResult;

        var w = model.Wrapper;
        var nativeFrames = _wrapper.GetStackTrace(w, maxFrames);
        if (nativeFrames.Length == 0)
            return [];

        var result = new StackFrame[nativeFrames.Length];
        for (int i = 0; i < nativeFrames.Length; i++)
        {
            var ip = nativeFrames[i].InstructionOffset;
            string name = $"0x{ip:X}";
            Source? source = null;
            int line = 0;

            // Try to resolve function name
            var nameInfo = _wrapper.GetNameByOffset(w, ip);
            if (nameInfo != null)
            {
                name = nameInfo.Value.Displacement > 0
                    ? $"{nameInfo.Value.Name}+0x{nameInfo.Value.Displacement:x}"
                    : nameInfo.Value.Name;
            }

            // Try to resolve source location
            var lineInfo = _wrapper.GetLineByOffset(w, ip);
            if (lineInfo != null)
            {
                line = (int)lineInfo.Value.Line;
                source = new Source
                {
                    Name = Path.GetFileName(lineInfo.Value.File),
                    Path = lineInfo.Value.File,
                };
            }

            // Fallback: if dbgeng can't resolve, try the profiler's JIT method map.
            if (source == null && model.JitMethodMap.Count > 0)
            {
                try
                {
                    var profilerFrame = _managedDebugger.ResolveFrameFromProfilerData(model, ip);
                    if (profilerFrame != null)
                    {
                        name = profilerFrame.Value.Name;
                        source = profilerFrame.Value.Source;
                        line = profilerFrame.Value.Line;
                    }
                }
                catch { }
            }

            _log.LogInfo(_logStore, $"  Frame {i}: ip=0x{ip:X} name={name} line={line}");

            result[i] = new StackFrame
            {
                Id = i + 1, // 1-based
                Name = name,
                Source = source,
                Line = line,
                Column = 0,
            };
        }

        // Merge managed frame info from ClrMD.
        if (model.ManagedInitialized)
            _managedDebugger.MergeManagedFrames(model, result);

        model.CachedStackTraceResult = result;
        return result;
    }

    public Scope[] GetScopesOnEngine(NativeDebuggerModel model, int frameId)
    {
        int localsRef = _wrapper.SetScopeAndGetLocals(model.Wrapper, frameId);
        _log.LogInfo(_logStore, $"SetScopeAndGetLocals(frameId={frameId}) -> ref={localsRef}");
        if (localsRef == 0)
            return [];

        return
        [
            new Scope
            {
                Name = "Locals",
                VariablesReference = localsRef,
                Expensive = false,
            }
        ];
    }

    public Variable[] GetVariablesOnEngine(NativeDebuggerModel model, int variablesReference)
    {
        _log.LogInfo(_logStore, $"GetVariables: ref={variablesReference}");
        var vars = _wrapper.GetVariables(model.Wrapper, variablesReference);

        var result = new Variable[vars.Length];
        for (int i = 0; i < vars.Length; i++)
        {
            var v = vars[i];
            _log.LogInfo(_logStore, $"  Var: name=\"{v.Name}\" type=\"{v.Type}\" value=\"{v.Value}\" childRef={v.VariablesReference}");
            result[i] = new Variable
            {
                Name = v.Name,
                Value = v.Value,
                Type = v.Type,
                VariablesReference = v.VariablesReference,
            };
        }
        return result;
    }

    public DapThread[] GetThreadsOnEngine(NativeDebuggerModel model)
    {
        var threads = _wrapper.GetThreads(model.Wrapper);
        if (threads.Length == 0)
            return [new DapThread { Id = 1, Name = "Main Thread" }];

        var result = new DapThread[threads.Length];
        for (int i = 0; i < threads.Length; i++)
        {
            result[i] = new DapThread
            {
                Id = (int)threads[i].EngineId,
                Name = $"Thread {threads[i].SystemId} (dbg:{threads[i].EngineId})",
            };
        }
        return result;
    }

    // ── Engine-thread command methods ──────────────────

    /// <summary>No-op command that unblocks the engine thread's <c>Commands.Take()</c>.</summary>
    private static void WakeEngineThread() { }

    // ── Engine-thread methods (caller must dispatch via Commands.Add) ──

    /// <summary>Resumes execution, clears transient BPs, and re-enables profiler hooks.</summary>
    public void ExecuteContinueOnEngine(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Continue executing: SetExecutionStatus(GO)");
        _managedDebugger.RemoveTransientManagedBreakpoints(model);
        model.ProfilerRehookEvent?.Set();
        model.ConfigDone = true;
        model.CachedStackTraceResult = null;
        _wrapper.ClearVariables(model.Wrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
    }

    /// <summary>Steps over/into by setting the execution status.</summary>
    public void ExecuteStepOnEngine(NativeDebuggerModel model, EngineExecutionStatus stepKind)
    {
        _managedDebugger.RemoveTransientManagedBreakpoints(model);
        _wrapper.ClearVariables(model.Wrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, stepKind);
    }

    /// <summary>Steps out via the dbgeng "gu" (go up) command.</summary>
    public void ExecuteStepOutOnEngine(NativeDebuggerModel model)
    {
        _wrapper.ClearVariables(model.Wrapper);
        _wrapper.ExecuteCommand(model.Wrapper, "gu");
    }

    /// <summary>Gets the engine thread ID of the last event.</summary>
    public int GetStoppedThreadIdOnEngine(NativeDebuggerModel model)
    {
        return (int)_wrapper.GetEventThreadId(model.Wrapper);
    }
}
