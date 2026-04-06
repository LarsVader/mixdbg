using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP threads request by returning all debugger threads.
/// </summary>
public class ThreadsRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ThreadsResponseBody, EmptyArguments>
{
    public const string DapMessage = "threads";

    public override string Command => DapMessage;

    public override ThreadsResponseBody ExecuteInternal(EmptyArguments args)
    {
		if (sessionModel.Engine == null)
			return new ThreadsResponseBody
			{
				Threads = [new DapThread { Id = 1, Name = "Main Thread" }],
			};

		return new ThreadsResponseBody { Threads = nativeDebugger.GetThreads(sessionModel.Engine) };
    }
}
