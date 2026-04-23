using MixDbg.Models;
using MixDbg.Models.DapMessages.Execution;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP next (step over) request.
/// </summary>
public class NextRequestHandlerService(
        ISteppingService stepping,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<StepArguments>
{
    public const string DapMessage = "next";

    public override string Command => DapMessage;

    public override void ExecuteInternal(StepArguments args)
    {
        if (sessionModel.Engine is NativeDebuggerModel model)
        {
            model.Stepping = true;
            model.CachedStackTraceResult = null;
            model.CachedThreadsResult = null;
            model.Commands.Add(() => stepping.ExecuteStepOnEngine(model, EngineExecutionStatus.StepOver));
        }
        sessionModel.State = SessionState.Running;
    }
}