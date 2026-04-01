using MixDbg.Dap;

namespace MixDbg.Engine;

public enum SessionState
{
    Uninitialized,
    Initialized,
    Configured,
    Running,
    Stopped,
    Terminated,
}

/// <summary>
/// Central orchestrator for the debug session. Manages state transitions
/// and coordinates between the DAP layer and the debug engine.
/// </summary>
public sealed class DebugSession : IDisposable
{
    private readonly DapServer _server;
    private NativeDebugger? _engine;

    // Breakpoints received before launch — applied in ConfigurationDone.
    private readonly List<SetBreakpointsArguments> _pendingBreakpoints = new();

    public DebugSession(DapServer server)
    {
        _server = server;
    }

    public SessionState State { get; private set; } = SessionState.Uninitialized;

    public Capabilities Initialize(InitializeRequestArguments args)
    {
        State = SessionState.Initialized;
        _server.SendEvent("initialized", new InitializedEventBody());

        return new Capabilities
        {
            SupportsConfigurationDoneRequest = true,
            SupportsFunctionBreakpoints = false,
            SupportsEvaluateForHovers = true,
            SupportsTerminateRequest = true,
        };
    }

    public void ConfigurationDone()
    {
        State = SessionState.Configured;

        if (_engine != null)
        {
            // Apply breakpoints that arrived before launch.
            foreach (var pending in _pendingBreakpoints)
            {
                var bps = _engine.SetBreakpoints(
                    pending.Source.Path!, pending.Breakpoints);
                // Notify client that breakpoints are now verified.
                foreach (var bp in bps)
                {
                    Log.Write($"Sending breakpoint changed: id={bp.Id} verified={bp.Verified}");
                    _server.SendEvent("breakpoint", new BreakpointEventBody
                    {
                        Reason = "changed",
                        Breakpoint = bp,
                    });
                }
            }
            _pendingBreakpoints.Clear();

            _engine.Continue();
            State = SessionState.Running;
        }
    }

    public void Launch(LaunchRequestArguments args)
    {
        var symbolPath = args.SymbolPath != null
            ? string.Join(";", args.SymbolPath)
            : null;

        var progDir = Path.GetDirectoryName(args.Program);
        if (progDir != null)
        {
            // Walk up to find the solution root so PDBs in
            // sibling output dirs (e.g. x64/Debug) are found.
            var root = progDir;
            for (int up = 0; up < 5; up++)
            {
                var parent = Path.GetDirectoryName(root);
                if (parent == null) break;
                root = parent;
            }
            symbolPath = string.Join(";",
                new[] { progDir, root, symbolPath }
                    .Where(s => s != null));
        }

        _engine = new NativeDebugger(_server);
        _engine.Launch(args.Program, args.Cwd ?? progDir, symbolPath);
        State = SessionState.Running;
    }

    public void Attach(AttachRequestArguments args)
    {
        var symbolPath = args.SymbolPath != null
            ? string.Join(";", args.SymbolPath)
            : null;

        _engine = new NativeDebugger(_server);

        if (args.Pid.HasValue)
        {
            _engine.Attach((uint)args.Pid.Value, symbolPath);
        }
        else
        {
            throw new InvalidOperationException("PID is required for attach");
        }
        State = SessionState.Running;
    }

    public SetBreakpointsResponseBody SetBreakpoints(SetBreakpointsArguments args)
    {
        if (_engine == null || args.Source.Path == null)
        {
            // Engine not ready yet — store for later.
            if (args.Source.Path != null)
                _pendingBreakpoints.Add(args);

            var ext = Path.GetExtension(args.Source.Path ?? "").ToLowerInvariant();
            bool isNative = ext is ".cpp" or ".c" or ".cc" or ".cxx" or ".h" or ".hpp";

            return new SetBreakpointsResponseBody
            {
                Breakpoints = args.Breakpoints.Select((bp, i) => new Breakpoint
                {
                    Id = i,
                    Verified = isNative,
                    Line = bp.Line,
                    Source = args.Source,
                    Message = isNative ? null : "Managed breakpoints not yet supported",
                }).ToArray(),
            };
        }

        var bps = _engine.SetBreakpoints(args.Source.Path, args.Breakpoints);
        return new SetBreakpointsResponseBody { Breakpoints = bps };
    }

    public void Continue()
    {
        if (_engine != null)
        {
            _engine.Continue();
            State = SessionState.Running;
        }
    }

    public void StepOver()
    {
        _engine?.StepOver();
        State = SessionState.Running;
    }

    public void StepInto()
    {
        _engine?.StepInto();
        State = SessionState.Running;
    }

    public void StepOut()
    {
        _engine?.StepOut();
        State = SessionState.Running;
    }

    public void Pause()
    {
        _engine?.Break();
    }

    public StackTraceResponseBody GetStackTrace(StackTraceArguments args)
    {
        if (_engine == null)
            return new StackTraceResponseBody { StackFrames = [] };

        var frames = _engine.GetStackTrace(args.Levels > 0 ? args.Levels : 50);
        return new StackTraceResponseBody
        {
            StackFrames = frames,
            TotalFrames = frames.Length,
        };
    }

    public ThreadsResponseBody GetThreads()
    {
        if (_engine == null)
            return new ThreadsResponseBody
            {
                Threads = [new DapThread { Id = 1, Name = "Main Thread" }],
            };

        var threads = _engine.GetThreads();
        return new ThreadsResponseBody { Threads = threads };
    }

    public void Disconnect(DisconnectArguments args)
    {
        if (_engine != null)
        {
            if (args.TerminateDebuggee == true)
                _engine.Terminate();
            else
                _engine.Detach();
        }
        State = SessionState.Terminated;
    }

    public void Dispose()
    {
        _engine?.Dispose();
    }
}
