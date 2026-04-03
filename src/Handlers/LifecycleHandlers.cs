using MixDbg.Dap;
using MixDbg.Models;
using MixDbg.Services;

namespace MixDbg.Handlers;

public static class LifecycleHandlers
{
    public static void Register(
        IDapDispatcher dispatcher, DapDispatcherModel dispatcherModel,
        IDebugSession session, DebugSessionModel sessionModel)
    {
        dispatcher.Register<LaunchRequestArguments>(dispatcherModel, "launch", args =>
        {
            session.Launch(sessionModel, args);
            return null;
        });

        dispatcher.Register<AttachRequestArguments>(dispatcherModel, "attach", args =>
        {
            session.Attach(sessionModel, args);
            return null;
        });

        dispatcher.Register(dispatcherModel, "configurationDone", _ =>
        {
            session.ConfigurationDone(sessionModel);
            return null;
        });

        dispatcher.Register<DisconnectArguments>(dispatcherModel, "disconnect", args =>
        {
            session.Disconnect(sessionModel, args);
            throw new DisconnectException();
        });

        dispatcher.Register(dispatcherModel, "terminate", _ =>
        {
            session.Disconnect(sessionModel, new DisconnectArguments { TerminateDebuggee = true });
            throw new DisconnectException();
        });

        dispatcher.Register(dispatcherModel, "threads", _ =>
        {
            return session.GetThreads(sessionModel);
        });
    }
}
