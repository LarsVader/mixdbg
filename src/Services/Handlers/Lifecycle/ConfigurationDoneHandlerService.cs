using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP configurationDone request by applying pending breakpoints and resuming.
/// </summary>
public class ConfigurationDoneHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "configurationDone";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
		session.ConfigurationDone(sessionModel);
    }
}
