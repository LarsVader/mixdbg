using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP continue request by resuming execution.
/// </summary>
public class ContinueRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ContinueResponseBody, ContinueArguments>
{
    public const string DapMessage = "continue";

    public override string Command => DapMessage;

    public override ContinueResponseBody ExecuteInternal(ContinueArguments args)
    {
		if (sessionModel.Engine != null)
		{
			nativeDebugger.Continue(sessionModel.Engine);
			sessionModel.State = SessionState.Running;
		}
		return new ContinueResponseBody { AllThreadsContinued = true };
    }
}
