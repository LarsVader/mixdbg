using MixDbg.Models;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP pause request.
/// </summary>
public class PauseRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "pause";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
        if (sessionModel.Engine != null)
            nativeDebugger.Break(sessionModel.Engine);
    }
}