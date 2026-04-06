using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP pause request.
/// </summary>
public class PauseRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "pause";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
		session.Pause(sessionModel);
    }
}
