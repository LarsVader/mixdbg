using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP configurationDone request by applying pending breakpoints and resuming.
/// </summary>
public class ConfigurationDoneHandlerService(
        ILoggingService log,
        LogStore logStore,
        IBreakpointService breakpointService,
        IEngineQueryService engineQuery,
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

        if (sessionModel.Engine is NativeDebuggerModel model)
        {
            // Apply breakpoints that arrived before launch.
            foreach (SetBreakpointsArguments pending in sessionModel.PendingBreakpoints)
            {
                Breakpoint[] bps = model.QueueEngineQuery(
                    () => breakpointService.SetBreakpointsOnEngine(model, pending.Source.Path!, pending.Breakpoints));
                foreach (Breakpoint? bp in bps)
                {
                    server.SendEvent(transport, "breakpoint", new BreakpointEventBody
                    {
                        Reason = "changed",
                        Breakpoint = bp,
                    });
                }
            }
            sessionModel.PendingBreakpoints.Clear();

            log.LogInfo(logStore, "Continue queued");
            model.CachedStackTraceResult = null;
            model.Commands.Add(() => engineQuery.ExecuteContinueOnEngine(model));
            sessionModel.State = SessionState.Running;
        }
    }
}