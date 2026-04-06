using MixDbg.Dap;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP loadedSources request.
/// </summary>
public class LoadedSourcesRequestHandlerService
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "loadedSources";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
		// Silently accepted — not yet implemented.
    }
}
