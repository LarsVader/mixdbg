using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MixDbg.Dap;
using MixDbg.Engine.DbgEng;

namespace MixDbg.Engine;

public static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "mixdbg.log");
    private static readonly object Lock = new();

    public static void Write(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        lock (Lock)
        {
            File.AppendAllText(Path, line + Environment.NewLine);
        }
    }
}

/// <summary>
/// Wraps dbgeng for native debugging. Runs WaitForEvent on a
/// dedicated engine thread. DAP handlers queue commands that
/// execute when the target is stopped.
/// </summary>
public sealed class NativeDebugger : IDisposable
{
    private IDebugClient _client = null!;
    private IDebugControl _control = null!;
    private IDebugSymbols _symbols = null!;
    private IDebugSystemObjects _sysObjects = null!;
    private EventCallbacks _callbacks = null!;
    private Thread? _engineThread;
    private volatile bool _terminated;
    private volatile bool _targetExited;
    private volatile bool _configDone;
    private volatile bool _stepping;
    private volatile bool _pauseRequested;

    // Set of dbgeng breakpoint IDs we created (user breakpoints).
    private readonly HashSet<uint> _userBreakpointIds = new();

    // The breakpoint ID that was just hit (set by callback).
    private uint _lastHitBpId;
    private volatile bool _hitUserBreakpoint;

    // Command queue: main thread queues, engine thread executes.
    private readonly BlockingCollection<Action> _commands = new();

    // Signaled when the target is stopped and ready for commands.
    private readonly ManualResetEventSlim _stopped = new(false);

    // DAP server for sending events.
    private readonly DapServer _server;

    // Track breakpoints: DAP source:line -> dbgeng breakpoint id
    private readonly Dictionary<string, uint> _breakpointIds = new();
    private int _nextBpId;

    public NativeDebugger(DapServer server)
    {
        _server = server;
    }

    public bool IsTargetStopped => _stopped.IsSet;

    // SignalConfigDone removed — _configDone is set on the
    // engine thread when the first Continue is processed.

    // Saved launch/attach parameters — actual work happens on engine thread.
    private string? _launchProgram;
    private string? _launchCwd;
    private uint _attachPid;
    private string? _symbolPath;
    private bool _isAttach;
    private readonly ManualResetEventSlim _engineReady = new(false);
    private Exception? _engineInitError;

    /// <summary>
    /// Creates the dbgeng client and attaches to a process.
    /// Blocks until the engine thread has initialized.
    /// </summary>
    public void Attach(uint pid, string? symbolPath)
    {
        _isAttach = true;
        _attachPid = pid;
        _symbolPath = symbolPath;
        StartEngineThread();
        _engineReady.Wait();
        if (_engineInitError != null)
            throw _engineInitError;
    }

    /// <summary>
    /// Creates the dbgeng client and launches a process.
    /// Blocks until the engine thread has initialized.
    /// </summary>
    public void Launch(string program, string? cwd, string? symbolPath)
    {
        _isAttach = false;
        _launchProgram = program;
        _launchCwd = cwd;
        _symbolPath = symbolPath;
        StartEngineThread();
        _engineReady.Wait();
        if (_engineInitError != null)
            throw _engineInitError;
    }

    /// <summary>
    /// Continues execution (all threads).
    /// </summary>
    public void Continue()
    {
        Log.Write("Continue queued");
        _commands.Add(() =>
        {
            Log.Write("Continue executing: SetExecutionStatus(GO)");
            _configDone = true;
            Check(_control.SetExecutionStatus(DebugStatus.Go));
        });
    }

    /// <summary>
    /// Requests the target to break (pause).
    /// Thread-safe — can be called while target is running.
    /// </summary>
    public void Break()
    {
        _pauseRequested = true;
        _control.SetInterrupt(0); // DEBUG_INTERRUPT_ACTIVE
    }

    /// <summary>
    /// Step over one source line.
    /// </summary>
    public void StepOver()
    {
        QueueStep(DebugStatus.StepOver);
    }

    /// <summary>
    /// Step into the next call.
    /// </summary>
    public void StepInto()
    {
        QueueStep(DebugStatus.StepInto);
    }

