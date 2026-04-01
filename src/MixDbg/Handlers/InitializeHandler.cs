using MixDbg.Dap;
using MixDbg.Engine;

namespace MixDbg.Handlers;

public static class InitializeHandler
{
    public static void Register(DapDispatcher dispatcher, DebugSession session)
    {
        dispatcher.Register<InitializeRequestArguments>("initialize", args =>
        {
            return session.Initialize(args);
        });
    }
}
