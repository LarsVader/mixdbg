using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP stepOut request.
/// </summary>
public class StepOutRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "stepOut";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
		session.StepOut(sessionModel);
    }
}