    /// <summary>
    /// Step out of the current function.
    /// </summary>
    public void StepOut()
    {
        // dbgeng doesn't have a direct step-out status.
        // Use the "gu" (go up) command instead.
        _commands.Add(() =>
        {
            _control.Execute(DebugOutCtl.Ignore, "gu", DebugExecute.NotLogged);
        });
    }

    /// <summary>
    /// Sets breakpoints for a source file. Returns the breakpoint results.
    /// </summary>
    public Breakpoint[] SetBreakpoints(string filePath, SourceBreakpoint[] requested)
    {
        // This needs to run on the engine thread for symbol resolution.
        // If target is stopped, execute directly. Otherwise, queue.
        var tcs = new TaskCompletionSource<Breakpoint[]>();

        _commands.Add(() =>
        {
            try
            {
                tcs.SetResult(SetBreakpointsOnEngine(filePath, requested));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        // Wait for engine thread to process
        return tcs.Task.Result;
    }

    /// <summary>
    /// Gets the current call stack.
    /// </summary>
    public StackFrame[] GetStackTrace(int maxFrames)
    {
        var tcs = new TaskCompletionSource<StackFrame[]>();

        _commands.Add(() =>
        {
            try
            {
                tcs.SetResult(GetStackTraceOnEngine(maxFrames));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.Result;
    }

    /// <summary>
    /// Gets all debugger threads.
    /// </summary>
    public DapThread[] GetThreads()
    {
        var tcs = new TaskCompletionSource<DapThread[]>();

        _commands.Add(() =>
        {
            try
            {
                tcs.SetResult(GetThreadsOnEngine());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task.Result;
    }

    /// <summary>
    /// Gets the engine thread ID that hit the last event.
    /// </summary>
    public int GetStoppedThreadId()
    {
        var tcs = new TaskCompletionSource<int>();
        _commands.Add(() =>
        {
            try
            {
                _sysObjects.GetEventThread(out var id);
                tcs.SetResult((int)id);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task.Result;
    }

    public void Terminate()
    {
        _terminated = true;
        if (!_targetExited)
        {
            try { _client.TerminateProcesses(); } catch { }
        }
        try { _client.EndSession(DebugEnd.ActiveTerminate); } catch { }
        _commands.Add(() => { }); // Wake the engine thread
    }

    public void Detach()
    {
        _terminated = true;
        try { _client.DetachProcesses(); } catch { }
        try { _client.EndSession(DebugEnd.ActiveDetach); } catch { }
        _commands.Add(() => { }); // Wake the engine thread
    }

    public void Dispose()
    {
        _terminated = true;
        _commands.CompleteAdding();
        _engineThread?.Join(3000);
        _commands.Dispose();
        _stopped.Dispose();
        _engineReady.Dispose();
    }

    // ── Private ─────────────────────────────────────────

    private void CreateEngine(string? symbolPath)
    {
        var iid = typeof(IDebugClient).GUID;
        Check(DbgEngNative.DebugCreate(ref iid, out var obj));
        _client = (IDebugClient)obj;
        _control = (IDebugControl)obj;
        _symbols = (IDebugSymbols)obj;
        _sysObjects = (IDebugSystemObjects)obj;

        _callbacks = new EventCallbacks();
        _callbacks.OnBreakpoint += OnBreakpoint;
        _callbacks.OnExitProcess += OnExitProcess;
        _callbacks.OnCreateProcess += name =>
        {
            _server.SendEvent("output", new OutputEventBody
            {
                Category = "console",
                Output = $"[mixdbg] Process created: {name}\n",
            });
        };
        _callbacks.OnLoadModule += (mod, img) =>
        {
            // Silent — could log if needed
        };

        Check(_client.SetEventCallbacks(_callbacks));

        // Enable source-line loading
        _symbols.SetSymbolOptions(SymOpt.LoadLines | SymOpt.DeferredLoads | SymOpt.UndName);

        if (symbolPath != null)
            _symbols.SetSymbolPath(symbolPath);
    }

    private void StartEngineThread()
    {
        _engineThread = new Thread(EngineLoop)
        {
            Name = "dbgeng-engine",
            IsBackground = true,
        };
        _engineThread.Start();
    }

    private void EngineLoop()
    {
        Log.Write("EngineLoop started — initializing dbgeng on engine thread");
        try
        {
            // All dbgeng COM calls must happen on this thread.
            CreateEngine(_symbolPath);

            if (_isAttach)
            {
                Log.Write($"Attach: pid={_attachPid}");
                Check(_client.AttachProcess(0, _attachPid, DebugAttach.Default));
            }
            else
            {
                if (_launchCwd != null)
                    _symbols.SetSourcePath(_launchCwd);

                Log.Write($"Launch: CreateProcess({_launchProgram})");
                Check(_client.CreateProcess(
                    0,
                    _launchProgram!,
                    CreateProcessFlags.DebugOnlyThisProcess | CreateProcessFlags.CreateNewConsole));
                Log.Write("Launch: CreateProcess succeeded");
            }

            // Signal the main thread that init is done.
            _engineReady.Set();

            while (!_terminated)
            {
                Log.Write("WaitForEvent...");
                int hr = _control.WaitForEvent(0, 0xFFFFFFFF); // INFINITE
                Log.Write($"WaitForEvent returned hr=0x{hr:X8}");
                if (hr < 0)
                {
                    Log.Write($"WaitForEvent failed, terminated={_terminated} exited={_targetExited}");
                    break;
                }

                // Target is now stopped.
                _stopped.Set();

                if (_targetExited)
                {
                    Log.Write("Target exited, sending terminated event");
                    _server.SendEvent("terminated", new TerminatedEventBody());
                    break;
                }

                // Get last event info for logging
                IntPtr descBuf = Marshal.AllocHGlobal(256);
                _control.GetLastEventInformation(
                    out var evtType, out var evtPid, out var evtTid,
                    IntPtr.Zero, 0, out _,
                    descBuf, 256, out _);
                var desc = Marshal.PtrToStringAnsi(descBuf) ?? "";
                Marshal.FreeHGlobal(descBuf);
                Log.Write($"Event: type=0x{evtType:X} pid={evtPid} tid={evtTid} desc=\"{desc}\"");
                Log.Write($"State: configDone={_configDone} hitUserBp={_hitUserBreakpoint} stepping={_stepping} pause={_pauseRequested}");

                if (!_configDone)
                {
                    Log.Write("Pre-configDone: processing commands until resume");
                    ProcessCommandsUntilResume();
                    continue;
                }

                // After configurationDone: determine stop reason.
                string? reason = null;

                if (_hitUserBreakpoint)
                {
                    _hitUserBreakpoint = false;
                    reason = "breakpoint";
                }
                else if (_stepping)
                {
                    _stepping = false;
                    reason = "step";
                }
                else if (_pauseRequested)
                {
                    _pauseRequested = false;
                    reason = "pause";
                }

                if (reason != null)
                {
                    _sysObjects.GetEventThread(out var threadId);
                    Log.Write($"User stop: reason={reason} threadId={threadId}");
                    _server.SendEvent("stopped", new StoppedEventBody
                    {
                        Reason = reason,
                        ThreadId = (int)threadId,
                        AllThreadsStopped = true,
                    });
                    ProcessCommandsUntilResume();
                }
                else
                {
                    Log.Write("System stop — auto-continuing");
                    _stopped.Reset();
                    _control.SetExecutionStatus(DebugStatus.Go);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"EngineLoop EXCEPTION: {ex}");
            // If init failed, unblock the main thread.
            _engineInitError = ex;
            _engineReady.Set();
            _server.SendEvent("output", new OutputEventBody
            {
                Category = "stderr",
                Output = $"[mixdbg] Engine error: {ex.Message}\n",
            });
            _server.SendEvent("terminated", new TerminatedEventBody());
        }
    }

    private void ProcessCommandsUntilResume()
    {
        Log.Write("ProcessCommandsUntilResume: waiting for commands");
        while (!_terminated)
        {
            Action cmd;
            try
            {
                cmd = _commands.Take();
            }
            catch (InvalidOperationException)
            {
                Log.Write("ProcessCommandsUntilResume: collection completed");
                break;
            }

            Log.Write("ProcessCommandsUntilResume: executing command");
            cmd();

            _control.GetExecutionStatus(out var status);
            Log.Write($"ProcessCommandsUntilResume: execStatus={status}");
            if (status != DebugStatus.Break
                && status != DebugStatus.NoDebuggee)
            {
                Log.Write("ProcessCommandsUntilResume: resuming");
                _stopped.Reset();
                break;
            }
        }
    }

    private void QueueStep(uint stepKind)
    {
        _stepping = true;
        _commands.Add(() =>
        {
            Check(_control.SetExecutionStatus(stepKind));
        });
    }

    private void OnBreakpoint(IDebugBreakpoint bp)
    {
        bp.GetId(out var id);
        _lastHitBpId = id;
        _hitUserBreakpoint = _userBreakpointIds.Contains(id);
        Log.Write($"OnBreakpoint: id={id} isUser={_hitUserBreakpoint} (tracked: [{string.Join(",", _userBreakpointIds)}])");

        // Send verified update so nvim-dap clears the "rejected" marker.
        if (_hitUserBreakpoint)
        {
            // Find the source:line for this breakpoint ID
            var entry = _breakpointIds.FirstOrDefault(kv => kv.Value == id);
            if (entry.Key != null)
            {
                var parts = entry.Key.Split(':', 2);
                var path = parts[0];
                var line = int.TryParse(parts[1], out var l) ? l : 0;
                _server.SendEvent("breakpoint", new BreakpointEventBody
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

    private void OnExitProcess(uint exitCode)
    {
        _targetExited = true;
        _server.SendEvent("output", new OutputEventBody
        {
            Category = "console",
            Output = $"[mixdbg] Process exited with code {exitCode}\n",
        });
    }

    private static bool IsNativeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".cpp" and not ".c" and not ".cc" and not ".cxx"
            and not ".h" and not ".hpp")
            return false;

        // Check if the file's directory has a vcxproj with CLRSupport
        // (C++/CLI) — those compile to IL, not debuggable via dbgeng.
        var dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            try
            {
                foreach (var vcx in Directory.GetFiles(dir, "*.vcxproj"))
                {
                    var text = File.ReadAllText(vcx);
                    if (text.Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            catch { /* ignore IO errors */ }
        }
        return true;
    }

    private Breakpoint[] SetBreakpointsOnEngine(string filePath, SourceBreakpoint[] requested)
    {
        Log.Write($"SetBreakpointsOnEngine: file={filePath} count={requested.Length}");
        foreach (var r in requested)
            Log.Write($"  requested: line={r.Line}");

        // Managed files can't be debugged via dbgeng yet (M4).
        if (!IsNativeFile(filePath))
        {
            Log.Write($"  Skipping non-native file: {filePath}");
            return requested.Select((bp, i) => new Breakpoint
            {
                Id = i,
                Verified = false,
                Line = bp.Line,
                Message = "Managed breakpoints not yet supported",
            }).ToArray();
        }

        // Remove old breakpoints for this file
        var keysToRemove = _breakpointIds.Keys
            .Where(k => k.StartsWith(filePath + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keysToRemove)
        {
            if (_breakpointIds.TryGetValue(key, out var oldId))
            {
                int hr = _control.GetBreakpointById(oldId, out var oldBp);
                if (hr >= 0)
                    _control.RemoveBreakpoint(oldBp);
                _userBreakpointIds.Remove(oldId);
                _breakpointIds.Remove(key);
            }
        }

        var results = new Breakpoint[requested.Length];
        for (int i = 0; i < requested.Length; i++)
        {
            var req = requested[i];
            var key = $"{filePath}:{req.Line}";

            int hr = _symbols.GetOffsetByLine((uint)req.Line, filePath, out var offset);
            Log.Write($"  GetOffsetByLine({req.Line}, {filePath}) -> hr=0x{hr:X8} offset=0x{offset:X}");
            if (hr < 0)
            {
                // GetOffsetByLine failed — module probably not loaded yet.
                // Use deferred breakpoint via bu command instead.
                var fileName = Path.GetFileName(filePath);
                var moduleName = Path.GetFileNameWithoutExtension(filePath);
                var buCmd = $"bu `{filePath}:{req.Line}`";
                Log.Write($"  Trying deferred breakpoint: {buCmd}");
                int buHr = _control.Execute(DebugOutCtl.Ignore, buCmd, DebugExecute.Default);
                Log.Write($"  bu result: hr=0x{buHr:X8}");

                if (buHr >= 0)
                {
                    // Get the ID of the breakpoint we just created
                    _control.GetNumberBreakpoints(out var bpCount);
                    uint deferredId = 0;
                    if (bpCount > 0)
                    {
                        _control.GetBreakpointByIndex(bpCount - 1, out var deferredBp);
                        if (deferredBp != null)
                        {
                            deferredBp.GetId(out deferredId);
                            _breakpointIds[key] = deferredId;
                            _userBreakpointIds.Add(deferredId);
                        }
                    }
                    Log.Write($"  Deferred bp registered: id={deferredId}");
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
                        Id = ++_nextBpId,
                        Verified = false,
                        Line = req.Line,
                        Message = "Could not resolve source line",
                    };
                }
                continue;
            }

            hr = _control.AddBreakpoint(
                DebugBreakpointType.Code,
                0xFFFFFFFF, // DEBUG_ANY_ID
                out var bp);
            if (hr < 0)
            {
                results[i] = new Breakpoint
                {
                    Id = ++_nextBpId,
                    Verified = false,
                    Line = req.Line,
                    Message = "Failed to create breakpoint",
                };
                continue;
            }

            bp.SetOffset(offset);
            bp.AddFlags(DebugBreakpointFlag.Enabled);
            bp.GetId(out var bpId);

            _breakpointIds[key] = bpId;
            _userBreakpointIds.Add(bpId);

            // Resolve back to verify the actual line
            int actualLine = req.Line;
            IntPtr fileOut = Marshal.AllocHGlobal(512);
            if (_symbols.GetLineByOffset(offset, out var resolvedLine,
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

    private StackFrame[] GetStackTraceOnEngine(int maxFrames)
    {
        if (maxFrames <= 0) maxFrames = 50;
        int frameSize = Marshal.SizeOf<DEBUG_STACK_FRAME>();
        Log.Write($"DEBUG_STACK_FRAME size={frameSize}");
        IntPtr buf = Marshal.AllocHGlobal(frameSize * maxFrames);
        try
        {
        int hr = _control.GetStackTrace(0, 0, 0, buf, (uint)maxFrames, out var filled);
        Log.Write($"GetStackTrace: hr=0x{hr:X8} filled={filled}");
        if (hr < 0) return [];

        var frames = new DEBUG_STACK_FRAME[filled];
        for (int i = 0; i < (int)filled; i++)
            frames[i] = Marshal.PtrToStructure<DEBUG_STACK_FRAME>(buf + i * frameSize);

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
            int nameHr = _symbols.GetNameByOffset(f.InstructionOffset, nameBuf, 512,
                out _, out var displacement);
            if (nameHr >= 0)
            {
                var nameStr = Marshal.PtrToStringAnsi(nameBuf) ?? "";
                name = displacement > 0
                    ? $"{nameStr}+0x{displacement:x}"
                    : nameStr;
            }

            // Try to resolve source location
            int lineHr = _symbols.GetLineByOffset(f.InstructionOffset, out var srcLine,
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

            Log.Write($"  Frame {i}: ip=0x{f.InstructionOffset:X} name={name} nameHr=0x{nameHr:X8} lineHr=0x{lineHr:X8} line={line}");

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

    private DapThread[] GetThreadsOnEngine()
    {
        int hr = _sysObjects.GetNumberThreads(out var count);
        if (hr < 0 || count == 0)
            return [new DapThread { Id = 1, Name = "Main Thread" }];

        var ids = new uint[count];
        var sysIds = new uint[count];
        _sysObjects.GetThreadIdsByIndex(0, count, ids, sysIds);

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
