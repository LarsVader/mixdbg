using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP scopes request by returning locals/arguments for a stack frame.
/// </summary>
public class ScopesRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ScopesResponseBody, ScopesArguments>
{
    public const string DapMessage = "scopes";

    public override string Command => DapMessage;

    public override ScopesResponseBody ExecuteInternal(ScopesArguments args)
    {
		if (sessionModel.Engine == null)
			return new ScopesResponseBody { Scopes = [] };

		return new ScopesResponseBody { Scopes = nativeDebugger.GetScopes(sessionModel.Engine, args.FrameId) };
    }
}
