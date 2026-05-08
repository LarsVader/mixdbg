using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Lifecycle;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP attach request by attaching to an existing process.
/// </summary>
public class AttachRequestHandlerService(
        IEngineLifecycleService nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<AttachRequestArguments>
{
    public const string DapMessage = "attach";

    public override string Command => DapMessage;

    public override void ExecuteInternal(AttachRequestArguments args)
    {
        // Validate before allocating the engine model — otherwise a missing
        // PID throws after CreateModel and the model leaks (BlockingCollection,
        // ManualResetEventSlim, queued env-var-clear DisposeAction).
        if (!args.Pid.HasValue)
            throw new InvalidOperationException("PID is required for attach");
        if (args.Pid.Value <= 0)
        {
            throw new InvalidOperationException(
                $"PID must be positive (got {args.Pid.Value}); a negative or zero value would silently " +
                $"coerce to a huge unsigned PID and surface as a generic dbgeng AttachProcess failure.");
        }

        NativeDebuggerModel debuggerModel = nativeDebugger.CreateModel();
        sessionModel.Engine = debuggerModel;

        // Mirror the launch handler: copy pending breakpoint file:line pairs
        // into ProfilerBreakpointHints so the profiler watch-token list is
        // seeded with the user's actual targets. Without this, the attach
        // path always sends an empty watch list and the eager-HW-BP install
        // races every JIT instead of blocking only on watched ones.
        foreach (SetBreakpointsArguments pending in sessionModel.PendingBreakpoints)
        {
            if (pending.Source.Path != null)
            {
                foreach (SourceBreakpoint bp in pending.Breakpoints)
                    debuggerModel.ProfilerBreakpointHints.Add((pending.Source.Path, bp.Line));
            }
        }

        debuggerModel.IsAttach = true;
        debuggerModel.AttachPid = (uint)args.Pid.Value;
        debuggerModel.SymbolPath = args.SymbolPath is { Length: > 0 }
            ? string.Join(';', args.SymbolPath)
            : null;
        nativeDebugger.StartEngineThread(debuggerModel);
        debuggerModel.EngineReady.Wait();
        if (debuggerModel.EngineInitError != null)
            throw debuggerModel.EngineInitError;

        sessionModel.State = SessionState.Running;
    }
}