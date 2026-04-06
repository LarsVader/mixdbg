using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP threads request by returning all debugger threads.
/// </summary>
public class ThreadsRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ThreadsResponseBody, EmptyArguments>
{
    public const string DapMessage = "threads";

    public override string Command => DapMessage;

    public override ThreadsResponseBody ExecuteInternal(EmptyArguments args)
    {
		return session.GetThreads(sessionModel);
    }
}
