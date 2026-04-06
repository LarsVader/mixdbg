using MixDbg.Models.DapMessages.Protocol;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP source request.
/// </summary>
public class SourceRequestHandlerService
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "source";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
        // Silently accepted — not yet implemented.
    }
}