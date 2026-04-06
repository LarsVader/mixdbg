using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP disconnect request by detaching or terminating the target.
/// </summary>
public class DisconnectRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<DisconnectArguments>
{
    public const string DapMessage = "disconnect";

    public override string Command => DapMessage;

    public override void ExecuteInternal(DisconnectArguments args)
    {
		session.Disconnect(sessionModel, args);
		throw new DisconnectException();
    }
}
