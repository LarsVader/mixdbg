using System.Collections.Concurrent;
using ClrDebug;
using MixDbg.Models.Dap;
using MixDbg.Engine.Clr;

namespace MixDbg.Models;

/// <summary>
/// Mutable state for the ICorDebug-based debug engine. Holds ICorDebug interface
/// references, callback handlers, breakpoint tracking, module/symbol state, and
/// the command queue for marshaling DAP handler calls to the callback thread.
/// </summary>
public sealed class CorDebugEngineModel : IDisposable
{
    // ICorDebug instances — set during launch/attach.
    internal ClrDebug.CorDebug? CorDebug { get; set; }
    internal CorDebugProcess? Process { get; set; }
    internal ManagedCallbackHandler? ManagedCallbacks { get; set; }
    internal int ProcessId;

    // Lifecycle flags.
    internal volatile bool Terminated;
    internal volatile bool TargetExited;
    internal volatile bool ConfigDone;
    internal volatile bool Stepping;
    internal volatile bool PauseRequested;

    // Breakpoint tracking: DAP breakpoint ID → ICorDebug breakpoint.
    internal Dictionary<int, CorDebugFunctionBreakpoint> ManagedBreakpoints { get; } = new();

    /// <summary>File path → list of DAP breakpoint IDs for that file.</summary>
    internal Dictionary<string, List<int>> FileBreakpointIds { get; } = new();

    /// <summary>
    /// Breakpoints waiting for their module to load. Key is lowercase source file path.
    /// </summary>
    internal List<PendingBreakpoint> PendingBreakpoints { get; } = new();

    internal int NextBpId;

    // Module tracking: module base address → loaded module info.
    internal Dictionary<long, LoadedModule> Modules { get; } = new();

    // Thread state.
    internal CorDebugThread? StoppedThread;
    internal CorDebugAppDomain? StoppedAppDomain;
    internal volatile bool HitUserBreakpoint;
    internal int LastHitBpId;

    // Command queue: DAP handlers queue, callback thread executes.
    internal BlockingCollection<Action> Commands { get; } = new();

    // Signaled when the target is stopped and ready for commands.
    internal ManualResetEventSlim Stopped { get; } = new(false);

    // Launch/attach parameters — stored by handler, consumed by engine.
    internal string? LaunchProgram;
    internal string? LaunchCwd;
    internal string[]? LaunchArgs;
    internal uint AttachPid;
    internal bool IsAttach;
    internal ManualResetEventSlim EngineReady { get; } = new(false);
    internal Exception? EngineInitError;

    internal Action? DisposeAction { get; set; }

    public bool IsTargetStopped => Stopped.IsSet;

    public void Dispose() => DisposeAction?.Invoke();
}

/// <summary>
/// A managed breakpoint waiting for its module to load.
/// </summary>
internal record PendingBreakpoint(string FilePath, int Line, int BpId);

/// <summary>
/// Tracks a loaded managed module and its symbol reader (PDB).
/// </summary>
internal sealed class LoadedModule
{
    public required CorDebugModule Module { get; init; }
    public required string? Path { get; init; }
    public required string? PdbPath { get; init; }
}
