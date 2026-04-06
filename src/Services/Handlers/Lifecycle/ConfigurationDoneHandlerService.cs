using MixDbg.Models.Dap;
using MixDbg.Models;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP configurationDone request by applying pending breakpoints and resuming.
/// </summary>
public class ConfigurationDoneHandlerService(
        INativeDebugger nativeDebugger,
        IDapServer server,
        DapServerModel transport,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<EmptyArguments>
{
    public const string DapMessage = "configurationDone";

    public override string Command => DapMessage;

    public override void ExecuteInternal(EmptyArguments args)
    {
		sessionModel.State = SessionState.Configured;

		if (sessionModel.Engine != null)
		{
			// Apply breakpoints that arrived before launch.
			foreach (var pending in sessionModel.PendingBreakpoints)
			{
				var bps = nativeDebugger.SetBreakpoints(
					sessionModel.Engine, pending.Source.Path!, pending.Breakpoints);
				foreach (var bp in bps)
				{
					server.SendEvent(transport, "breakpoint", new BreakpointEventBody
					{
						Reason = "changed",
						Breakpoint = bp,
					});
				}
			}
			sessionModel.PendingBreakpoints.Clear();

			nativeDebugger.Continue(sessionModel.Engine);
			sessionModel.State = SessionState.Running;
		}
    }
}
