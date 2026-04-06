using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Breakpoints;

public class SetBreakpointsRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<SetBreakpointsResponseBody, SetBreakpointsArguments>
{
    public const string DapMessage = "setBreakpoints";

    public override string Command => DapMessage;

    public override SetBreakpointsResponseBody ExecuteInternal(SetBreakpointsArguments args)
    {
		return session.SetBreakpoints(sessionModel, args);
    }
}
