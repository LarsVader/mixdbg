using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP next (step over) request.
/// </summary>
public class NextRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "next";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
		if (sessionModel.Engine != null)
			nativeDebugger.StepOver(sessionModel.Engine);
		sessionModel.State = SessionState.Running;
    }
}
