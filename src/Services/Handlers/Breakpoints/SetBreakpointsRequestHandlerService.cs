using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Breakpoints;

/// <summary>
/// Handles the DAP setBreakpoints request. Queues breakpoints as pending if the engine
/// is not ready, otherwise delegates to the native debugger on the engine thread.
/// </summary>
public class SetBreakpointsRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<SetBreakpointsResponseBody, SetBreakpointsArguments>
{
    public const string DapMessage = "setBreakpoints";

    public override string Command => DapMessage;

    public override SetBreakpointsResponseBody ExecuteInternal(SetBreakpointsArguments args)
    {
        if (sessionModel.Engine is not NativeDebuggerModel model || args.Source.Path == null)
        {
            if (args.Source.Path != null)
                sessionModel.PendingBreakpoints.Add(args);

            return new SetBreakpointsResponseBody
            {
                Breakpoints = [.. args.Breakpoints.Select((bp, i) => new Breakpoint
                {
                    Id = sessionModel.NextPendingBpId++,
                    Verified = true,
                    Line = bp.Line,
                    Source = args.Source,
                })],
            };
        }

        Breakpoint[] bps = model.QueueEngineQuery(
            () => nativeDebugger.SetBreakpointsOnEngine(model, args.Source.Path, args.Breakpoints));
        return new SetBreakpointsResponseBody { Breakpoints = bps };
    }
}