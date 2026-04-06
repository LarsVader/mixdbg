using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP attach request by attaching to an existing process.
/// </summary>
public class AttachRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<AttachRequestArguments>
{
    public const string DapMessage = "attach";

    public override string Command => DapMessage;

    public override void ExecuteInternal(AttachRequestArguments args)
    {
		session.Attach(sessionModel, args);
    }
}
