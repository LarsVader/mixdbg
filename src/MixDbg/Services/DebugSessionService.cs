using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless debug session orchestrator. All mutable state lives in
/// <see cref="DebugSessionModel"/>.
/// </summary>
internal sealed class DebugSessionService(
    IDapServer server,
    DapServerModel transport,
    ISourceFileService sourceFiles,
    ILoggingService log,
    LogStore logStore,
    INativeDebugger nativeDebugger) : IDebugSession
{
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ISourceFileService _sourceFiles = sourceFiles;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly INativeDebugger _nativeDebugger = nativeDebugger;

    public DebugSessionModel CreateModel()
        => new();

    public Capabilities Initialize(DebugSessionModel session, InitializeRequestArguments args)
    {
        session.State = SessionState.Initialized;
        _server.SendEvent(_transport, "initialized", new InitializedEventBody());

        return new Capabilities
        {
            SupportsConfigurationDoneRequest = true,
            SupportsFunctionBreakpoints = false,
            SupportsEvaluateForHovers = true,
            SupportsTerminateRequest = true,
        };
    }

    public void ConfigurationDone(DebugSessionModel session)
    {
        session.State = SessionState.Configured;

        if (session.Engine != null)
        {
            // Apply breakpoints that arrived before launch.
            foreach (var pending in session.PendingBreakpoints)
            {
                var bps = _nativeDebugger.SetBreakpoints(
                    session.Engine, pending.Source.Path!, pending.Breakpoints);
                // Notify client that breakpoints are now verified.
                foreach (var bp in bps)
                {
                    _log.LogInfo(_logStore, $"Sending breakpoint changed: id={bp.Id} verified={bp.Verified}");
                    _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                    {
                        Reason = "changed",
                        Breakpoint = bp,
                    });
                }
            }
            session.PendingBreakpoints.Clear();

            _nativeDebugger.Continue(session.Engine);
            session.State = SessionState.Running;
        }
    }

    public void Launch(DebugSessionModel session, LaunchRequestArguments args)
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

        session.Engine = _nativeDebugger.CreateModel();
        _nativeDebugger.Launch(session.Engine, args.Program, args.Cwd ?? progDir, symbolPath);
        session.State = SessionState.Running;
    }

    public void Attach(DebugSessionModel session, AttachRequestArguments args)
    {
        var symbolPath = args.SymbolPath != null
            ? string.Join(";", args.SymbolPath)
            : null;

        session.Engine = _nativeDebugger.CreateModel();

        if (args.Pid.HasValue)
        {
            _nativeDebugger.Attach(session.Engine, (uint)args.Pid.Value, symbolPath);
        }
        else
        {
            throw new InvalidOperationException("PID is required for attach");
        }
        session.State = SessionState.Running;
    }

    public SetBreakpointsResponseBody SetBreakpoints(DebugSessionModel session, SetBreakpointsArguments args)
    {
        if (session.Engine == null || args.Source.Path == null)
        {
            // Engine not ready yet — store for later.
            if (args.Source.Path != null)
                session.PendingBreakpoints.Add(args);

            bool isNative = _sourceFiles.IsNativeFile(args.Source.Path!);

            return new SetBreakpointsResponseBody
            {
                Breakpoints = args.Breakpoints.Select((bp, i) => new Breakpoint
                {
                    Id = session.NextPendingBpId++,
                    Verified = isNative,
                    Line = bp.Line,
                    Source = args.Source,
                    Message = isNative ? null : "Managed breakpoints not yet supported",
                }).ToArray(),
            };
        }

        var bps = _nativeDebugger.SetBreakpoints(session.Engine, args.Source.Path, args.Breakpoints);
        return new SetBreakpointsResponseBody { Breakpoints = bps };
    }

    public void Continue(DebugSessionModel session)
    {
        if (session.Engine != null)
        {
            _nativeDebugger.Continue(session.Engine);
            session.State = SessionState.Running;
        }
    }

    public void StepOver(DebugSessionModel session)
    {
        if (session.Engine != null)
            _nativeDebugger.StepOver(session.Engine);
        session.State = SessionState.Running;
    }

    public void StepInto(DebugSessionModel session)
    {
        if (session.Engine != null)
            _nativeDebugger.StepInto(session.Engine);
        session.State = SessionState.Running;
    }

    public void StepOut(DebugSessionModel session)
    {
        if (session.Engine != null)
            _nativeDebugger.StepOut(session.Engine);
        session.State = SessionState.Running;
    }

    public void Pause(DebugSessionModel session)
    {
        if (session.Engine != null)
            _nativeDebugger.Break(session.Engine);
    }

    public StackTraceResponseBody GetStackTrace(DebugSessionModel session, StackTraceArguments args)
    {
        if (session.Engine == null)
            return new StackTraceResponseBody { StackFrames = [] };

        var frames = _nativeDebugger.GetStackTrace(session.Engine, args.Levels > 0 ? args.Levels : 50);
        return new StackTraceResponseBody
        {
            StackFrames = frames,
            TotalFrames = frames.Length,
        };
    }

    public ScopesResponseBody GetScopes(DebugSessionModel session, ScopesArguments args)
    {
        if (session.Engine == null)
            return new ScopesResponseBody { Scopes = [] };

        var scopes = _nativeDebugger.GetScopes(session.Engine, args.FrameId);
        return new ScopesResponseBody { Scopes = scopes };
    }

    public VariablesResponseBody GetVariables(DebugSessionModel session, VariablesArguments args)
    {
        if (session.Engine == null)
            return new VariablesResponseBody { Variables = [] };

        var vars = _nativeDebugger.GetVariables(session.Engine, args.VariablesReference);
        return new VariablesResponseBody { Variables = vars };
    }

    public ThreadsResponseBody GetThreads(DebugSessionModel session)
    {
        if (session.Engine == null)
            return new ThreadsResponseBody
            {
                Threads = [new DapThread { Id = 1, Name = "Main Thread" }],
            };

        var threads = _nativeDebugger.GetThreads(session.Engine);
        return new ThreadsResponseBody { Threads = threads };
    }

    public void Disconnect(DebugSessionModel session, DisconnectArguments args)
    {
        if (session.Engine != null)
        {
            if (args.TerminateDebuggee == true)
                _nativeDebugger.Terminate(session.Engine);
            else
                _nativeDebugger.Detach(session.Engine);
        }
        session.State = SessionState.Terminated;
    }
}
