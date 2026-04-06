using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP continue request by resuming execution.
/// </summary>
public class ContinueRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ContinueResponseBody, ContinueArguments>
{
    public const string DapMessage = "continue";

    public override string Command => DapMessage;

    public override ContinueResponseBody ExecuteInternal(ContinueArguments args)
    {
		session.Continue(sessionModel);
		return new ContinueResponseBody { AllThreadsContinued = true };
    }
}
