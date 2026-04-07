using MixDbg.Models;
using MixDbg.Models.DapMessages.Execution;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP stepIn request.
/// </summary>
public class StepInRequestHandlerService(
        IEngineQueryService engineQuery,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "stepIn";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
        if (sessionModel.Engine is NativeDebuggerModel model)
        {
            model.Stepping = true;
            model.CachedStackTraceResult = null;
            model.Commands.Add(() => engineQuery.ExecuteStepOnEngine(model, EngineExecutionStatus.StepInto));
        }
        sessionModel.State = SessionState.Running;
    }
}