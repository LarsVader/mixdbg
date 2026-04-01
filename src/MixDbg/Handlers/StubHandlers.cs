using MixDbg.Dap;
using MixDbg.Engine;

namespace MixDbg.Handlers;

/// <summary>
/// Registers all DAP command handlers that talk to the debug session.
/// </summary>
public static class StubHandlers
{
    public static void Register(DapDispatcher dispatcher, DebugSession session)
    {
        dispatcher.Register<SetBreakpointsArguments>("setBreakpoints", args =>
        {
            return session.SetBreakpoints(args);
        });

        dispatcher.Register<ContinueArguments>("continue", _ =>
        {
            session.Continue();
            return new ContinueResponseBody { AllThreadsContinued = true };
        });

        dispatcher.Register<StepArguments>("next", _ =>
        {
            session.StepOver();
            return null;
        });

        dispatcher.Register<StepArguments>("stepIn", _ =>
        {
            session.StepInto();
            return null;
        });

        dispatcher.Register<StepArguments>("stepOut", _ =>
        {
            session.StepOut();
            return null;
        });

        dispatcher.Register("pause", _ =>
        {
            session.Pause();
            return null;
        });

        dispatcher.Register<StackTraceArguments>("stackTrace", args =>
        {
            return session.GetStackTrace(args);
        });

        dispatcher.Register<ScopesArguments>("scopes", _ =>
        {
            return new ScopesResponseBody { Scopes = [] };
        });

        dispatcher.Register<VariablesArguments>("variables", _ =>
        {
            return new VariablesResponseBody { Variables = [] };
        });

        dispatcher.Register<EvaluateArguments>("evaluate", args =>
        {
            return new EvaluateResponseBody
            {
                Result = $"[not implemented] {args.Expression}",
                VariablesReference = 0,
            };
        });

        // Silently accept these without error
        dispatcher.Register("setFunctionBreakpoints", _ => null);
        dispatcher.Register("setExceptionBreakpoints", _ => null);
        dispatcher.Register("source", _ => null);
        dispatcher.Register("loadedSources", _ => null);
    }
}
