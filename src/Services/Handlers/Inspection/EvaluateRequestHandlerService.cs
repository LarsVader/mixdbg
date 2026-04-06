using MixDbg.Models.DapMessages.Inspection;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP evaluate request.
/// </summary>
public class EvaluateRequestHandlerService
    : DapHandlerServiceBase<EvaluateResponseBody, EvaluateArguments>
{
    public const string DapMessage = "evaluate";

    public override string Command => DapMessage;

    public override EvaluateResponseBody ExecuteInternal(EvaluateArguments args) => new()
    {
        Result = $"[not implemented] {args.Expression}",
        VariablesReference = 0,
    };
}