using MixDbg.Models;
using MixDbg.Models.Dap;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP disconnect request by detaching or terminating the target.
/// </summary>
public class DisconnectRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<DisconnectArguments>
{
    public const string DapMessage = "disconnect";

    public override string Command => DapMessage;

    public override void ExecuteInternal(DisconnectArguments args)
    {
        if (sessionModel.Engine != null)
        {
            if (args.TerminateDebuggee == true)
                nativeDebugger.Terminate(sessionModel.Engine);
            else
                nativeDebugger.Detach(sessionModel.Engine);
        }
        sessionModel.State = SessionState.Terminated;
        throw new DisconnectException();
    }
}