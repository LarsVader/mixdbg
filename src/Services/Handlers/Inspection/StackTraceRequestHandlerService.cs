using MixDbg.Models.Dap;
using MixDbg.Models;

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
		if (sessionModel.Engine == null)
			return new StackTraceResponseBody { StackFrames = [] };

		var frames = nativeDebugger.GetStackTrace(sessionModel.Engine, args.Levels > 0 ? args.Levels : 50);
		return new StackTraceResponseBody
		{
			StackFrames = frames,
			TotalFrames = frames.Length,
		};
    }
}
