using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP stackTrace request by returning the call stack.
/// </summary>
public class StackTraceRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<StackTraceResponseBody, StackTraceArguments>
{
    public const string DapMessage = "stackTrace";

    public override string Command => DapMessage;

    public override StackTraceResponseBody ExecuteInternal(StackTraceArguments args)
    {
		return session.GetStackTrace(sessionModel, args);
    }
}
