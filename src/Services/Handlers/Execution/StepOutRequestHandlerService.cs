using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP stepOut request.
/// </summary>
public class StepOutRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "stepOut";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
		if (sessionModel.Engine != null)
			nativeDebugger.StepOut(sessionModel.Engine);
		sessionModel.State = SessionState.Running;
    }
}
