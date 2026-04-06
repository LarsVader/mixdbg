using MixDbg.Models.Dap;

namespace MixDbg.Services.Handlers.Inspection;

/// <summary>
/// Handles the DAP evaluate request.
/// </summary>
public class EvaluateRequestHandlerService
    : DapHandlerServiceBase<EvaluateResponseBody, EvaluateArguments>
{
    public const string DapMessage = "evaluate";

    public override string Command => DapMessage;

    public override EvaluateResponseBody ExecuteInternal(EvaluateArguments args)
    {
		return new EvaluateResponseBody
		{
			Result = $"[not implemented] {args.Expression}",
			VariablesReference = 0,
		};
    }
}
