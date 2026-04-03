using MixDbg.Dap;
using MixDbg.Models;
using MixDbg.Services;

namespace MixDbg.Handlers;

public static class InitializeHandler
{
    public static void Register(
        IDapDispatcher dispatcher, DapDispatcherModel dispatcherModel,
        IDebugSession session, DebugSessionModel sessionModel)
    {
        dispatcher.Register<InitializeRequestArguments>(dispatcherModel, "initialize", args =>
        {
            return session.Initialize(sessionModel, args);
        });
    }
}
