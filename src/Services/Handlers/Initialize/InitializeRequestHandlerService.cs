using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Initialize;

public class InitializeRequestHandlerService(
        IDebugSession session,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<Capabilities, InitializeRequestArguments>
{
    public const string DapMessage = "initialize";

    public override string Command => DapMessage;

    public override Capabilities ExecuteInternal(InitializeRequestArguments args)
    {
		return session.Initialize(sessionModel, args);
    }
}
