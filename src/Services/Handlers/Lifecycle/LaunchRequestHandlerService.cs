using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP launch request by starting a new debug target process.
/// </summary>
public class LaunchRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<LaunchRequestArguments>
{
    public const string DapMessage = "launch";

    public override string Command => DapMessage;

    public override void ExecuteInternal(LaunchRequestArguments args)
    {
		session.Launch(sessionModel, args);
    }
}
