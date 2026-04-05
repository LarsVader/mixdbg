using System.IO.Pipes;
using System.Runtime.InteropServices;
using MixDbg.Dap;
using MixDbg.Engine.DbgEng;
using MixDbg.Engine.CorDebug;
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
    ISourceFileService sourceFiles,
    IManagedDebugger managedDebugger) : INativeDebugger
{
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly ISourceFileService _sourceFiles = sourceFiles;
    private readonly IManagedDebugger _managedDebugger = managedDebugger;

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

    public void Launch(NativeDebuggerModel model, string program, string? cwd, string? symbolPath, string[]? args = null)
    {
        model.IsAttach = false;
        model.LaunchProgram = program;
        model.LaunchCwd = cwd;
        model.LaunchArgs = args;
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
            RemoveTransientManagedBreakpoints(model);
            // Re-enable enter/leave hooks in the profiler for the next method call.
            model.ProfilerRehookEvent?.Set();
            model.ConfigDone = true;
            _cachedStackTraceResult = null;
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
        model.DataSpaces = (IDebugDataSpaces)obj;
        model.Advanced = (IDebugAdvanced)obj;

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
        model.Callbacks.OnLoadModule += (mod, img, baseOffset) =>
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
                TryBindManagedBreakpointsOnModuleLoad(model);
            }
        };
        model.Callbacks.OnClrNotification += () =>
        {
            // When deferred managed breakpoints exist after configDone,
            // break so the engine loop can recreate the DAC and check for JIT.
            if (model.ConfigDone && model.DeferredManagedBreakpoints.Count > 0)
                model.Callbacks.ClrNotificationShouldBreak = true;
        };
        model.Callbacks.OnExceptionBreakpoint += addr =>
        {
            // Check if this EXCEPTION_BREAKPOINT is from a managed IL breakpoint.
            if (model.ManagedInitialized &&
                (model.ManagedBreakpointAddresses.Contains(addr) ||
                 (model.CorManagedBreakpoints.Count > 0 && !model.UserBreakpointIds.Contains(model.LastHitBpId))))
            {
                model.HitUserBreakpoint = true;
                _log.LogInfo(_logStore, $"Managed breakpoint hit at 0x{addr:X}");
            }
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

                // Disable tiered compilation and ReadyToRun so JIT produces stable
                // native addresses for hardware breakpoints on managed methods.
                Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", "0");
                Environment.SetEnvironmentVariable("DOTNET_ReadyToRun", "0");

                // Set up CLR profiler for real-time JIT notifications.
                SetupProfilerPipe(model);

                var cmdLine = model.LaunchProgram!;
                if (model.LaunchArgs is { Length: > 0 })
                    cmdLine += " " + string.Join(" ", model.LaunchArgs);
                _log.LogInfo(_logStore, $"Launch: CreateProcess({cmdLine})");
                Check(model.Client.CreateProcess(
                    0,
                    cmdLine,
                    CreateProcessFlags.DebugOnlyThisProcess | CreateProcessFlags.CreateNewConsole));
                _log.LogInfo(_logStore, "Launch: CreateProcess succeeded");

                // Start reading profiler notifications in the background.
                StartProfilerReader(model);
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

                    _log.LogInfo(_logStore, $"WaitForEvent failed hr=0x{hr:X8}, terminated={model.Terminated} exited={model.TargetExited}");
                    break;
                }

                // Target is now stopped.
                model.Stopped.Set();

                // Initialize managed debugging when CLR is first detected.
                if (model.ClrLoaded && !model.ManagedInitialized)
                    TryInitializeManaged(model);

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

                // Process JIT notifications from the CLR profiler pipe.
                // JIT notifications: set hardware BP at exact line on first JIT.
                // Works with both hooks-active and fallback modes.
                if (model.DeferredManagedBreakpoints.Count > 0 && !model.JitNotifications.IsEmpty)
                {
                    try
                    {
                        var jitResolved = _managedDebugger.HandleJitNotifications(model);
                        foreach (var bp in jitResolved)
                        {
                            _log.LogInfo(_logStore, $"Profiler JIT bp resolved: id={bp.Id} verified={bp.Verified}");
                            _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                            {
                                Reason = "changed",
                                Breakpoint = bp,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogInfo(_logStore, $"HandleJitNotifications failed: {ex.Message}");
                    }
                }

                // Fallback: try to resolve deferred managed breakpoints via DAC/XCLRData.
                // Skip when hooks are active — deferred BPs are consumed by ENTER notifications.
                if (!model.ProfilerHooksActive &&
                    model.ManagedInitialized && model.DeferredManagedBreakpoints.Count > 0)
                {
                    try
                    {
                        var resolved = _managedDebugger.TryResolveDeferredBreakpoints(model);
                        foreach (var bp in resolved)
                        {
                            _log.LogInfo(_logStore, $"Deferred managed bp resolved: id={bp.Id} verified={bp.Verified}");
                            _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                            {
                                Reason = "changed",
                                Breakpoint = bp,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogInfo(_logStore, $"TryResolveDeferredBreakpoints failed: {ex.Message}");
                    }
                }

                // After configurationDone: determine stop reason.
                string? reason = null;

                // With enter/leave hooks: ENTER freezes the thread before the method body.
                // Set a transient hardware BP at the exact breakpointed LINE, then resume.
                // The method runs and hits the BP at the correct source location.
                // With enter hooks: the profiler disabled hooks and is blocking.
                // Set transient hardware BP at the resolved line address, ACK,
                // and auto-continue. The method runs without hooks → BP fires at the line.
                // The profiler re-enables hooks after ACK.
                if (model.ProfilerHooksActive && model.PendingEnterBreakpoint)
                {
                    model.PendingEnterBreakpoint = false;
                    // Find the matching deferred BP and compute exact native address
                    // from the IL-to-native mapping (resolves breakpoint line → native offset).
                    var bpKey = $"{model.EnterBreakpointAssembly}:{model.EnterBreakpointToken:X8}";
                    foreach (var deferred in model.DeferredManagedBreakpoints)
                    {
                        if (deferred.MethodToken == model.EnterBreakpointToken &&
                            deferred.AssemblyName != null &&
                            deferred.AssemblyName.Equals(model.EnterBreakpointAssembly, StringComparison.OrdinalIgnoreCase))
                        {
                            // Use IL-to-native mapping to get the exact address for the BP line.
                            ulong addr = model.EnterBreakpointAddress; // fallback: body entry
                            if (model.JitMethodMappings.TryGetValue(bpKey, out var mapping))
                            {
                                addr = mapping.GetNativeAddress(deferred.ILOffset);
                                _log.LogInfo(_logStore,
                                    $"  ENTER: IL offset {deferred.ILOffset} -> native 0x{addr:X}");
                            }
                            _managedDebugger.SetTransientBreakpoint(model, addr, deferred.FilePath, deferred.Line);
                            _log.LogInfo(_logStore, $"  ENTER: transient hw BP at 0x{addr:X} for {deferred.FilePath}:{deferred.Line}");
                            break;
                        }
                    }
                    // ACK unblocks the profiler (hooks stay disabled). Method body runs
                    // through normal code path → hardware BP fires. Rehook watcher
                    // re-enables hooks when MixDbg signals on Continue.
                    model.ProfilerAckEvent?.Set();
                    model.Stopped.Reset();
                    model.Control.SetExecutionStatus(DebugStatus.Go);
                    continue;
                }

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
                    model.SysObjects.GetCurrentThreadId(out var threadId);
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

    /// <summary>
    /// Removes all transient managed hardware breakpoints. Called on Continue/Step
    /// to free debug registers. The profiler's enter hook will re-set them on the
    /// next call to each breakpointed method.
    /// </summary>
    private void RemoveTransientManagedBreakpoints(NativeDebuggerModel model)
    {
        // Only remove BPs when using enter/leave hooks (BPs are transient per-call).
        // With JIT-blocking fallback, BPs are permanent and must persist.
        if (!model.ProfilerHooksActive || model.ManagedBreakpointIds.Count == 0)
            return;

        var idsToRemove = new List<uint>(model.ManagedBreakpointIds);
        foreach (var hwBpId in idsToRemove)
        {
            int hr = model.Control.GetBreakpointById(hwBpId, out var hwBp);
            if (hr >= 0)
            {
                model.Control.RemoveBreakpoint(hwBp);
                _log.LogInfo(_logStore, $"Removed transient managed hw bp #{hwBpId}");
            }
            model.UserBreakpointIds.Remove(hwBpId);
        }
        model.ManagedBreakpointIds.Clear();
        model.ManagedBreakpointAddresses.Clear();

        // Clear the key→id mappings for managed breakpoints.
        var keysToRemove = model.BreakpointIds
            .Where(kv => idsToRemove.Contains(kv.Value))
            .Select(kv => kv.Key).ToList();
        foreach (var key in keysToRemove)
            model.BreakpointIds.Remove(key);
    }

    /// <summary>
    /// Creates a named pipe server and sets CLR profiler environment variables
    /// so the child process loads MixDbgProfiler.dll at startup.
    /// Must be called before <c>CreateProcess</c> on the engine thread.
    /// </summary>
    private void SetupProfilerPipe(NativeDebuggerModel model)
    {
        // Find MixDbgProfiler.dll next to MixDbg.exe.
        var exeDir = AppContext.BaseDirectory;
        var profilerPath = Path.Combine(exeDir, "MixDbgProfiler.dll");

        // Also check profiler/x64/Debug/ relative to the repo root (dev builds).
        // Exe is at src/bin/Debug/net10.0/win-x64/ — 5 levels up to repo root.
        if (!File.Exists(profilerPath))
        {
            var repoRoot = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", ".."));
            var devPath = Path.Combine(repoRoot, "profiler", "x64", "Debug", "MixDbgProfiler.dll");
            if (File.Exists(devPath))
                profilerPath = devPath;
        }

        if (!File.Exists(profilerPath))
        {
            _log.LogWarning(_logStore, $"MixDbgProfiler.dll not found at {profilerPath} — JIT notifications disabled");
            return;
        }

        // Create a named pipe for the profiler to connect to.
        var pipeName = $"MixDbgProfiler-{Environment.ProcessId}-{Guid.NewGuid():N}";
        model.ProfilerPipeName = pipeName;
        model.ProfilerPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1, // max connections
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            65536, // inBufferSize
            0);    // outBufferSize

        // Create a named event for ACK signaling. The profiler blocks on this event
        // after writing a JIT notification, ensuring the hardware breakpoint is set
        // before the method body executes (first-click breakpoints).
        var ackEventName = $"MixDbgProfilerAck-{pipeName}";
        model.ProfilerAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ackEventName);

        // Resolve exact method tokens from pending breakpoints so the profiler only
        // blocks for breakpointed methods (skips all other JITs including framework).
        string? watchTokens = null;
        if (model.ProfilerBreakpointHints.Count > 0)
        {
            var tokens = _managedDebugger.ResolveTokensFromBreakpoints(model.ProfilerBreakpointHints);
            if (tokens.Count > 0)
            {
                watchTokens = string.Join(",", tokens.Select(t => $"{t.Assembly}:{t.Token:X8}"));
                _log.LogInfo(_logStore, $"Profiler watch tokens: {watchTokens}");
            }
        }

        // Set CLR profiling env vars — child process inherits them.
        Environment.SetEnvironmentVariable("CORECLR_ENABLE_PROFILING", "1");
        Environment.SetEnvironmentVariable("CORECLR_PROFILER", "{D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}");
        Environment.SetEnvironmentVariable("CORECLR_PROFILER_PATH", profilerPath);
        Environment.SetEnvironmentVariable("MIXDBG_PIPE_NAME", $@"\\.\pipe\{pipeName}");
        Environment.SetEnvironmentVariable("MIXDBG_ACK_EVENT", ackEventName);

        // REHOOK event — signaled on Continue to re-enable enter/leave hooks in the profiler.
        var rehookEventName = $"MixDbgProfilerRehook-{pipeName}";
        model.ProfilerRehookEvent = new EventWaitHandle(false, EventResetMode.AutoReset, rehookEventName);
        Environment.SetEnvironmentVariable("MIXDBG_REHOOK_EVENT", rehookEventName);

        if (watchTokens != null)
            Environment.SetEnvironmentVariable("MIXDBG_WATCH_TOKENS", watchTokens);

        _log.LogInfo(_logStore, $"Profiler pipe created: {pipeName}, DLL: {profilerPath}");
    }

    /// <summary>
    /// Starts a background thread that reads JIT notifications from the profiler pipe.
    /// Each notification is parsed and added to <c>model.JitNotifications</c>.
    /// When a notification matches a deferred breakpoint, <c>SetInterrupt</c> is called
    /// to wake the engine thread so it can set the hardware breakpoint.
    /// </summary>
    private void StartProfilerReader(NativeDebuggerModel model)
    {
        if (model.ProfilerPipe == null)
            return;

        model.ProfilerReaderThread = new Thread(() => ProfilerReaderLoop(model))
        {
            Name = "profiler-reader",
            IsBackground = true,
        };
        model.ProfilerReaderThread.Start();
    }

    /// <summary>
    /// Background thread loop: waits for the profiler to connect, then reads
    /// notification lines until the pipe closes or the session terminates.
    /// Protocol:
    ///   JIT:token:address:codeSize:assembly   — method JIT'd (for stack trace map)
    ///   ENTER:token:address:assembly           — method about to execute (set BP)
    /// </summary>
    private void ProfilerReaderLoop(NativeDebuggerModel model)
    {
        try
        {
            _log.LogInfo(_logStore, "ProfilerReader: waiting for profiler to connect...");
            model.ProfilerPipe!.WaitForConnection();
            model.ProfilerConnected = true;
            model.ProfilerPipeReader = new StreamReader(model.ProfilerPipe, System.Text.Encoding.UTF8);
            _log.LogInfo(_logStore, "ProfilerReader: profiler connected");

            while (!model.Terminated)
            {
                var line = model.ProfilerPipeReader.ReadLine();
                if (line == null)
                {
                    _log.LogInfo(_logStore, "ProfilerReader: pipe closed (EOF)");
                    break;
                }

                // Parse notification line. Supports two formats:
                // Old: TOKEN:ADDRESS:SIZE:ASSEMBLY (JIT notification, may block for watched tokens)
                // New: JIT:TOKEN:ADDRESS:SIZE:ASSEMBLY / ENTER:TOKEN:ADDRESS:ASSEMBLY (prefixed)
                string payload = line;
                bool isEnterNotification = false;

                if (line.StartsWith("READY:"))
                {
                    _log.LogInfo(_logStore, $"ProfilerReader: profiler ready ({line.Substring(6)})");
                    continue;
                }
                if (line.StartsWith("JIT:"))
                {
                    // JIT: prefixed — profiler has enter hooks active.
                    // Format: JIT:TOKEN:ADDRESS:SIZE:ASSEMBLY[:IL0=N0,IL1=N1,...]
                    var jitParts = line.Substring(4).Split(':');
                    if (jitParts.Length >= 4 &&
                        int.TryParse(jitParts[0], System.Globalization.NumberStyles.HexNumber, null, out var jToken) &&
                        ulong.TryParse(jitParts[1], System.Globalization.NumberStyles.HexNumber, null, out var jAddr) &&
                        uint.TryParse(jitParts[2], System.Globalization.NumberStyles.HexNumber, null, out var jSize))
                    {
                        var jAsm = jitParts[3];
                        lock (model.JitMethodMap)
                            model.JitMethodMap[jAddr] = new JitMethodInfo(jToken, jAddr, jSize, jAsm);

                        // Parse IL-to-native mapping if present (5th field).
                        if (jitParts.Length >= 5 && jitParts[4].Length > 0)
                        {
                            var mapEntries = new List<(int ILOffset, int NativeOffset)>();
                            foreach (var entry in jitParts[4].Split(','))
                            {
                                var eqParts = entry.Split('=');
                                if (eqParts.Length == 2 &&
                                    int.TryParse(eqParts[0], System.Globalization.NumberStyles.HexNumber, null, out var il) &&
                                    int.TryParse(eqParts[1], System.Globalization.NumberStyles.HexNumber, null, out var nat))
                                {
                                    mapEntries.Add((il, nat));
                                }
                            }
                            if (mapEntries.Count > 0)
                            {
                                var key = $"{jAsm}:{jToken:X8}";
                                model.JitMethodMappings[key] = new JitMethodMapping
                                {
                                    CodeStart = jAddr,
                                    ILToNativeMap = mapEntries,
                                };
                                _log.LogInfo(_logStore, $"ProfilerReader: stored IL map for {key} ({mapEntries.Count} entries)");
                            }
                        }
                    }
                    model.ProfilerHooksActive = true;
                    continue;
                }
                if (line.StartsWith("ENTER:"))
                {
                    payload = line.Substring(6);
                    isEnterNotification = true;
                }

                var parts = payload.Split(':');

                if (isEnterNotification)
                {
                    // ENTER:TOKEN:ADDRESS:THREADID:ASSEMBLY — method frozen in enter hook.
                    // No hardware BP needed — just signal the engine to report a breakpoint stop.
                    if (parts.Length < 4) continue;
                    if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var eToken)) continue;
                    if (!ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var eAddr)) continue;
                    if (!uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var eTid)) continue;
                    var eAsm = parts[3];
                    // Don't queue another ENTER breakpoint if one is already pending
                    // (prevents duplicate stops from repeated calls before user continues).
                    if (!model.PendingEnterBreakpoint)
                    {
                        model.PendingEnterBreakpoint = true;
                        model.EnterBreakpointThreadId = eTid;
                        model.EnterBreakpointToken = eToken;
                        model.EnterBreakpointAddress = eAddr;
                        model.EnterBreakpointAssembly = eAsm;
                        _log.LogInfo(_logStore,
                            $"ProfilerReader: ENTER token=0x{eToken:X8} addr=0x{eAddr:X} tid={eTid} asm={eAsm} — interrupting engine");
                        try { model.Control.SetInterrupt(0); } catch { }
                    }
                    else
                    {
                        // Already have a pending BP — ACK immediately so profiler doesn't block.
                        model.ProfilerAckEvent?.Set();
                    }
                    continue;
                }

                // JIT notification (old or new format): TOKEN:ADDRESS:SIZE:ASSEMBLY
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var token)) continue;
                if (!ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var address)) continue;
                if (!uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var codeSize)) continue;
                var assembly = parts[3];

                // Store in sorted map for stack trace resolution.
                lock (model.JitMethodMap)
                {
                    model.JitMethodMap[address] = new JitMethodInfo(token, address, codeSize, assembly);
                }

                // Check if this JIT matches a deferred breakpoint (for JIT-based blocking).
                bool matches = false;
                foreach (var deferred in model.DeferredManagedBreakpoints)
                {
                    if (deferred.MethodToken == token &&
                        deferred.AssemblyName != null &&
                        deferred.AssemblyName.Equals(assembly, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                        break;
                    }
                }
                if (matches)
                {
                    model.JitNotifications.Enqueue(new JitNotification(token, address, codeSize, assembly));
                    _log.LogInfo(_logStore,
                        $"ProfilerReader: JIT match! token=0x{token:X8} addr=0x{address:X} asm={assembly} — interrupting engine");
                    try { model.Control.SetInterrupt(0); } catch { }
                }
            }
        }
        catch (Exception ex) when (!model.Terminated)
        {
            _log.LogInfo(_logStore, $"ProfilerReader: error: {ex.Message}");
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

    private void TryInitializeManaged(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Initializing managed debugging (CLR detected)...");
        if (_managedDebugger.InitializeRuntime(model))
        {
            // Apply any managed breakpoints that were pending before CLR loaded.
            foreach (var pending in model.PendingManagedBreakpoints)
            {
                var bps = _managedDebugger.SetManagedBreakpoints(
                    model, pending.Source.Path!, pending.Breakpoints);
                foreach (var bp in bps)
                {
                    _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                    {
                        Reason = "changed",
                        Breakpoint = bp,
                    });
                }
            }
            model.PendingManagedBreakpoints.Clear();
            _log.LogInfo(_logStore, "Managed debugging initialized (ICorDebug V4)");

            // Start polling for deferred managed breakpoint resolution.
            // Skip when profiler is connected — profiler JIT notifications are real-time
            // and don't starve the WPF UI thread like the 2s SetInterrupt polling does.
            // Only start the DAC poller if no profiler pipe exists.
            // With the profiler, ENTER hooks or JIT-blocking handle BP timing.
            // The poller would resolve and remove deferred BPs via DAC before
            // the profiler can use them.
            if (model.DeferredManagedBreakpoints.Count > 0 && model.ProfilerPipe == null)
                StartDeferredBreakpointPoller(model);
        }
    }

    /// <summary>
    /// Starts a timer that periodically interrupts the target so the engine loop
    /// can check if deferred managed breakpoints can be resolved (via
    /// <c>GetOffsetByLine</c> after JIT compilation). Stops automatically when
    /// all deferred breakpoints are resolved or the session terminates.
    /// </summary>
    private void StartDeferredBreakpointPoller(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, $"Starting deferred BP poller ({model.DeferredManagedBreakpoints.Count} deferred)");
        var timer = new System.Threading.Timer(_ =>
        {
            if (model.Terminated || model.DeferredManagedBreakpoints.Count == 0)
                return;
            try
            {
                model.Control.SetInterrupt(0); // DEBUG_INTERRUPT_ACTIVE
            }
            catch { }
        }, null, 2000, 2000);

        // Store the timer so it can be disposed.
        model.DisposeAction = () =>
        {
            timer.Dispose();
            model.Terminated = true;
            model.Commands.CompleteAdding();
            model.EngineThread?.Join(3000);
            model.Commands.Dispose();
            model.Stopped.Dispose();
            model.EngineReady.Dispose();
        };
    }

    /// <summary>
    /// Called on managed module loads. Re-enumerates ICorDebug modules and tries
    /// to bind any pending managed breakpoints against newly loaded assemblies.
    /// </summary>
    private void TryBindManagedBreakpointsOnModuleLoad(NativeDebuggerModel model)
    {
        try
        {
            var resolved = _managedDebugger.OnModuleLoad(model);
            foreach (var bp in resolved)
            {
                _log.LogInfo(_logStore, $"Managed bp bound on module load: id={bp.Id} line={bp.Line}");
                _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                {
                    Reason = "changed",
                    Breakpoint = bp,
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"TryBindManagedBreakpointsOnModuleLoad failed: {ex.Message}");
        }
    }

    private void QueueStep(NativeDebuggerModel model, uint stepKind)
    {
        model.Stepping = true;
        model.Variables.Clear();
        _cachedStackTraceResult = null;
        model.Commands.Add(() =>
        {
            RemoveTransientManagedBreakpoints(model);
            Check(model.Control.SetExecutionStatus(stepKind));
        });
    }

    private void OnBreakpoint(NativeDebuggerModel model, IDebugBreakpoint bp)
    {
        bp.GetId(out var id);
        model.LastHitBpId = id;
        model.HitUserBreakpoint = model.UserBreakpointIds.Contains(id)
            || model.ManagedBreakpointIds.Contains(id);
        _log.LogInfo(_logStore, $"OnBreakpoint: id={id} isUser={model.HitUserBreakpoint} (native: [{string.Join(",", model.UserBreakpointIds)}] managed: [{string.Join(",", model.ManagedBreakpointIds)}])");

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

    private StackFrame[]? _cachedStackTraceResult;

    private StackFrame[] GetStackTraceOnEngine(NativeDebuggerModel model, int maxFrames)
    {
        // Cache the stack trace result per stop. Repeated stackTrace requests from
        // nvim-dap (one per thread) all return the event thread's stack anyway,
        // but the redundant GetStackTrace + symbol lookups degrade the DAC,
        // breaking CreateRuntime for deferred breakpoint resolution.
        if (_cachedStackTraceResult != null)
            return _cachedStackTraceResult;

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

            // Fallback: if dbgeng can't resolve, try the profiler's JIT method map.
            // This handles managed (C#/CLI) frames where dbgeng has no symbol info.
            if (source == null && model.JitMethodMap.Count > 0)
            {
                try
                {
                    var profilerFrame = _managedDebugger.ResolveFrameFromProfilerData(model, f.InstructionOffset);
                    if (profilerFrame != null)
                    {
                        name = profilerFrame.Value.Name;
                        source = profilerFrame.Value.Source;
                        line = profilerFrame.Value.Line;
                    }
                }
                catch { }
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

        // Merge managed frame info from ClrMD.
        if (model.ManagedInitialized)
            MergeManagedFrames(model, result);

        _cachedStackTraceResult = result;
        return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private void MergeManagedFrames(NativeDebuggerModel model, StackFrame[] nativeFrames)
    {
        var managedFrames = _managedDebugger.GetManagedStackFrames(model);
        if (managedFrames.Length == 0)
            return;

        // ClrMD GetManagedStackFrames returns frames with sequential IDs starting at 1.
        // We stored the IP in the frame temporarily via the managed service. Instead,
        // we match by walking both stacks: for each native frame without source info
        // that looks like managed code, try to find a managed frame with the same IP.

        // Build a set of managed frame IPs for lookup.
        // The managed frames use the same IPs as the native stack since both read
        // the physical stack. Store managed info keyed by position for sequential match.

        // Best-effort merge: for each native frame without source info, if the
        // next managed frame has a name, overlay it.
        int managedIdx = 0;
        for (int i = 0; i < nativeFrames.Length && managedIdx < managedFrames.Length; i++)
        {
            var nf = nativeFrames[i];

            // Skip frames that already resolved to native source.
            if (nf.Source != null)
                continue;

            // Check if this looks like a JIT-compiled or CLR infrastructure frame.
            var name = nf.Name ?? "";
            bool looksManaged = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || name.Contains("coreclr!", StringComparison.OrdinalIgnoreCase)
                || name.Contains("clrjit!", StringComparison.OrdinalIgnoreCase)
                || name.Contains("clr!", StringComparison.OrdinalIgnoreCase);

            if (!looksManaged)
                continue;

            // Overlay with the next managed frame.
            var mf = managedFrames[managedIdx++];
            nativeFrames[i] = new StackFrame
            {
                Id = nf.Id,
                Name = mf.Name,
                Source = mf.Source,
                Line = mf.Line,
                Column = 0,
            };
            _log.LogInfo(_logStore, $"  Merged managed frame into slot {i}: {mf.Name}");
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
