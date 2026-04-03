using ClrDebug;
using MixDbg.Dap;
using MixDbg.Engine.Clr;
using MixDbg.Engine.Sos;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// ICorDebug-based debug engine. Uses ICorDebug interop debugging for managed
/// breakpoints (IL-level, handles pre-JIT automatically) and native debugging.
/// Replaces the dbgeng-based <see cref="NativeDebuggerService"/> for M4V2.
/// </summary>
internal sealed class CorDebugEngineService(
    IDapServer server,
    DapServerModel transport,
    ILoggingService log,
    LogStore logStore) : ICorDebugEngine
{
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;

    public CorDebugEngineModel CreateModel()
    {
        var model = new CorDebugEngineModel();
        model.DisposeAction = () =>
        {
            model.Terminated = true;
            model.Commands.CompleteAdding();
            try { model.Process?.Stop(0); } catch { }
            try { model.Process?.Detach(); } catch { }
            try { model.CorDebug?.Terminate(); } catch { }
        };
        return model;
    }

    public void Launch(CorDebugEngineModel model, string program, string? cwd, string[]? args = null)
    {
        _log.LogInfo(_logStore, $"Launch: {program} cwd={cwd}");

        var dbgShim = DbgShimBootstrap.LoadDbgShim();
        _log.LogInfo(_logStore, "dbgshim.dll loaded");

        var (corDebug, pid) = DbgShimBootstrap.LaunchAndAttach(dbgShim, program, cwd, args);
        _log.LogInfo(_logStore, $"CLR startup complete: pid={pid}");

        model.CorDebug = corDebug;
        model.ProcessId = pid;

        // Set up callbacks.
        model.ManagedCallbacks = new ManagedCallbackHandler();
        WireCallbacks(model);

        // Initialize ICorDebug and attach.
        corDebug.Initialize();
        corDebug.SetManagedHandler(model.ManagedCallbacks.Callback);
        // TODO: SetUnmanagedHandler for interop/native debugging (Phase 7)
        corDebug.DebugActiveProcess(pid, win32Attach: false); // managed-only for now

        _log.LogInfo(_logStore, "ICorDebug attached (managed-only mode)");
        model.EngineReady.Set();
    }

    public void Attach(CorDebugEngineModel model, uint pid)
    {
        throw new NotImplementedException("Attach not yet implemented for ICorDebug engine");
    }

    public void Continue(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "Continue");
        model.ConfigDone = true;
        model.Stopped.Reset();
        model.HitUserBreakpoint = false;
        try
        {
            model.Process?.Continue(false);
        }
        catch (System.Runtime.InteropServices.COMException ex)
            when (ex.HResult == unchecked((int)0x8013132E)) // CORDBG_E_SUPERFLOUS_CONTINUE
        {
            // Process is already running — safe to ignore.
            _log.LogInfo(_logStore, "Continue: process already running (CORDBG_E_SUPERFLOUS_CONTINUE)");
        }
    }

    public void Break(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "Break requested");
        model.PauseRequested = true;
        model.Process?.Stop(0);
    }

    public void StepOver(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "StepOver — not yet implemented");
        model.Stepping = true;
        model.Stopped.Reset();
        // TODO: ICorDebugStepper with IL offset ranges (Phase 8)
        model.Process?.Continue(false);
    }

    public void StepInto(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "StepInto — not yet implemented");
        model.Stepping = true;
        model.Stopped.Reset();
        model.Process?.Continue(false);
    }

    public void StepOut(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "StepOut — not yet implemented");
        model.Stepping = true;
        model.Stopped.Reset();
        model.Process?.Continue(false);
    }

    public Breakpoint[] SetBreakpoints(CorDebugEngineModel model, string filePath, SourceBreakpoint[] requested)
    {
        _log.LogInfo(_logStore, $"SetBreakpoints: file={filePath} count={requested.Length}");

        // Clear existing breakpoints for this file.
        if (model.FileBreakpointIds.TryGetValue(filePath, out var existingIds))
        {
            foreach (var id in existingIds)
            {
                if (model.ManagedBreakpoints.TryGetValue(id, out var bp))
                {
                    try { bp.Activate(false); } catch { }
                    model.ManagedBreakpoints.Remove(id);
                }
            }
            existingIds.Clear();
        }

        // Remove any pending breakpoints for this file.
        model.PendingBreakpoints.RemoveAll(p =>
            p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        var results = new Breakpoint[requested.Length];
        for (int i = 0; i < requested.Length; i++)
        {
            results[i] = SetOneBreakpoint(model, filePath, requested[i]);
        }
        return results;
    }

    private Breakpoint SetOneBreakpoint(CorDebugEngineModel model, string filePath, SourceBreakpoint req)
    {
        var bpId = ++model.NextBpId;

        // Try to bind the breakpoint to a loaded module.
        if (TryBindBreakpoint(model, filePath, req.Line, bpId))
        {
            return new Breakpoint
            {
                Id = bpId,
                Verified = true,
                Line = req.Line,
                Source = new Source { Name = Path.GetFileName(filePath), Path = filePath },
            };
        }

        // Module not loaded yet — store as pending, will bind on LoadModule.
        model.PendingBreakpoints.Add(new PendingBreakpoint(filePath, req.Line, bpId));
        _log.LogInfo(_logStore, $"  Breakpoint #{bpId} pending — module not loaded yet");

        if (!model.FileBreakpointIds.ContainsKey(filePath))
            model.FileBreakpointIds[filePath] = new List<int>();
        model.FileBreakpointIds[filePath].Add(bpId);

        return new Breakpoint
        {
            Id = bpId,
            Verified = true, // Optimistic — will bind on module load.
            Line = req.Line,
            Source = new Source { Name = Path.GetFileName(filePath), Path = filePath },
            Message = "Pending — module not yet loaded",
        };
    }

    /// <summary>
    /// Tries to bind a breakpoint to a loaded module via PDB resolution + ICorDebugCode::CreateBreakpoint.
    /// Returns true if the breakpoint was bound.
    /// </summary>
    private bool TryBindBreakpoint(CorDebugEngineModel model, string filePath, int line, int bpId)
    {
        using var mapper = new PdbSourceMapper();

        foreach (var loaded in model.Modules.Values)
        {
            if (loaded.PdbPath == null || loaded.Path == null)
                continue;

            var result = mapper.FindMethodAtLine(loaded.Path, filePath, line);
            if (result == null)
                continue;

            var (_, _, methodToken, ilOffset) = result.Value;
            _log.LogInfo(_logStore, $"  Resolved {filePath}:{line} -> token=0x{methodToken:X8} IL={ilOffset} in {loaded.Path}");

            try
            {
                var function = loaded.Module.GetFunctionFromToken(methodToken);
                var ilCode = function.ILCode;
                var corBp = ilCode.CreateBreakpoint(ilOffset);
                corBp.Activate(true);

                model.ManagedBreakpoints[bpId] = corBp;
                if (!model.FileBreakpointIds.ContainsKey(filePath))
                    model.FileBreakpointIds[filePath] = new List<int>();
                model.FileBreakpointIds[filePath].Add(bpId);

                _log.LogInfo(_logStore, $"  IL breakpoint #{bpId} set and activated");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(_logStore, $"  CreateBreakpoint failed: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// Tries to bind all pending breakpoints against newly loaded modules.
    /// Called from the LoadModule callback.
    /// </summary>
    private void TryBindPendingBreakpoints(CorDebugEngineModel model)
    {
        var resolved = new List<PendingBreakpoint>();

        foreach (var pending in model.PendingBreakpoints)
        {
            if (TryBindBreakpoint(model, pending.FilePath, pending.Line, pending.BpId))
            {
                resolved.Add(pending);
                _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                {
                    Reason = "changed",
                    Breakpoint = new Breakpoint
                    {
                        Id = pending.BpId,
                        Verified = true,
                        Line = pending.Line,
                        Source = new Source
                        {
                            Name = Path.GetFileName(pending.FilePath),
                            Path = pending.FilePath,
                        },
                    },
                });
            }
        }

        foreach (var r in resolved)
            model.PendingBreakpoints.Remove(r);
    }

    public StackFrame[] GetStackTrace(CorDebugEngineModel model, int maxFrames)
    {
        if (model.StoppedThread == null)
            return [];

        var frames = new List<StackFrame>();
        int frameId = 1;

        try
        {
            using var mapper = new PdbSourceMapper();

            foreach (var chain in model.StoppedThread.Chains)
            {
                if (!chain.IsManaged)
                    continue;

                foreach (var frame in chain.Frames)
                {
                    if (frames.Count >= maxFrames)
                        break;

                    try
                    {
                        var function = frame.Function;
                        var module = function.Module;
                        var token = (int)function.Token;
                        var modulePath = module.Name;

                        // Get IL offset for source resolution.
                        int ilOffset = 0;
                        try
                        {
                            // Try to cast to ICorDebugILFrame for IL offset.
                            var ilFrame = frame as CorDebugILFrame;
                            if (ilFrame != null)
                            {
                                var ip = ilFrame.IP;
                                ilOffset = (int)ip.pnOffset;
                            }
                        }
                        catch { }

                        // Resolve source location via PDB.
                        Source? source = null;
                        int line = 0;

                        if (modulePath != null)
                        {
                            var srcLoc = mapper.GetSourceLocation(modulePath, token, ilOffset > 0 ? ilOffset : 1);
                            if (srcLoc != null)
                            {
                                source = new Source
                                {
                                    Name = Path.GetFileName(srcLoc.Value.File),
                                    Path = srcLoc.Value.File,
                                };
                                line = srcLoc.Value.Line;
                            }
                        }

                        // Build frame name from metadata.
                        var name = GetFrameName(function);

                        frames.Add(new StackFrame
                        {
                            Id = frameId++,
                            Name = name,
                            Source = source,
                            Line = line,
                            Column = 0,
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.LogInfo(_logStore, $"  Frame enumeration error: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(_logStore, $"GetStackTrace failed: {ex.Message}");
        }

        _log.LogInfo(_logStore, $"GetStackTrace: {frames.Count} frames");
        return frames.ToArray();
    }

    private static string GetFrameName(CorDebugFunction function)
    {
        try
        {
            var module = function.Module;
            var metaData = module.GetMetaDataInterface<MetaDataImport>();
            var methodProps = metaData.GetMethodProps(function.Token);
            var typeName = metaData.GetTypeDefProps(methodProps.pClass).szTypeDef;
            return $"{typeName}.{methodProps.szMethod}";
        }
        catch
        {
            return $"<frame token=0x{function.Token:X8}>";
        }
    }

    public Scope[] GetScopes(CorDebugEngineModel model, int frameId)
    {
        return [];
    }

    public Variable[] GetVariables(CorDebugEngineModel model, int variablesReference)
    {
        return [];
    }

    public DapThread[] GetThreads(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "GetThreads");
        // Return at least the main thread so DAP clients don't error.
        return [new DapThread { Id = 1, Name = "Main Thread" }];
    }

    public int GetStoppedThreadId(CorDebugEngineModel model)
    {
        return 1; // TODO: actual thread ID from callback
    }

    public void Terminate(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "Terminate");
        model.Terminated = true;
        try { model.Process?.Stop(0); } catch { }
        try { model.Process?.Terminate(0); } catch { }
    }

    public void Detach(CorDebugEngineModel model)
    {
        _log.LogInfo(_logStore, "Detach");
        model.Terminated = true;
        try { model.Process?.Detach(); } catch { }
    }

    // ── Private ─────────────────────────────────────────

    private void WireCallbacks(CorDebugEngineModel model)
    {
        model.ManagedCallbacks!.ProcessCreated += process =>
        {
            model.Process = process;
            _log.LogInfo(_logStore, "Managed callback: CreateProcess");
        };

        model.ManagedCallbacks.ProcessExited += () =>
        {
            model.TargetExited = true;
            _log.LogInfo(_logStore, "Managed callback: ExitProcess");
            _server.SendEvent(_transport, "terminated", new TerminatedEventBody());
        };

        model.ManagedCallbacks.ModuleLoaded += (appDomain, module) =>
        {
            var name = module.Name;
            _log.LogInfo(_logStore, $"Managed callback: LoadModule {name}");

            // Store the module for later breakpoint binding.
            var baseAddr = (long)module.BaseAddress;
            var pdbPath = name != null ? Path.ChangeExtension(name, ".pdb") : null;
            model.Modules[baseAddr] = new LoadedModule
            {
                Module = module,
                Path = name,
                PdbPath = pdbPath != null && File.Exists(pdbPath) ? pdbPath : null,
            };

            // Bind any pending breakpoints that match this module's PDB.
            if (model.PendingBreakpoints.Count > 0)
                TryBindPendingBreakpoints(model);
        };

        model.ManagedCallbacks.BreakpointHit += (appDomain, thread, breakpoint) =>
        {
            _log.LogInfo(_logStore, "Managed callback: Breakpoint hit");
            model.StoppedThread = thread;
            model.StoppedAppDomain = appDomain;
            model.HitUserBreakpoint = true;
            model.Stopped.Set();

            // Don't block the callback thread — return immediately.
            // The DAP client will send stackTrace/continue via the command queue.
            _server.SendEvent(_transport, "stopped", new StoppedEventBody
            {
                Reason = "breakpoint",
                ThreadId = 1,
                AllThreadsStopped = true,
            });
        };

        model.ManagedCallbacks.StepCompleted += (appDomain, thread) =>
        {
            _log.LogInfo(_logStore, "Managed callback: StepComplete");
            model.StoppedThread = thread;
            model.StoppedAppDomain = appDomain;
            model.Stepping = false;
            model.Stopped.Set();

            _server.SendEvent(_transport, "stopped", new StoppedEventBody
            {
                Reason = "step",
                ThreadId = 1,
                AllThreadsStopped = true,
            });
        };

        model.ManagedCallbacks.DebuggerBreak += (appDomain, thread) =>
        {
            _log.LogInfo(_logStore, "Managed callback: Break (Debugger.Break)");
            model.StoppedThread = thread;
            model.StoppedAppDomain = appDomain;
            model.Stopped.Set();

            _server.SendEvent(_transport, "stopped", new StoppedEventBody
            {
                Reason = "pause",
                ThreadId = 1,
                AllThreadsStopped = true,
            });
        };
    }

    /// <summary>
    /// Blocks the callback thread, processing queued commands (setBreakpoints, stackTrace, etc.)
    /// until a command resumes execution (Continue, Step).
    /// </summary>
    private void ProcessCommandsUntilResume(CorDebugEngineModel model)
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
                break;
            }

            cmd();

            // If the command resumed execution, exit the loop.
            if (!model.Stopped.IsSet)
            {
                _log.LogInfo(_logStore, "ProcessCommandsUntilResume: resumed");
                break;
            }
        }
    }
}
