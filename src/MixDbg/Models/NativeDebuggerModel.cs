using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using MixDbg.Dap;
using MixDbg.Engine.DbgEng;

namespace MixDbg.Models;

/// <summary>
/// Mutable state for the native debug engine. Holds dbgeng COM interface
/// references (thread-affine to the engine thread), volatile flags for
/// cross-thread signaling, a command queue for marshaling DAP handler
/// calls to the engine thread, and breakpoint tracking state.
/// Dispose tears down the engine thread and releases all resources.
/// </summary>
public sealed class NativeDebuggerModel : IDisposable
{
    // COM interfaces — set during engine initialization on the engine thread.
    internal IDebugClient Client { get; set; } = null!;
    internal IDebugControl Control { get; set; } = null!;
    internal IDebugSymbols Symbols { get; set; } = null!;
    internal IDebugSystemObjects SysObjects { get; set; } = null!;
    internal EventCallbacks Callbacks { get; set; } = null!;

    // Engine thread lifecycle.
    internal Thread? EngineThread { get; set; }
    internal volatile bool Terminated;
    internal volatile bool TargetExited;
    internal volatile bool ConfigDone;
    internal volatile bool Stepping;
    internal volatile bool PauseRequested;

    // Breakpoint tracking.
    internal HashSet<uint> UserBreakpointIds { get; } = new();
    internal uint LastHitBpId;
    internal volatile bool HitUserBreakpoint;
    internal Dictionary<string, uint> BreakpointIds { get; } = new();
    internal int NextBpId;

    // Command queue: main thread queues, engine thread executes.
    internal BlockingCollection<Action> Commands { get; } = new();

    // Managed breakpoint tracking.
    internal HashSet<uint> ManagedBreakpointIds { get; } = new();
    internal List<SetBreakpointsArguments> PendingManagedBreakpoints { get; } = new();
    internal List<DeferredManagedBreakpoint> DeferredManagedBreakpoints { get; } = new();
    internal int DeferredResolutionFailures;

    // CLR / managed debugging state — set on the engine thread.
    internal volatile bool ClrLoaded;
    internal volatile bool ManagedInitialized;
    internal DataTarget? DataTarget { get; set; }
    internal ClrRuntime? Runtime { get; set; }
    /// <summary>The original runtime created during InitializeRuntime — stable for stack traces.</summary>
    internal ClrRuntime? OriginalRuntime { get; set; }

    // Variable inspection — invalidated on continue/step.
    internal VariableStore Variables { get; } = new();
    internal DEBUG_STACK_FRAME[] CachedStackFrames { get; set; } = [];

    // Signaled when the target is stopped and ready for commands.
    internal ManualResetEventSlim Stopped { get; } = new(false);

    // Saved launch/attach parameters — actual work happens on engine thread.
    internal string? LaunchProgram;
    internal string? LaunchCwd;
    internal string[]? LaunchArgs;
    internal uint AttachPid;
    internal string? SymbolPath;
    internal bool IsAttach;
    internal ManualResetEventSlim EngineReady { get; } = new(false);
    internal Exception? EngineInitError;

    internal Action? DisposeAction { get; set; }

    public bool IsTargetStopped => Stopped.IsSet;

    public void Dispose() => DisposeAction?.Invoke();
}
