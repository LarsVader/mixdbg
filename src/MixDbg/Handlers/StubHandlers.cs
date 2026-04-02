using MixDbg.Dap;
using MixDbg.Models;
using MixDbg.Services;

namespace MixDbg.Handlers;

/// <summary>
/// Registers all DAP command handlers that talk to the debug session.
/// </summary>
public static class StubHandlers
{
    public static void Register(
        IDapDispatcher dispatcher, DapDispatcherModel dispatcherModel,
        IDebugSession session, DebugSessionModel sessionModel)
    {
        dispatcher.Register<SetBreakpointsArguments>(dispatcherModel, "setBreakpoints", args =>
        {
            return session.SetBreakpoints(sessionModel, args);
        });

        dispatcher.Register<ContinueArguments>(dispatcherModel, "continue", _ =>
        {
            session.Continue(sessionModel);
            return new ContinueResponseBody { AllThreadsContinued = true };
        });

        dispatcher.Register<StepArguments>(dispatcherModel, "next", _ =>
        {
            session.StepOver(sessionModel);
            return null;
        });

        dispatcher.Register<StepArguments>(dispatcherModel, "stepIn", _ =>
        {
            session.StepInto(sessionModel);
            return null;
        });

        dispatcher.Register<StepArguments>(dispatcherModel, "stepOut", _ =>
        {
            session.StepOut(sessionModel);
            return null;
        });

        dispatcher.Register(dispatcherModel, "pause", _ =>
        {
            session.Pause(sessionModel);
            return null;
        });

        dispatcher.Register<StackTraceArguments>(dispatcherModel, "stackTrace", args =>
        {
            return session.GetStackTrace(sessionModel, args);
        });

        dispatcher.Register<ScopesArguments>(dispatcherModel, "scopes", args =>
        {
            return session.GetScopes(sessionModel, args);
        });

        dispatcher.Register<VariablesArguments>(dispatcherModel, "variables", args =>
        {
            return session.GetVariables(sessionModel, args);
        });

        dispatcher.Register<EvaluateArguments>(dispatcherModel, "evaluate", args =>
        {
            return new EvaluateResponseBody
            {
                Result = $"[not implemented] {args.Expression}",
                VariablesReference = 0,
            };
        });

        // Silently accept these without error
        dispatcher.Register(dispatcherModel, "setFunctionBreakpoints", _ => null);
        dispatcher.Register(dispatcherModel, "setExceptionBreakpoints", _ => null);
        dispatcher.Register(dispatcherModel, "source", _ => null);
        dispatcher.Register(dispatcherModel, "loadedSources", _ => null);
    }
}
