using MixDbg.Dap;

namespace MixDbg.Services.Handlers.Breakpoints;

/// <summary>
/// Handles the DAP setFunctionBreakpoints request.
/// </summary>
public class SetFunctionBreakpointsHandlerService
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "setFunctionBreakpoints";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
		// Silently accepted — not yet implemented.
    }
}
