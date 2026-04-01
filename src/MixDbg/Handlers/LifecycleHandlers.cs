using MixDbg.Dap;
using MixDbg.Engine;

namespace MixDbg.Handlers;

public static class LifecycleHandlers
{
    public static void Register(DapDispatcher dispatcher, DebugSession session)
    {
        dispatcher.Register<LaunchRequestArguments>("launch", args =>
        {
            session.Launch(args);
            return null;
        });

        dispatcher.Register<AttachRequestArguments>("attach", args =>
        {
            session.Attach(args);
            return null;
        });

        dispatcher.Register("configurationDone", _ =>
        {
            session.ConfigurationDone();
            return null;
        });

        dispatcher.Register<DisconnectArguments>("disconnect", args =>
        {
            session.Disconnect(args);
            throw new DisconnectException();
        });

        dispatcher.Register("terminate", _ =>
        {
            session.Disconnect(new DisconnectArguments { TerminateDebuggee = true });
            throw new DisconnectException();
        });

        dispatcher.Register("threads", _ =>
        {
            return session.GetThreads();
        });
    }
}
