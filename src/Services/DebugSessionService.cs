using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless debug session orchestrator. All mutable state lives in
/// <see cref="DebugSessionModel"/>.
/// </summary>
internal sealed class DebugSessionService(
    IDapServer server,
    DapServerModel transport,
    ILoggingService log,
    LogStore logStore,
    INativeDebugger nativeDebugger) : IDebugSession
{
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
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
                foreach (var bp in bps)
                {
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
        session.Engine = _nativeDebugger.CreateModel();

        // Copy pending breakpoint file:line pairs so the profiler knows which
        // assemblies to block on during JIT (avoids blocking all 2000+ startup JITs).
        foreach (var pending in session.PendingBreakpoints)
        {
            if (pending.Source.Path != null)
            {
                foreach (var bp in pending.Breakpoints)
                    session.Engine.ProfilerBreakpointHints.Add((pending.Source.Path, bp.Line));
            }
        }

        string? symbolPath = null;
        if (args.SymbolPath is { Length: > 0 })
            symbolPath = string.Join(";", args.SymbolPath);

        _nativeDebugger.Launch(
            session.Engine,
            args.Program,
            args.Cwd ?? Path.GetDirectoryName(args.Program),
            symbolPath,
            args.Args);
        session.State = SessionState.Running;
    }

    public void Attach(DebugSessionModel session, AttachRequestArguments args)
    {
        session.Engine = _nativeDebugger.CreateModel();

        if (args.Pid.HasValue)
        {
            string? symbolPath = null;
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
            if (args.Source.Path != null)
                session.PendingBreakpoints.Add(args);

            return new SetBreakpointsResponseBody
            {
                Breakpoints = args.Breakpoints.Select((bp, i) => new Breakpoint
                {
                    Id = session.NextPendingBpId++,
                    Verified = true,
                    Line = bp.Line,
                    Source = args.Source,
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

        return new ScopesResponseBody { Scopes = _nativeDebugger.GetScopes(session.Engine, args.FrameId) };
    }

    public VariablesResponseBody GetVariables(DebugSessionModel session, VariablesArguments args)
    {
        if (session.Engine == null)
            return new VariablesResponseBody { Variables = [] };

        return new VariablesResponseBody { Variables = _nativeDebugger.GetVariables(session.Engine, args.VariablesReference) };
    }

    public ThreadsResponseBody GetThreads(DebugSessionModel session)
    {
        if (session.Engine == null)
            return new ThreadsResponseBody
            {
                Threads = [new DapThread { Id = 1, Name = "Main Thread" }],
            };

        return new ThreadsResponseBody { Threads = _nativeDebugger.GetThreads(session.Engine) };
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
