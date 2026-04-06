using MixDbg.Models.Dap;

namespace MixDbg.Services.Handlers.Breakpoints;

/// <summary>
/// Handles the DAP setExceptionBreakpoints request.
/// </summary>
public class SetExceptionBreakpointsHandlerService
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "setExceptionBreakpoints";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
        // Silently accepted — not yet implemented.
    }
}