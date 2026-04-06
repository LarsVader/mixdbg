using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP terminate request by terminating the debuggee.
/// </summary>
public class TerminateRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "terminate";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
		session.Disconnect(sessionModel, new DisconnectArguments { TerminateDebuggee = true });
		throw new DisconnectException();
    }
}
