using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP next (step over) request.
/// </summary>
public class NextRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "next";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
		session.StepOver(sessionModel);
    }
}
