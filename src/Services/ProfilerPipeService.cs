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
    IDbgEngWrapper dbgEngWrapper) : IProfilerPipeService
{
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly IManagedBreakpointService _managedBp = managedBp;
    private readonly IDbgEngWrapper _dbgEng = dbgEngWrapper;

    public void SetupProfilerPipe(NativeDebuggerModel model)
    {
        // Find MixDbgProfiler.dll next to MixDbg.exe.
        string exeDir = AppContext.BaseDirectory;
        string profilerPath = Path.Combine(exeDir, "MixDbgProfiler.dll");

        // Also check profiler/x64/Debug/ relative to the repo root (dev builds).
        // Exe is at src/bin/Debug/net10.0/win-x64/ — 5 levels up to repo root.
        if (!File.Exists(profilerPath))
        {
            string repoRoot = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", ".."));
            string devPath = Path.Combine(repoRoot, "profiler", "x64", "Debug", "MixDbgProfiler.dll");
            if (File.Exists(devPath))
                profilerPath = devPath;
        }

        if (!File.Exists(profilerPath))
        {
            _log.LogWarning(_logStore, $"MixDbgProfiler.dll not found at {profilerPath} — JIT notifications disabled");
            return;
        }

        // Create a named pipe for the profiler to connect to.
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

        // Create a named event for ACK signaling. The profiler blocks on this event
        // after writing a JIT notification, ensuring the hardware breakpoint is set
        // before the method body executes (first-click breakpoints).
        string ackEventName = $"MixDbgProfilerAck-{pipeName}";
        model.ProfilerAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ackEventName);

        // Resolve exact method tokens from pending breakpoints so the profiler only
        // blocks for breakpointed methods (skips all other JITs including framework).
        string? watchTokens = null;
        if (model.ProfilerBreakpointHints.Count > 0)
        {
            List<(string Assembly, int Token)> tokens = _managedBp.ResolveTokensFromBreakpoints(model.ProfilerBreakpointHints);
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
        string rehookEventName = $"MixDbgProfilerRehook-{pipeName}";
        model.ProfilerRehookEvent = new EventWaitHandle(false, EventResetMode.AutoReset, rehookEventName);
        Environment.SetEnvironmentVariable("MIXDBG_REHOOK_EVENT", rehookEventName);

        if (watchTokens != null)
            Environment.SetEnvironmentVariable("MIXDBG_WATCH_TOKENS", watchTokens);

        // Create a command pipe for sending dynamic WATCH commands to the profiler.
        // Used for mid-session breakpoints set after the debugger is already running.
        string cmdPipeName = $"MixDbgProfilerCmd-{pipeName}";
        model.ProfilerCmdPipe = new NamedPipeServerStream(
            cmdPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,     // inBufferSize
            65536); // outBufferSize
        Environment.SetEnvironmentVariable("MIXDBG_CMD_PIPE", $@"\\.\pipe\{cmdPipeName}");

        // Resolve C++/CLI assembly names for assembly-level watching.
        // FunctionIDMapper is called once per method (result cached by CLR), so we
        // can't add watches dynamically. Instead, set an env var at pre-launch time
        // so the profiler hooks ALL methods from these assemblies.
        if (model.ProfilerBreakpointHints.Count > 0)
        {
            List<string> watchAssemblies = _managedBp.ResolveWatchAssemblies(model.ProfilerBreakpointHints);
            if (watchAssemblies.Count > 0)
            {
                string asmList = string.Join(",", watchAssemblies);
                Environment.SetEnvironmentVariable("MIXDBG_WATCH_ASSEMBLIES", asmList);
                _log.LogInfo(_logStore, $"Profiler watch assemblies: {asmList}");
            }
        }

        _log.LogInfo(_logStore, $"Profiler pipe created: {pipeName}, DLL: {profilerPath}");
    }

    /// <summary>
    /// Starts a background thread that reads JIT notifications from the profiler pipe.
    /// Each notification is parsed and added to <c>model.JitNotifications</c>.
    /// When a notification matches a deferred breakpoint, <c>SetInterrupt</c> is called
    /// to wake the engine thread so it can set the hardware breakpoint.
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

        // Wait for the command pipe connection on a separate thread.
        if (model.ProfilerCmdPipe != null)
        {
            Thread cmdThread = new(() =>
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
            cmdThread.Start();
        }
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
                string? line = model.ProfilerPipeReader.ReadLine();
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
                    _log.LogInfo(_logStore, $"ProfilerReader: profiler ready ({line[6..]})");
                    continue;
                }
                if (line.StartsWith("JIT:"))
                {
                    ParseJitNotification(model, line[4..]);
                    model.ProfilerHooksActive = true;
                    continue;
                }
                if (line.StartsWith("ENTER:"))
                {
                    payload = line[6..];
                    isEnterNotification = true;
                }

                string[] parts = payload.Split(':');

                if (isEnterNotification)
                {
                    ParseEnterNotification(model, parts);
                    continue;
                }

                // JIT notification (old format): TOKEN:ADDRESS:SIZE:ASSEMBLY
                ParseOldFormatJitNotification(model, parts);
            }
        }
        catch (Exception ex) when (!model.Terminated)
        {
            _log.LogInfo(_logStore, $"ProfilerReader: error: {ex.Message}");
        }
    }

    private void ParseJitNotification(NativeDebuggerModel model, string data)
    {
        // Format: TOKEN:ADDRESS:SIZE:ASSEMBLY[:IL0=N0,IL1=N1,...]
        string[] jitParts = data.Split(':');
        if (jitParts.Length < 4 ||
            !int.TryParse(jitParts[0], System.Globalization.NumberStyles.HexNumber, null, out int jToken) ||
            !ulong.TryParse(jitParts[1], System.Globalization.NumberStyles.HexNumber, null, out ulong jAddr) ||
            !uint.TryParse(jitParts[2], System.Globalization.NumberStyles.HexNumber, null, out uint jSize))
        {
            return;
        }

        string jAsm = jitParts[3];
        lock (model.JitMethodMap)
        {
            model.JitMethodMap[jAddr] = new JitMethodInfo(jToken, jAddr, jSize, jAsm);
            model.JitMethodMapSnapshot = null;
        }

        // Parse IL-to-native mapping if present (5th field).
        // Format: IL0=N0,IL1=N1,... (hex values). Parsed with spans to avoid
        // per-entry string[] allocations on this hot path.
        if (jitParts.Length >= 5 && jitParts[4].Length > 0)
        {
            List<(int ILOffset, int NativeOffset)> mapEntries = [];
            ReadOnlySpan<char> mapSpan = jitParts[4].AsSpan();
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
                string key = $"{jAsm}:{jToken:X8}";
                model.JitMethodMappings[key] = new JitMethodMapping
                {
                    CodeStart = jAddr,
                    ILToNativeMap = mapEntries,
                };
                _log.LogVerbose(_logStore, $"ProfilerReader: stored IL map for {key} ({mapEntries.Count} entries)");
            }
        }

        // Check if this JIT matches a deferred breakpoint.
        if (MatchesDeferredBreakpoint(model, jToken, jAsm))
        {
            model.JitNotifications.Enqueue(new JitNotification(jToken, jAddr, jSize, jAsm));
            _log.LogInfo(_logStore,
                $"ProfilerReader: JIT: match! token=0x{jToken:X8} addr=0x{jAddr:X} asm={jAsm} — interrupting engine");
            RequestInterrupt(model);
        }
    }

    private void ParseEnterNotification(NativeDebuggerModel model, string[] parts)
    {
        // ENTER:TOKEN:ADDRESS:THREADID:ASSEMBLY — method frozen in enter hook.
        if (parts.Length < 4) return;
        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out int eToken)) return;
        if (!ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out ulong eAddr)) return;
        if (!uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out uint eTid)) return;
        string eAsm = parts[3];

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
            RequestInterrupt(model);
        }
        else
        {
            // Already have a pending BP — ACK immediately so profiler doesn't block.
            _ = (model.ProfilerAckEvent?.Set());
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
            model.JitMethodMap[address] = new JitMethodInfo(token, address, codeSize, assembly);
            model.JitMethodMapSnapshot = null;
        }

        if (MatchesDeferredBreakpoint(model, token, assembly))
        {
            model.JitNotifications.Enqueue(new JitNotification(token, address, codeSize, assembly));
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