using MixDbg.Models;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP terminate request by terminating the debuggee.
/// </summary>
public class TerminateRequestHandlerService(
        IEngineLifecycleService nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "terminate";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
        if (sessionModel.Engine != null)
            nativeDebugger.Terminate(sessionModel.Engine);
        sessionModel.State = SessionState.Terminated;
        throw new DisconnectException();
    }
}