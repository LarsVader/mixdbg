using MixDbg.Models;
using MixDbg.Models.Dap;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP launch request by starting a new debug target process.
/// </summary>
public class LaunchRequestHandlerService(
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<LaunchRequestArguments>
{
    public const string DapMessage = "launch";

    public override string Command => DapMessage;

    public override void ExecuteInternal(LaunchRequestArguments args)
    {
        sessionModel.Engine = nativeDebugger.CreateModel();

        // Copy pending breakpoint file:line pairs so the profiler knows which
        // assemblies to block on during JIT (avoids blocking all 2000+ startup JITs).
        foreach (SetBreakpointsArguments pending in sessionModel.PendingBreakpoints)
        {
            if (pending.Source.Path != null)
            {
                foreach (SourceBreakpoint bp in pending.Breakpoints)
                    sessionModel.Engine.ProfilerBreakpointHints.Add((pending.Source.Path, bp.Line));
            }
        }

        string? symbolPath = null;
        if (args.SymbolPath is { Length: > 0 })
            symbolPath = string.Join(";", args.SymbolPath);


        sessionModel.Engine.IsAttach = false;
        sessionModel.Engine.LaunchProgram = args.Program;
        sessionModel.Engine.LaunchCwd = args.Cwd ?? Path.GetDirectoryName(args.Program);
        sessionModel.Engine.LaunchArgs = args.Args;
        sessionModel.Engine.SymbolPath = symbolPath;
        nativeDebugger.StartEngineThread(sessionModel.Engine);
        sessionModel.Engine.EngineReady.Wait();
        if (sessionModel.Engine.EngineInitError != null)
            throw sessionModel.Engine.EngineInitError;

        sessionModel.State = SessionState.Running;
    }
}