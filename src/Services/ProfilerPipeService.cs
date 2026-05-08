using System.IO.Pipes;

using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Manages the named pipe connection to the CLR profiler DLL.
/// Handles pipe setup, profiler environment variables, and background
/// reading of JIT/ENTER notifications.
/// </summary>
internal sealed class ProfilerPipeService(
    ILoggingService log,
    LogStore logStore,
    IManagedBreakpointService managedBp,
    IDbgEngWrapper dbgEngWrapper,
    IProfilerAttachIpcService attachIpc) : IProfilerPipeService
{
    /// <summary>
    /// CLSID of <c>MixDbgProfiler</c> — must match the value in
    /// <c>profiler/ClassFactory.cpp</c>.
    /// </summary>
    internal static readonly Guid ProfilerClsid = new("{D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}");

    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly IManagedBreakpointService _managedBp = managedBp;
    private readonly IDbgEngWrapper _dbgEng = dbgEngWrapper;
    private readonly IProfilerAttachIpcService _attachIpc = attachIpc;

    public void SetupProfilerPipe(NativeDebuggerModel model)
    {
        string? profilerPath = LocateProfilerDll();
        if (profilerPath == null)
            return;

        (string pipeName, string ackEventName, string cmdPipeName) = CreatePipesAndAckEvent(model);

        // Resolve exact method tokens from pending breakpoints so the profiler only
        // blocks for breakpointed methods (skips all other JITs including framework).
        List<(string Assembly, int Token)> watchTokens = ResolveWatchTokens(model);
        string? watchTokensCsv = watchTokens.Count > 0
            ? string.Join(",", watchTokens.Select(t => $"{t.Assembly}:{t.Token:X8}"))
            : null;
        if (watchTokensCsv != null)
            _log.LogInfo(_logStore, $"Profiler watch tokens: {watchTokensCsv}");

        // Set CLR profiling env vars — child process inherits them.
        Environment.SetEnvironmentVariable("CORECLR_ENABLE_PROFILING", "1");
        Environment.SetEnvironmentVariable("CORECLR_PROFILER", ProfilerClsid.ToString("B").ToUpperInvariant());
        Environment.SetEnvironmentVariable("CORECLR_PROFILER_PATH", profilerPath);
        Environment.SetEnvironmentVariable("MIXDBG_PIPE_NAME", $@"\\.\pipe\{pipeName}");
        Environment.SetEnvironmentVariable("MIXDBG_ACK_EVENT", ackEventName);
        Environment.SetEnvironmentVariable("MIXDBG_CMD_PIPE", $@"\\.\pipe\{cmdPipeName}");

        if (watchTokensCsv != null)
            Environment.SetEnvironmentVariable("MIXDBG_WATCH_TOKENS", watchTokensCsv);

        _log.LogInfo(_logStore, $"Profiler pipe created: {pipeName}, DLL: {profilerPath}");
    }

    public void SetupProfilerPipeForAttach(NativeDebuggerModel model, int pid)
    {
        string? profilerPath = LocateProfilerDll();
        if (profilerPath == null)
            return;

        (string pipeName, string ackEventName, string cmdPipeName) = CreatePipesAndAckEvent(model);

        List<(string Assembly, int Token)> watchTokens = ResolveWatchTokens(model);
        if (watchTokens.Count > 0)
        {
            _log.LogInfo(_logStore,
                $"Attach profiler watch tokens: {string.Join(",", watchTokens.Select(t => $"{t.Assembly}:{t.Token:X8}"))}");
        }

        byte[] clientData = ProfilerClientDataBuilder.Build(
            pipeName: $@"\\.\pipe\{pipeName}",
            ackEventName: ackEventName,
            cmdPipeName: $@"\\.\pipe\{cmdPipeName}",
            watchTokens: watchTokens);

        // Tag the model AFTER the IPC succeeds. If the IPC throws, IsRejitMode
        // stays false so the resolver doesn't take the attach-mode-only paths
        // for the rest of the (failed) session.
        _attachIpc.AttachProfiler(pid, ProfilerClsid, profilerPath, clientData);
        model.IsRejitMode = true;

        _log.LogInfo(_logStore, $"Profiler attached to pid {pid}: {pipeName}, DLL: {profilerPath}");
    }

    /// <summary>
    /// Locates <c>MixDbgProfiler.dll</c> next to the exe, or in the dev-build
    /// fallback (<c>profiler/x64/Debug/</c>). Returns null if not found and logs
    /// a warning — managed BPs are a no-op without the profiler.
    /// </summary>
    private string? LocateProfilerDll()
    {
        string exeDir = AppContext.BaseDirectory;
        string profilerPath = Path.Combine(exeDir, "MixDbgProfiler.dll");

        if (!File.Exists(profilerPath))
        {
            // Exe is at src/bin/Debug/net10.0/win-x64/ — 5 levels up to repo root.
            string repoRoot = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", ".."));
            string devPath = Path.Combine(repoRoot, "profiler", "x64", "Debug", "MixDbgProfiler.dll");
            if (File.Exists(devPath))
                profilerPath = devPath;
        }

        if (!File.Exists(profilerPath))
        {
            _log.LogWarning(_logStore, $"MixDbgProfiler.dll not found at {profilerPath} — JIT notifications disabled");
            return null;
        }
        return profilerPath;
    }

    /// <summary>
    /// Creates the notification pipe (profiler→MixDbg), ACK event, and command
    /// pipe (MixDbg→profiler) on <paramref name="model"/>. Returns the names so
    /// callers can install them as env vars (launch) or pack them into client
    /// data (attach).
    /// </summary>
    private static (string PipeName, string AckEventName, string CmdPipeName)
        CreatePipesAndAckEvent(NativeDebuggerModel model)
    {
        string pipeName = $"MixDbgProfiler-{Environment.ProcessId}-{Guid.NewGuid():N}";
        model.ProfilerPipeName = pipeName;
        model.ProfilerPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1, // max connections
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            65536, // inBufferSize
            0);    // outBufferSize

        string ackEventName = $"MixDbgProfilerAck-{pipeName}";
        model.ProfilerAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ackEventName);

        string cmdPipeName = $"MixDbgProfilerCmd-{pipeName}";
        model.ProfilerCmdPipe = new NamedPipeServerStream(
            cmdPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            65536);

        return (pipeName, ackEventName, cmdPipeName);
    }

    private List<(string Assembly, int Token)> ResolveWatchTokens(NativeDebuggerModel model)
        => model.ProfilerBreakpointHints.Count == 0
            ? []
            : _managedBp.ResolveTokensFromBreakpoints(model.ProfilerBreakpointHints);

    /// <summary>
    /// Starts a background thread that reads profiler notifications from the pipe.
    /// JIT/ENTER/LEAVE/TAILCALL notifications are enqueued onto
    /// <c>model.ProfilerNotifications</c>. <c>SetInterrupt</c> is called when the
    /// engine thread needs to wake up to process them.
    /// </summary>
    public void StartProfilerReader(NativeDebuggerModel model)
    {
        if (model.ProfilerPipe == null)
            return;

        model.ProfilerReaderThread = new Thread(() => ProfilerReaderLoop(model))
        {
            Name = "profiler-reader",
            IsBackground = true,
        };
        model.ProfilerReaderThread.Start();

        // Wait for the command pipe connection on a separate thread. The
        // thread handle is stored on the model so DisposeAction can join
        // it — without that, a profiler that dies before connecting leaves
        // the thread blocked in WaitForConnection until the OS cleans it
        // up at process exit.
        if (model.ProfilerCmdPipe != null)
        {
            model.ProfilerCmdConnectThread = new Thread(() =>
            {
                try
                {
                    model.ProfilerCmdPipe.WaitForConnection();
                    // Use UTF8 WITHOUT BOM — the profiler's parser is a byte-level
                    // strncmp and would fail to match "WATCH:" if a BOM prefix is present.
                    StreamWriter writer = new(model.ProfilerCmdPipe, new System.Text.UTF8Encoding(false))
                    {
                        AutoFlush = true,
                    };

                    // Drain any WATCH commands queued before the pipe connected.
                    while (model.PendingWatchCommands.TryDequeue(out string? line))
                    {
                        writer.WriteLine(line);
                        _log.LogVerbose(_logStore, $"ProfilerCmd: flushed queued {line}");
                    }

                    // Publish the writer AFTER draining so queued commands aren't missed
                    // by a concurrent SendWatchToken call (it checks writer == null).
                    model.ProfilerCmdPipeWriter = writer;
                    _log.LogInfo(_logStore, "ProfilerCmd: command pipe connected");
                }
                catch (Exception ex)
                {
                    _log.LogInfo(_logStore, $"ProfilerCmd: connection failed: {ex.Message}");
                }
            })
            {
                Name = "profiler-cmd-connect",
                IsBackground = true,
            };
            model.ProfilerCmdConnectThread.Start();
        }
    }

    /// <summary>
    /// Background thread loop: waits for the profiler to connect, then reads
    /// notification lines until the pipe closes or the session terminates.
    /// Protocol:
    ///   JIT:token:address:codeSize:assembly[:IL-map]  — method JIT'd (for stack trace map)
    ///   ENTER:token:bodyAddress:threadId:assembly     — method activation starts
    ///   LEAVE:token:threadId:assembly                 — method activation ends
    ///   TAILCALL:token:threadId:assembly              — method activation ends via tailcall
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
                string? line = model.ProfilerPipeReader.ReadLine();
                if (line == null)
                {
                    _log.LogInfo(_logStore, "ProfilerReader: pipe closed (EOF)");
                    break;
                }

                if (line.StartsWith("READY:"))
                {
                    _log.LogInfo(_logStore, $"ProfilerReader: profiler ready ({line[6..]})");
                    // READY:attach signals that InitializeForAttach finished
                    // its setup (event mask set, ack/cmd pipes opened, watch
                    // list parsed) — this is the only safe point at which
                    // dbgeng can suspend runtime threads without corrupting
                    // profiler init.
                    if (line.AsSpan(6).StartsWith("attach"))
                        model.ProfilerInitComplete = true;
                    _ = Interlocked.Increment(ref model.ProfilerLinesProcessed);
                    continue;
                }
                if (line.StartsWith("JIT:"))
                {
                    // ProfilerHooksActive means "ENTER/LEAVE hooks are authoritative
                    // for installing managed BPs" — true in launch mode (runtime
                    // FunctionEnter/Leave fires), false in attach mode (no
                    // COR_PRF_MONITOR_ENTERLEAVE; only JIT-time eager install
                    // catches new JITs). In attach mode the DAC fallback is the
                    // only path that binds BPs on methods JIT'd before attach,
                    // so we must NOT suppress it here.
                    if (!model.IsRejitMode)
                        model.ProfilerHooksActive = true;
                    ParseJitNotification(model, line[4..]);
                    _ = Interlocked.Increment(ref model.ProfilerLinesProcessed);
                    continue;
                }
                if (line.StartsWith("ENTER:"))
                {
                    ParseEnterNotification(model, line[6..].Split(':'));
                    _ = Interlocked.Increment(ref model.ProfilerLinesProcessed);
                    continue;
                }
                if (line.StartsWith("LEAVE:"))
                {
                    ParseLeaveOrTailcallNotification(model, line[6..].Split(':'), isTailcall: false);
                    _ = Interlocked.Increment(ref model.ProfilerLinesProcessed);
                    continue;
                }
                if (line.StartsWith("TAILCALL:"))
                {
                    ParseLeaveOrTailcallNotification(model, line[9..].Split(':'), isTailcall: true);
                    _ = Interlocked.Increment(ref model.ProfilerLinesProcessed);
                    continue;
                }

                // JIT notification (old format): TOKEN:ADDRESS:SIZE:ASSEMBLY
                ParseOldFormatJitNotification(model, line.Split(':'));
                _ = Interlocked.Increment(ref model.ProfilerLinesProcessed);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (!model.Terminated)
        {
            _log.LogInfo(_logStore, $"ProfilerReader: error: {ex.Message}");
        }
    }

    private void ParseJitNotification(NativeDebuggerModel model, string data)
    {
        // Format: TOKEN:ADDRESS:SIZE:ASSEMBLY[:IL0=N0,IL1=N1,...]
        // Span-based field parsing to avoid string[] allocation on this hot path.
        ReadOnlySpan<char> span = data.AsSpan();

        int sep1 = span.IndexOf(':');
        if (sep1 < 0 || !int.TryParse(span[..sep1], System.Globalization.NumberStyles.HexNumber, null, out int jToken))
            return;
        span = span[(sep1 + 1)..];

        int sep2 = span.IndexOf(':');
        if (sep2 < 0 || !ulong.TryParse(span[..sep2], System.Globalization.NumberStyles.HexNumber, null, out ulong jAddr))
            return;
        span = span[(sep2 + 1)..];

        int sep3 = span.IndexOf(':');
        if (sep3 < 0 || !uint.TryParse(span[..sep3], System.Globalization.NumberStyles.HexNumber, null, out uint jSize))
            return;
        span = span[(sep3 + 1)..];

        // Assembly name (must be materialized as string for storage).
        int sep4 = span.IndexOf(':');
        string jAsm = sep4 >= 0 ? span[..sep4].ToString() : span.ToString();

        lock (model.JitMethodMap)
        {
            JitMethodInfo jitInfo = new(jToken, jAddr, jSize, jAsm);
            model.JitMethodMap[jAddr] = jitInfo;
            model.JitMethodMapByToken[(jToken, jAsm)] = jitInfo;
            model.JitMethodMapSnapshot = null;
        }

        // Parse IL-to-native mapping if present (5th field).
        // Format: IL0=N0,IL1=N1,... (hex values). Parsed with spans to avoid
        // per-entry string[] allocations on this hot path.
        if (sep4 >= 0)
        {
            ReadOnlySpan<char> mapSpan = span[(sep4 + 1)..];
            if (mapSpan.Length > 0)
            {
                List<(int ILOffset, int NativeOffset)> mapEntries = [];
                while (mapSpan.Length > 0)
                {
                    int comma = mapSpan.IndexOf(',');
                    ReadOnlySpan<char> entry = comma >= 0 ? mapSpan[..comma] : mapSpan;
                    mapSpan = comma >= 0 ? mapSpan[(comma + 1)..] : default;

                    int eq = entry.IndexOf('=');
                    if (eq > 0 &&
                        int.TryParse(entry[..eq], System.Globalization.NumberStyles.HexNumber, null, out int il) &&
                        int.TryParse(entry[(eq + 1)..], System.Globalization.NumberStyles.HexNumber, null, out int nat))
                    {
                        mapEntries.Add((il, nat));
                    }
                }
                if (mapEntries.Count > 0)
                {
                    model.JitMethodMappings[(jToken, jAsm)] = new JitMethodMapping(jAddr, mapEntries);
                    _log.LogVerbose(_logStore, $"ProfilerReader: stored IL map for {jAsm}:0x{jToken:X8} ({mapEntries.Count} entries)");
                }
            }
        }

        // Check if this JIT matches a deferred breakpoint.
        if (MatchesDeferredBreakpoint(model, jToken, jAsm))
        {
            model.ProfilerNotifications.Enqueue(new JitNotification(jToken, jAddr, jSize, jAsm));
            _log.LogInfo(_logStore,
                $"ProfilerReader: JIT: match! token=0x{jToken:X8} addr=0x{jAddr:X} asm={jAsm} — interrupting engine");
            RequestInterrupt(model);
        }
    }

    private void ParseEnterNotification(NativeDebuggerModel model, string[] parts)
    {
        // ENTER:TOKEN:BODYADDRESS:THREADID:ASSEMBLY
        if (parts.Length < 4) return;
        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out int eToken)) return;
        if (!ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out ulong eAddr)) return;
        if (!uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out uint eTid)) return;
        string eAsm = parts[3];

        // Fast-path: if no plan and no active BPs for this method, ACK immediately
        // without interrupting the engine. In large assemblies, most methods have no
        // BPs and interrupting for each ENTER makes the debugger unresponsive.
        (int Token, string Assembly) key = (eToken, eAsm);
        if (!model.ManagedBpPlans.ContainsKey(key) && !model.ActiveMethodBreakpoints.ContainsKey(key))
        {
            _log.LogVerbose(_logStore,
                $"ProfilerReader: ENTER token=0x{eToken:X8} asm={eAsm} — no plan, ACK-only");
            _ = model.ProfilerAckEvent?.Set();
            return;
        }

        model.ProfilerNotifications.Enqueue(new EnterNotification(eToken, eAddr, eTid, eAsm));
        _log.LogInfo(_logStore,
            $"ProfilerReader: ENTER token=0x{eToken:X8} addr=0x{eAddr:X} tid={eTid} asm={eAsm} — interrupting engine");
        RequestInterrupt(model);
    }

    private void ParseLeaveOrTailcallNotification(NativeDebuggerModel model, string[] parts, bool isTailcall)
    {
        // LEAVE:TOKEN:THREADID:ASSEMBLY  or  TAILCALL:TOKEN:THREADID:ASSEMBLY
        if (parts.Length < 3) return;
        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out int token)) return;
        if (!uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out uint tid)) return;
        string asm = parts[2];

        ProfilerNotification notification = isTailcall
            ? new TailcallNotification(token, tid, asm)
            : new LeaveNotification(token, tid, asm);
        model.ProfilerNotifications.Enqueue(notification);

        // Only interrupt when this method has active HW BPs that need to be removed.
        if (model.ActiveMethodBreakpoints.ContainsKey((token, asm)))
        {
            _log.LogVerbose(_logStore,
                $"ProfilerReader: {(isTailcall ? "TAILCALL" : "LEAVE")} token=0x{token:X8} tid={tid} asm={asm} — interrupting engine");
            RequestInterrupt(model);
        }
    }

    private void ParseOldFormatJitNotification(NativeDebuggerModel model, string[] parts)
    {
        if (parts.Length < 4) return;
        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out int token)) return;
        if (!ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out ulong address)) return;
        if (!uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out uint codeSize)) return;
        string assembly = parts[3];

        // Store in map for stack trace resolution.
        lock (model.JitMethodMap)
        {
            JitMethodInfo jitInfo = new(token, address, codeSize, assembly);
            model.JitMethodMap[address] = jitInfo;
            model.JitMethodMapByToken[(token, assembly)] = jitInfo;
            model.JitMethodMapSnapshot = null;
        }

        if (MatchesDeferredBreakpoint(model, token, assembly))
        {
            model.ProfilerNotifications.Enqueue(new JitNotification(token, address, codeSize, assembly));
            _log.LogInfo(_logStore,
                $"ProfilerReader: JIT match! token=0x{token:X8} addr=0x{address:X} asm={assembly} — interrupting engine");
            RequestInterrupt(model);
        }
    }

    /// <summary>
    /// Requests an engine interrupt. Calls SetInterrupt directly when the engine
    /// is in WaitForEvent (safe per dbgeng docs). Otherwise sets a flag for the
    /// engine thread to pick up — avoids cross-thread COM calls during active
    /// operations which corrupt .NET RCW state.
    /// </summary>
    private void RequestInterrupt(NativeDebuggerModel model)
    {
        if (model.InWaitForEvent)
        {
            try { _dbgEng.SetInterrupt(model.Wrapper); } catch { }
        }
        else
        {
            model.Wrapper.InterruptRequested = true;
        }
    }

    private static bool MatchesDeferredBreakpoint(NativeDebuggerModel model, int token, string assembly)
        => model.DeferredBreakpointIndex.Contains((token, assembly));
}