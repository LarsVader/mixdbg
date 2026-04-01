using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MixDbg.Dap;
using MixDbg.Engine.DbgEng;

namespace MixDbg.Engine;

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

    /// <summary>
    /// Creates the dbgeng client and attaches to a process.
    /// </summary>
    public void Attach(uint pid, string? symbolPath)
    {
        CreateEngine(symbolPath);
        Check(_client.AttachProcess(0, pid, DebugAttach.Default));
        StartEngineThread();
    }

    /// <summary>
    /// Creates the dbgeng client and launches a process.
    /// </summary>
    public void Launch(string program, string? cwd, string? symbolPath)
    {
        CreateEngine(symbolPath);

        // Set source path to the program's directory for source-level debugging
        if (cwd != null)
            _symbols.SetSourcePath(cwd);

        Check(_client.CreateProcess(
            0,
            program,
            CreateProcessFlags.DebugOnlyThisProcess | CreateProcessFlags.CreateNewConsole));

        StartEngineThread();
    }

    /// <summary>
    /// Continues execution (all threads).
    /// </summary>
    public void Continue()
    {
        _commands.Add(() =>
        {
            // Sentinel: the engine loop checks this and enters WaitForEvent
        });
    }

    /// <summary>
    /// Requests the target to break (pause).
    /// Thread-safe — can be called while target is running.
    /// </summary>
    public void Break()
    {
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
        try
        {
            while (!_terminated)
            {
                // Wait for a debug event (target running)
                int hr = _control.WaitForEvent(0, 0xFFFFFFFF); // INFINITE
                if (hr < 0)
                {
                    if (_terminated || _targetExited) break;
                    // E_UNEXPECTED when target exits
                    break;
                }

                // Target is now stopped.
                _stopped.Set();

                if (_targetExited)
                {
                    _server.SendEvent("terminated", new TerminatedEventBody());
                    break;
                }

                // Determine why we stopped
                _sysObjects.GetEventThread(out var threadId);
                _server.SendEvent("stopped", new StoppedEventBody
                {
                    Reason = "breakpoint",
                    ThreadId = (int)threadId,
                    AllThreadsStopped = true,
                });

                // Process commands until a continue/step is issued
                var shouldResume = false;
                while (!_terminated && !shouldResume)
                {
                    Action cmd;
                    try
                    {
                        cmd = _commands.Take();
                    }
                    catch (InvalidOperationException)
                    {
                        break; // Collection completed
                    }

                    cmd();

                    // Check if execution status changed (continue/step was issued)
                    _control.GetExecutionStatus(out var status);
                    if (status != DebugStatus.Break && status != DebugStatus.NoDebuggee)
                    {
                        shouldResume = true;
                        _stopped.Reset();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _server.SendEvent("output", new OutputEventBody
            {
                Category = "stderr",
                Output = $"[mixdbg] Engine error: {ex.Message}\n",
            });
            _server.SendEvent("terminated", new TerminatedEventBody());
        }
    }

    private void QueueStep(uint stepKind)
    {
        _commands.Add(() =>
        {
            Check(_control.SetExecutionStatus(stepKind));
        });
    }

    private void OnBreakpoint(IDebugBreakpoint bp)
    {
        // Event handled in EngineLoop via the stopped event
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

    private Breakpoint[] SetBreakpointsOnEngine(string filePath, SourceBreakpoint[] requested)
    {
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
                _breakpointIds.Remove(key);
            }
        }

        var results = new Breakpoint[requested.Length];
        for (int i = 0; i < requested.Length; i++)
        {
            var req = requested[i];
            var key = $"{filePath}:{req.Line}";

            int hr = _symbols.GetOffsetByLine((uint)req.Line, filePath, out var offset);
            if (hr < 0)
            {
                results[i] = new Breakpoint
                {
                    Id = ++_nextBpId,
                    Verified = false,
                    Line = req.Line,
                    Message = "Could not resolve source line",
                };
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

            // Resolve back to verify the actual line
            int actualLine = req.Line;
            var fileOut = new StringBuilder(512);
            if (_symbols.GetLineByOffset(offset, out var resolvedLine,
                fileOut, 512, out _, out _) >= 0)
            {
                actualLine = (int)resolvedLine;
            }

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
        var frames = new DEBUG_STACK_FRAME[maxFrames];
        int hr = _control.GetStackTrace(0, 0, 0, frames, (uint)maxFrames, out var filled);
        if (hr < 0) return [];

        var result = new StackFrame[filled];
        var nameBuf = new StringBuilder(512);
        var fileBuf = new StringBuilder(512);

        for (int i = 0; i < filled; i++)
        {
            var f = frames[i];
            string name = $"0x{f.InstructionOffset:X}";
            Source? source = null;
            int line = 0;

            // Try to resolve function name
            nameBuf.Clear();
            if (_symbols.GetNameByOffset(f.InstructionOffset, nameBuf, 512,
                out _, out var displacement) >= 0)
            {
                name = displacement > 0
                    ? $"{nameBuf}+0x{displacement:x}"
                    : nameBuf.ToString();
            }

            // Try to resolve source location
            fileBuf.Clear();
            if (_symbols.GetLineByOffset(f.InstructionOffset, out var srcLine,
                fileBuf, 512, out _, out _) >= 0)
            {
                line = (int)srcLine;
                var path = fileBuf.ToString();
                source = new Source
                {
                    Name = Path.GetFileName(path),
                    Path = path,
                };
            }

            result[i] = new StackFrame
            {
                Id = i + 1, // 1-based
                Name = name,
                Source = source,
                Line = line,
                Column = 0,
            };
        }
        return result;
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
