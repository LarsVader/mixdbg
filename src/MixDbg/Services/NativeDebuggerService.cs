using System.Runtime.InteropServices;
using MixDbg.Dap;
using MixDbg.Engine.DbgEng;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless dbgeng wrapper service. All mutable state lives in
/// <see cref="NativeDebuggerModel"/>.
/// </summary>
internal sealed class NativeDebuggerService(
    IDapServer server,
    DapServerModel transport,
    ILoggingService log,
    LogStore logStore,
    ISourceFileService sourceFiles) : INativeDebugger
{
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly ISourceFileService _sourceFiles = sourceFiles;

    public NativeDebuggerModel CreateModel()
    {
        var model = new NativeDebuggerModel();
        model.DisposeAction = () =>
        {
            model.Terminated = true;
            model.Commands.CompleteAdding();
            model.EngineThread?.Join(3000);
            model.Commands.Dispose();
            model.Stopped.Dispose();
            model.EngineReady.Dispose();
        };
        return model;
    }

    public void Attach(NativeDebuggerModel model, uint pid, string? symbolPath)
    {
        model.IsAttach = true;
        model.AttachPid = pid;
        model.SymbolPath = symbolPath;
        StartEngineThread(model);
        model.EngineReady.Wait();
        if (model.EngineInitError != null)
            throw model.EngineInitError;
    }

    public void Launch(NativeDebuggerModel model, string program, string? cwd, string? symbolPath)
    {
        model.IsAttach = false;
        model.LaunchProgram = program;
        model.LaunchCwd = cwd;
        model.SymbolPath = symbolPath;
        StartEngineThread(model);
        model.EngineReady.Wait();
        if (model.EngineInitError != null)
            throw model.EngineInitError;
    }

    public void Continue(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Continue queued");
        model.Variables.Clear();
        model.Commands.Add(() =>
        {
            _log.LogInfo(_logStore, "Continue executing: SetExecutionStatus(GO)");
            model.ConfigDone = true;
            Check(model.Control.SetExecutionStatus(DebugStatus.Go));
        });
    }

    public void Break(NativeDebuggerModel model)
    {
        model.PauseRequested = true;
        model.Control.SetInterrupt(0); // DEBUG_INTERRUPT_ACTIVE
    }

    public void StepOver(NativeDebuggerModel model)
    {
        QueueStep(model, DebugStatus.StepOver);
    }

    public void StepInto(NativeDebuggerModel model)
    {
        QueueStep(model, DebugStatus.StepInto);
    }

    public void StepOut(NativeDebuggerModel model)
    {
        // dbgeng doesn't have a direct step-out status.
        // Use the "gu" (go up) command instead.
        model.Variables.Clear();
        model.Commands.Add(() =>
        {
            model.Control.Execute(DebugOutCtl.Ignore, "gu", DebugExecute.NotLogged);
        });
    }

    public Breakpoint[] SetBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested)
    {
        var tcs = new TaskCompletionSource<Breakpoint[]>();

        model.Commands.Add(() =>
        {
            try
            {
                tcs.SetResult(SetBreakpointsOnEngine(model, filePath, requested));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.Result;
    }

    public StackFrame[] GetStackTrace(NativeDebuggerModel model, int maxFrames)
    {
        var tcs = new TaskCompletionSource<StackFrame[]>();

        model.Commands.Add(() =>
        {
            try
            {
                tcs.SetResult(GetStackTraceOnEngine(model, maxFrames));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.Result;
    }

    public Scope[] GetScopes(NativeDebuggerModel model, int frameId)
    {
        var tcs = new TaskCompletionSource<Scope[]>();

        model.Commands.Add(() =>
        {
            try
            {
                tcs.SetResult(GetScopesOnEngine(model, frameId));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.Result;
    }

    public Variable[] GetVariables(NativeDebuggerModel model, int variablesReference)
    {
        var tcs = new TaskCompletionSource<Variable[]>();

        model.Commands.Add(() =>
        {
            try
            {
                tcs.SetResult(GetVariablesOnEngine(model, variablesReference));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.Result;
    }

    public DapThread[] GetThreads(NativeDebuggerModel model)
    {
        var tcs = new TaskCompletionSource<DapThread[]>();

        model.Commands.Add(() =>
        {
            try
            {
                tcs.SetResult(GetThreadsOnEngine(model));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.Result;
    }

    public int GetStoppedThreadId(NativeDebuggerModel model)
    {
        var tcs = new TaskCompletionSource<int>();
        model.Commands.Add(() =>
        {
            try
            {
                model.SysObjects.GetEventThread(out var id);
                tcs.SetResult((int)id);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task.Result;
    }

    public void Terminate(NativeDebuggerModel model)
    {
        model.Terminated = true;
        if (!model.TargetExited)
        {
            try { model.Client.TerminateProcesses(); } catch { }
        }
        try { model.Client.EndSession(DebugEnd.ActiveTerminate); } catch { }
        model.Commands.Add(() => { }); // Wake the engine thread
    }

    public void Detach(NativeDebuggerModel model)
    {
        model.Terminated = true;
        try { model.Client.DetachProcesses(); } catch { }
        try { model.Client.EndSession(DebugEnd.ActiveDetach); } catch { }
        model.Commands.Add(() => { }); // Wake the engine thread
    }

    // ── Private ─────────────────────────────────────────

    private void CreateEngine(NativeDebuggerModel model)
    {
        var iid = typeof(IDebugClient).GUID;
        Check(DbgEngNative.DebugCreate(ref iid, out var obj));
        model.Client = (IDebugClient)obj;
        model.Control = (IDebugControl)obj;
        model.Symbols = (IDebugSymbols)obj;
        model.SysObjects = (IDebugSystemObjects)obj;

        model.Callbacks = new EventCallbacks();
        model.Callbacks.OnBreakpoint += bp => OnBreakpoint(model, bp);
        model.Callbacks.OnExitProcess += code => OnExitProcess(model, code);
        model.Callbacks.OnCreateProcess += name =>
        {
            _server.SendEvent(_transport, "output", new OutputEventBody
            {
                Category = "console",
                Output = $"[mixdbg] Process created: {name}\n",
            });
        };
        model.Callbacks.OnLoadModule += (mod, img) =>
        {
            // Silent — could log if needed
        };

        Check(model.Client.SetEventCallbacks(model.Callbacks));

        // Enable source-line loading
        model.Symbols.SetSymbolOptions(SymOpt.LoadLines | SymOpt.DeferredLoads | SymOpt.UndName);

        if (model.SymbolPath != null)
            model.Symbols.SetSymbolPath(model.SymbolPath);
    }

    private void StartEngineThread(NativeDebuggerModel model)
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

            if (model.IsAttach)
            {
                _log.LogInfo(_logStore, $"Attach: pid={model.AttachPid}");
                Check(model.Client.AttachProcess(0, model.AttachPid, DebugAttach.Default));
            }
            else
            {
                if (model.LaunchCwd != null)
                    model.Symbols.SetSourcePath(model.LaunchCwd);

                _log.LogInfo(_logStore, $"Launch: CreateProcess({model.LaunchProgram})");
                Check(model.Client.CreateProcess(
                    0,
                    model.LaunchProgram!,
                    CreateProcessFlags.DebugOnlyThisProcess | CreateProcessFlags.CreateNewConsole));
                _log.LogInfo(_logStore, "Launch: CreateProcess succeeded");
            }

            // Signal the main thread that init is done.
            model.EngineReady.Set();

            while (!model.Terminated)
            {
                _log.LogInfo(_logStore, "WaitForEvent...");
                int hr = model.Control.WaitForEvent(0, 0xFFFFFFFF); // INFINITE
                _log.LogInfo(_logStore, $"WaitForEvent returned hr=0x{hr:X8}");
                if (hr < 0)
                {
                    _log.LogInfo(_logStore, $"WaitForEvent failed, terminated={model.Terminated} exited={model.TargetExited}");
                    break;
                }

                // Target is now stopped.
                model.Stopped.Set();

                if (model.TargetExited)
                {
                    _log.LogInfo(_logStore, "Target exited, sending terminated event");
                    _server.SendEvent(_transport, "terminated", new TerminatedEventBody());
                    break;
                }

                // Get last event info for logging
                IntPtr descBuf = Marshal.AllocHGlobal(256);
                model.Control.GetLastEventInformation(
                    out var evtType, out var evtPid, out var evtTid,
                    IntPtr.Zero, 0, out _,
                    descBuf, 256, out _);
                var desc = Marshal.PtrToStringAnsi(descBuf) ?? "";
                Marshal.FreeHGlobal(descBuf);
                _log.LogInfo(_logStore, $"Event: type=0x{evtType:X} pid={evtPid} tid={evtTid} desc=\"{desc}\"");
                _log.LogInfo(_logStore, $"State: configDone={model.ConfigDone} hitUserBp={model.HitUserBreakpoint} stepping={model.Stepping} pause={model.PauseRequested}");

                if (!model.ConfigDone)
                {
                    _log.LogInfo(_logStore, "Pre-configDone: processing commands until resume");
                    ProcessCommandsUntilResume(model);
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
                    model.SysObjects.GetEventThread(out var threadId);
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
                    model.Control.SetExecutionStatus(DebugStatus.Go);
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

            model.Control.GetExecutionStatus(out var status);
            _log.LogInfo(_logStore, $"ProcessCommandsUntilResume: execStatus={status}");
            if (status != DebugStatus.Break
                && status != DebugStatus.NoDebuggee)
            {
                _log.LogInfo(_logStore, "ProcessCommandsUntilResume: resuming");
                model.Stopped.Reset();
                break;
            }
        }
    }

    private static void QueueStep(NativeDebuggerModel model, uint stepKind)
    {
        model.Stepping = true;
        model.Variables.Clear();
        model.Commands.Add(() =>
        {
            Check(model.Control.SetExecutionStatus(stepKind));
        });
    }

    private void OnBreakpoint(NativeDebuggerModel model, IDebugBreakpoint bp)
    {
        bp.GetId(out var id);
        model.LastHitBpId = id;
        model.HitUserBreakpoint = model.UserBreakpointIds.Contains(id);
        _log.LogInfo(_logStore, $"OnBreakpoint: id={id} isUser={model.HitUserBreakpoint} (tracked: [{string.Join(",", model.UserBreakpointIds)}])");

        // Send verified update so nvim-dap clears the "rejected" marker.
        if (model.HitUserBreakpoint)
        {
            // Find the source:line for this breakpoint ID
            var entry = model.BreakpointIds.FirstOrDefault(kv => kv.Value == id);
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
                        Id = (int)id,
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

    private Breakpoint[] SetBreakpointsOnEngine(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested)
    {
        _log.LogInfo(_logStore, $"SetBreakpointsOnEngine: file={filePath} count={requested.Length}");
        foreach (var r in requested)
            _log.LogInfo(_logStore, $"  requested: line={r.Line}");

        // Managed files can't be debugged via dbgeng yet (M4).
        if (!_sourceFiles.IsNativeFile(filePath))
        {
            _log.LogInfo(_logStore, $"  Skipping non-native file: {filePath}");
            return requested.Select((bp, i) => new Breakpoint
            {
                Id = i,
                Verified = false,
                Line = bp.Line,
                Message = "Managed breakpoints not yet supported",
            }).ToArray();
        }

        // Remove old breakpoints for this file
        var keysToRemove = model.BreakpointIds.Keys
            .Where(k => k.StartsWith(filePath + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keysToRemove)
        {
            if (model.BreakpointIds.TryGetValue(key, out var oldId))
            {
                int hr = model.Control.GetBreakpointById(oldId, out var oldBp);
                if (hr >= 0)
                    model.Control.RemoveBreakpoint(oldBp);
                model.UserBreakpointIds.Remove(oldId);
                model.BreakpointIds.Remove(key);
            }
        }

        var results = new Breakpoint[requested.Length];
        for (int i = 0; i < requested.Length; i++)
        {
            var req = requested[i];
            var key = $"{filePath}:{req.Line}";

            int hr = model.Symbols.GetOffsetByLine((uint)req.Line, filePath, out var offset);
            _log.LogInfo(_logStore, $"  GetOffsetByLine({req.Line}, {filePath}) -> hr=0x{hr:X8} offset=0x{offset:X}");
            if (hr < 0)
            {
                // GetOffsetByLine failed — module probably not loaded yet.
                // Use deferred breakpoint via bu command instead.
                var buCmd = $"bu `{filePath}:{req.Line}`";
                _log.LogInfo(_logStore, $"  Trying deferred breakpoint: {buCmd}");
                int buHr = model.Control.Execute(DebugOutCtl.Ignore, buCmd, DebugExecute.Default);
                _log.LogInfo(_logStore, $"  bu result: hr=0x{buHr:X8}");

                if (buHr >= 0)
                {
                    // Get the ID of the breakpoint we just created
                    model.Control.GetNumberBreakpoints(out var bpCount);
                    uint deferredId = 0;
                    if (bpCount > 0)
                    {
                        model.Control.GetBreakpointByIndex(bpCount - 1, out var deferredBp);
                        if (deferredBp != null)
                        {
                            deferredBp.GetId(out deferredId);
                            model.BreakpointIds[key] = deferredId;
                            model.UserBreakpointIds.Add(deferredId);
                        }
                    }
                    _log.LogInfo(_logStore, $"  Deferred bp registered: id={deferredId}");
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

            hr = model.Control.AddBreakpoint(
                DebugBreakpointType.Code,
                0xFFFFFFFF, // DEBUG_ANY_ID
                out var bp);
            if (hr < 0)
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

            bp.SetOffset(offset);
            bp.AddFlags(DebugBreakpointFlag.Enabled);
            bp.GetId(out var bpId);

            model.BreakpointIds[key] = bpId;
            model.UserBreakpointIds.Add(bpId);

            // Resolve back to verify the actual line
            int actualLine = req.Line;
            IntPtr fileOut = Marshal.AllocHGlobal(512);
            if (model.Symbols.GetLineByOffset(offset, out var resolvedLine,
                fileOut, 512, out _, out _) >= 0)
            {
                actualLine = (int)resolvedLine;
            }
            Marshal.FreeHGlobal(fileOut);

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

    private StackFrame[] GetStackTraceOnEngine(NativeDebuggerModel model, int maxFrames)
    {
        if (maxFrames <= 0) maxFrames = 50;
        int frameSize = Marshal.SizeOf<DEBUG_STACK_FRAME>();
        _log.LogInfo(_logStore, $"DEBUG_STACK_FRAME size={frameSize}");
        IntPtr buf = Marshal.AllocHGlobal(frameSize * maxFrames);
        try
        {
        int hr = model.Control.GetStackTrace(0, 0, 0, buf, (uint)maxFrames, out var filled);
        _log.LogInfo(_logStore, $"GetStackTrace: hr=0x{hr:X8} filled={filled}");
        if (hr < 0) return [];

        var frames = new DEBUG_STACK_FRAME[filled];
        for (int i = 0; i < (int)filled; i++)
            frames[i] = Marshal.PtrToStructure<DEBUG_STACK_FRAME>(buf + i * frameSize);

        // Cache raw frames so GetScopes can SetScope by instruction offset.
        model.CachedStackFrames = frames;

        var result = new StackFrame[filled];
        IntPtr nameBuf = Marshal.AllocHGlobal(512);
        IntPtr fileBuf = Marshal.AllocHGlobal(512);

        for (int i = 0; i < filled; i++)
        {
            var f = frames[i];
            string name = $"0x{f.InstructionOffset:X}";
            Source? source = null;
            int line = 0;

            // Try to resolve function name
            int nameHr = model.Symbols.GetNameByOffset(f.InstructionOffset, nameBuf, 512,
                out _, out var displacement);
            if (nameHr >= 0)
            {
                var nameStr = Marshal.PtrToStringAnsi(nameBuf) ?? "";
                name = displacement > 0
                    ? $"{nameStr}+0x{displacement:x}"
                    : nameStr;
            }

            // Try to resolve source location
            int lineHr = model.Symbols.GetLineByOffset(f.InstructionOffset, out var srcLine,
                fileBuf, 512, out _, out _);
            if (lineHr >= 0)
            {
                line = (int)srcLine;
                var path = Marshal.PtrToStringAnsi(fileBuf) ?? "";
                source = new Source
                {
                    Name = Path.GetFileName(path),
                    Path = path,
                };
            }

            _log.LogInfo(_logStore, $"  Frame {i}: ip=0x{f.InstructionOffset:X} name={name} nameHr=0x{nameHr:X8} lineHr=0x{lineHr:X8} line={line}");

            result[i] = new StackFrame
            {
                Id = i + 1, // 1-based
                Name = name,
                Source = source,
                Line = line,
                Column = 0,
            };
        }
        Marshal.FreeHGlobal(nameBuf);
        Marshal.FreeHGlobal(fileBuf);
        return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private Scope[] GetScopesOnEngine(NativeDebuggerModel model, int frameId)
    {
        // Frame IDs are 1-based (from GetStackTraceOnEngine).
        int index = frameId - 1;
        if (index < 0 || index >= model.CachedStackFrames.Length)
        {
            _log.LogWarning(_logStore, $"GetScopes: invalid frameId={frameId}");
            return [];
        }

        var frame = model.CachedStackFrames[index];

        // Pin the DEBUG_STACK_FRAME and pass to SetScope.
        int frameSize = Marshal.SizeOf<DEBUG_STACK_FRAME>();
        IntPtr frameBuf = Marshal.AllocHGlobal(frameSize);
        Marshal.StructureToPtr(frame, frameBuf, false);
        int hr = model.Symbols.SetScope(frame.InstructionOffset, frameBuf, IntPtr.Zero, 0);
        Marshal.FreeHGlobal(frameBuf);
        _log.LogInfo(_logStore, $"SetScope(ip=0x{frame.InstructionOffset:X}) -> hr=0x{hr:X8}");

        // Get locals symbol group.
        hr = model.Symbols.GetScopeSymbolGroup(
            DebugScopeGroup.All, IntPtr.Zero, out var group);
        _log.LogInfo(_logStore, $"GetScopeSymbolGroup(ALL) -> hr=0x{hr:X8}");
        if (hr < 0)
            return [];

        group.GetNumberSymbols(out var count);
        _log.LogInfo(_logStore, $"Symbol group has {count} symbols");
        if (count == 0)
            return [];

        int localsRef = model.Variables.Allocate(group, 0, count);

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

    private Variable[] GetVariablesOnEngine(NativeDebuggerModel model, int variablesReference)
    {
        var container = model.Variables.Get(variablesReference);
        if (container == null)
        {
            _log.LogWarning(_logStore, $"GetVariables: unknown ref={variablesReference}");
            return [];
        }

        var group = container.Group;
        var start = container.StartIndex;
        var count = container.Count;
        _log.LogInfo(_logStore, $"GetVariables: ref={variablesReference} start={start} count={count}");

        // Read parameters for all symbols in the range to check SubElements.
        int paramSize = Marshal.SizeOf<DEBUG_SYMBOL_PARAMETERS>();
        IntPtr paramsBuf = Marshal.AllocHGlobal(paramSize * (int)count);
        int hr = group.GetSymbolParameters(start, count, paramsBuf);
        _log.LogInfo(_logStore, $"GetSymbolParameters: hr=0x{hr:X8}");
        var paramArray = new DEBUG_SYMBOL_PARAMETERS[count];
        if (hr >= 0)
        {
            for (int i = 0; i < (int)count; i++)
                paramArray[i] = Marshal.PtrToStructure<DEBUG_SYMBOL_PARAMETERS>(
                    paramsBuf + i * paramSize);
        }
        Marshal.FreeHGlobal(paramsBuf);

        IntPtr nameBuf = Marshal.AllocHGlobal(512);
        IntPtr typeBuf = Marshal.AllocHGlobal(512);
        IntPtr valBuf = Marshal.AllocHGlobal(1024);

        var result = new Variable[count];
        for (uint i = 0; i < count; i++)
        {
            uint idx = start + i;

            string name = $"[{idx}]";
            if (group.GetSymbolName(idx, nameBuf, 512, out _) >= 0)
                name = Marshal.PtrToStringAnsi(nameBuf) ?? name;

            string? type = null;
            if (group.GetSymbolTypeName(idx, typeBuf, 512, out _) >= 0)
                type = Marshal.PtrToStringAnsi(typeBuf);

            string value = "";
            if (group.GetSymbolValueText(idx, valBuf, 1024, out _) >= 0)
                value = Marshal.PtrToStringAnsi(valBuf) ?? "";

            int childRef = 0;
            if (hr >= 0 && paramArray[i].SubElements > 0)
            {
                // Expand the symbol so its children appear in the group.
                int expHr = group.ExpandSymbol(idx, true);
                if (expHr >= 0)
                {
                    // After expansion, children are inserted right after this symbol.
                    // Re-read the total count to find the new children.
                    group.GetNumberSymbols(out var newTotal);
                    uint childCount = paramArray[i].SubElements;
                    uint childStart = idx + 1;

                    // Clamp to avoid overrun.
                    if (childStart + childCount > newTotal)
                        childCount = newTotal - childStart;

                    if (childCount > 0)
                        childRef = model.Variables.Allocate(group, childStart, childCount);
                }
                _log.LogInfo(_logStore,
                    $"  Expand {name}: hr=0x{expHr:X8} subElements={paramArray[i].SubElements} childRef={childRef}");
            }

            _log.LogInfo(_logStore, $"  Var[{idx}]: name=\"{name}\" type=\"{type}\" value=\"{value}\" childRef={childRef}");

            result[i] = new Variable
            {
                Name = name,
                Value = value,
                Type = type,
                VariablesReference = childRef,
            };
        }

        Marshal.FreeHGlobal(nameBuf);
        Marshal.FreeHGlobal(typeBuf);
        Marshal.FreeHGlobal(valBuf);

        return result;
    }

    private static DapThread[] GetThreadsOnEngine(NativeDebuggerModel model)
    {
        int hr = model.SysObjects.GetNumberThreads(out var count);
        if (hr < 0 || count == 0)
            return [new DapThread { Id = 1, Name = "Main Thread" }];

        var ids = new uint[count];
        var sysIds = new uint[count];
        model.SysObjects.GetThreadIdsByIndex(0, count, ids, sysIds);

        var threads = new DapThread[count];
        for (int i = 0; i < count; i++)
        {
            threads[i] = new DapThread
            {
                Id = (int)ids[i],
                Name = $"Thread {sysIds[i]} (dbg:{ids[i]})",
            };
        }
        return threads;
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }
}
