using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP variables request by returning variables for a reference handle.
/// </summary>
public class VariablesRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<VariablesResponseBody, VariablesArguments>
{
    public const string DapMessage = "variables";

    public override string Command => DapMessage;

    public override VariablesResponseBody ExecuteInternal(VariablesArguments args)
    {
		return session.GetVariables(sessionModel, args);
    }
}
