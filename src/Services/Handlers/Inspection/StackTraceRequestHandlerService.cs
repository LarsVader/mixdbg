using MixDbg.Models;
using MixDbg.Models.Dap;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP stackTrace request by returning the call stack.
/// </summary>
public class StackTraceRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<StackTraceResponseBody, StackTraceArguments>
{
    public const string DapMessage = "stackTrace";

    public override string Command => DapMessage;

    public override StackTraceResponseBody ExecuteInternal(StackTraceArguments args)
    {
        if (sessionModel.Engine is not NativeDebuggerModel model)
            return new StackTraceResponseBody { StackFrames = [] };

        int maxFrames = args.Levels > 0 ? args.Levels : 50;
        StackFrame[] frames = model.QueueEngineQuery(
            () => nativeDebugger.GetStackTraceOnEngine(model, maxFrames));
        return new StackTraceResponseBody
        {
            StackFrames = frames,
            TotalFrames = frames.Length,
        };
    }
}