using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP stackTrace request by returning the call stack.
/// </summary>
public class StackTraceRequestHandlerService(
        IEngineQueryService engineQuery,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<StackTraceResponseBody, StackTraceArguments>
{
    public const string DapMessage = "stackTrace";

    public override string Command => DapMessage;

    public override StackTraceResponseBody ExecuteInternal(StackTraceArguments args)
    {
        if (sessionModel.Engine is not NativeDebuggerModel model)
            return new StackTraceResponseBody { StackFrames = [] };

        // Return cached result without engine round-trip when available.
        if (model.CachedStackTraceResult != null)
        {
            return new StackTraceResponseBody
            {
                StackFrames = model.CachedStackTraceResult,
                TotalFrames = model.CachedStackTraceResult.Length,
            };
        }

        int maxFrames = args.Levels > 0 ? args.Levels : 50;
        StackFrame[] frames = model.QueueEngineQuery(
            () => engineQuery.GetStackTraceOnEngine(model, maxFrames));
        return new StackTraceResponseBody
        {
            StackFrames = frames,
            TotalFrames = frames.Length,
        };
    }
}