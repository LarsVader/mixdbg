using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Initialize;

/// <summary>
/// Handles the DAP initialize handshake, sends the initialized event, and returns capabilities.
/// </summary>
public class InitializeRequestHandlerService(
        IDapServer server,
        DapServerModel transport,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<Capabilities, InitializeRequestArguments>
{
    public const string DapMessage = "initialize";

    public override string Command => DapMessage;

    public override Capabilities ExecuteInternal(InitializeRequestArguments args)
    {
		sessionModel.State = SessionState.Initialized;
		server.SendEvent(transport, "initialized", new InitializedEventBody());

		return new Capabilities
		{
			SupportsConfigurationDoneRequest = true,
			SupportsFunctionBreakpoints = false,
			SupportsEvaluateForHovers = true,
			SupportsTerminateRequest = true,
		};
    }
}
