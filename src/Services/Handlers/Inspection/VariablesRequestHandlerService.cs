using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP variables request by returning variables for a reference handle.
/// </summary>
public class VariablesRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<VariablesResponseBody, VariablesArguments>
{
    public const string DapMessage = "variables";

    public override string Command => DapMessage;

    public override VariablesResponseBody ExecuteInternal(VariablesArguments args)
    {
		if (sessionModel.Engine == null)
			return new VariablesResponseBody { Variables = [] };

		return new VariablesResponseBody { Variables = nativeDebugger.GetVariables(sessionModel.Engine, args.VariablesReference) };
    }
}
