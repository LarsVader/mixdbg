using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP variables request by returning variables for a reference handle.
/// </summary>
public class VariablesRequestHandlerService(
        IEngineQueryService engineQuery,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<VariablesResponseBody, VariablesArguments>
{
    public const string DapMessage = "variables";

    public override string Command => DapMessage;

    public override VariablesResponseBody ExecuteInternal(VariablesArguments args)
    {
        if (sessionModel.Engine is not NativeDebuggerModel model)
            return new VariablesResponseBody { Variables = [] };

        Variable[] vars = model.QueueEngineQuery(
            () => engineQuery.GetVariablesOnEngine(model, args.VariablesReference));
        return new VariablesResponseBody { Variables = vars };
    }
}