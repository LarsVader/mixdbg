using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP stepIn request.
/// </summary>
public class StepInRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "stepIn";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
		if (sessionModel.Engine != null)
			nativeDebugger.StepInto(sessionModel.Engine);
		sessionModel.State = SessionState.Running;
    }
}
