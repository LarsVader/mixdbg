using MixDbg.Models;
using MixDbg.Models.DapMessages.Execution;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP stepOut request.
/// </summary>
public class StepOutRequestHandlerService(
        IEngineQueryService engineQuery,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "stepOut";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
        if (sessionModel.Engine is NativeDebuggerModel model)
        {
            model.Commands.Add(() => engineQuery.ExecuteStepOutOnEngine(model));
        }
        sessionModel.State = SessionState.Running;
    }
}