using MixDbg.Models;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Models.DapMessages.Threads;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP threads request by returning all debugger threads.
/// </summary>
public class ThreadsRequestHandlerService(
        IEngineQueryService engineQuery,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ThreadsResponseBody, EmptyArguments>
{
    public const string DapMessage = "threads";

    public override string Command => DapMessage;

    public override ThreadsResponseBody ExecuteInternal(EmptyArguments args)
    {
        if (sessionModel.Engine is not NativeDebuggerModel model)
        {
            return new ThreadsResponseBody
            {
                Threads = [new DapThread { Id = 1, Name = "Main Thread" }],
            };
        }

        // Return cached result without engine round-trip when available.
        if (model.CachedThreadsResult != null)
        {
            return new ThreadsResponseBody { Threads = model.CachedThreadsResult };
        }

        DapThread[] threads = model.QueueEngineQuery(
            () => engineQuery.GetThreadsOnEngine(model));
        return new ThreadsResponseBody { Threads = threads };
    }
}