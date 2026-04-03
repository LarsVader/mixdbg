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
    ILoggingService log,
    LogStore logStore,
    ICorDebugEngine engine) : IDebugSession
{
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly ICorDebugEngine _engine = engine;

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

        if (session.CorEngine != null)
        {
            // Apply breakpoints that arrived before launch.
            foreach (var pending in session.PendingBreakpoints)
            {
                var bps = _engine.SetBreakpoints(
                    session.CorEngine, pending.Source.Path!, pending.Breakpoints);
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

            _engine.Continue(session.CorEngine);
            session.State = SessionState.Running;
        }
    }

    public void Launch(DebugSessionModel session, LaunchRequestArguments args)
    {
        session.CorEngine = _engine.CreateModel();
        _engine.Launch(session.CorEngine, args.Program, args.Cwd ?? Path.GetDirectoryName(args.Program), args.Args);
        session.State = SessionState.Running;
    }

    public void Attach(DebugSessionModel session, AttachRequestArguments args)
    {
        session.CorEngine = _engine.CreateModel();

        if (args.Pid.HasValue)
            _engine.Attach(session.CorEngine, (uint)args.Pid.Value);
        else
            throw new InvalidOperationException("PID is required for attach");

        session.State = SessionState.Running;
    }

    public SetBreakpointsResponseBody SetBreakpoints(DebugSessionModel session, SetBreakpointsArguments args)
    {
        if (session.CorEngine == null || args.Source.Path == null)
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

        var bps = _engine.SetBreakpoints(session.CorEngine, args.Source.Path, args.Breakpoints);
        return new SetBreakpointsResponseBody { Breakpoints = bps };
    }

    public void Continue(DebugSessionModel session)
    {
        if (session.CorEngine != null)
        {
            _engine.Continue(session.CorEngine);
            session.State = SessionState.Running;
        }
    }

    public void StepOver(DebugSessionModel session)
    {
        if (session.CorEngine != null)
            _engine.StepOver(session.CorEngine);
        session.State = SessionState.Running;
    }

    public void StepInto(DebugSessionModel session)
    {
        if (session.CorEngine != null)
            _engine.StepInto(session.CorEngine);
        session.State = SessionState.Running;
    }

    public void StepOut(DebugSessionModel session)
    {
        if (session.CorEngine != null)
            _engine.StepOut(session.CorEngine);
        session.State = SessionState.Running;
    }

    public void Pause(DebugSessionModel session)
    {
        if (session.CorEngine != null)
            _engine.Break(session.CorEngine);
    }

    public StackTraceResponseBody GetStackTrace(DebugSessionModel session, StackTraceArguments args)
    {
        if (session.CorEngine == null)
            return new StackTraceResponseBody { StackFrames = [] };

        var frames = _engine.GetStackTrace(session.CorEngine, args.Levels > 0 ? args.Levels : 50);
        return new StackTraceResponseBody
        {
            StackFrames = frames,
            TotalFrames = frames.Length,
        };
    }

    public ScopesResponseBody GetScopes(DebugSessionModel session, ScopesArguments args)
    {
        if (session.CorEngine == null)
            return new ScopesResponseBody { Scopes = [] };

        return new ScopesResponseBody { Scopes = _engine.GetScopes(session.CorEngine, args.FrameId) };
    }

    public VariablesResponseBody GetVariables(DebugSessionModel session, VariablesArguments args)
    {
        if (session.CorEngine == null)
            return new VariablesResponseBody { Variables = [] };

        return new VariablesResponseBody { Variables = _engine.GetVariables(session.CorEngine, args.VariablesReference) };
    }

    public ThreadsResponseBody GetThreads(DebugSessionModel session)
    {
        if (session.CorEngine == null)
            return new ThreadsResponseBody
            {
                Threads = [new DapThread { Id = 1, Name = "Main Thread" }],
            };

        return new ThreadsResponseBody { Threads = _engine.GetThreads(session.CorEngine) };
    }

    public void Disconnect(DebugSessionModel session, DisconnectArguments args)
    {
        if (session.CorEngine != null)
        {
            if (args.TerminateDebuggee == true)
                _engine.Terminate(session.CorEngine);
            else
                _engine.Detach(session.CorEngine);
        }
        session.State = SessionState.Terminated;
    }
}
