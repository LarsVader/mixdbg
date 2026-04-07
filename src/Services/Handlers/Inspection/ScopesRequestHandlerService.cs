using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP scopes request by returning locals/arguments for a stack frame.
/// </summary>
public class ScopesRequestHandlerService(
        IEngineQueryService engineQuery,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ScopesResponseBody, ScopesArguments>
{
    public const string DapMessage = "scopes";

    public override string Command => DapMessage;

    public override ScopesResponseBody ExecuteInternal(ScopesArguments args)
    {
        if (sessionModel.Engine is not NativeDebuggerModel model)
            return new ScopesResponseBody { Scopes = [] };

        Scope[] scopes = model.QueueEngineQuery(
            () => engineQuery.GetScopesOnEngine(model, args.FrameId));
        return new ScopesResponseBody { Scopes = scopes };
    }
}