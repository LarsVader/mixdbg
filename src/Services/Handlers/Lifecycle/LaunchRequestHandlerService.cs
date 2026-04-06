using MixDbg.Models.Dap;
using MixDbg.Models;

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
		foreach (var pending in sessionModel.PendingBreakpoints)
		{
			if (pending.Source.Path != null)
			{
				foreach (var bp in pending.Breakpoints)
					sessionModel.Engine.ProfilerBreakpointHints.Add((pending.Source.Path, bp.Line));
			}
		}

		string? symbolPath = null;
		if (args.SymbolPath is { Length: > 0 })
			symbolPath = string.Join(";", args.SymbolPath);

		nativeDebugger.Launch(
			sessionModel.Engine,
			args.Program,
			args.Cwd ?? Path.GetDirectoryName(args.Program),
			symbolPath,
			args.Args);
		sessionModel.State = SessionState.Running;
    }
}
