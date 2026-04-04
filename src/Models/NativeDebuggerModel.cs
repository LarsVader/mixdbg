using System.Collections.Concurrent;
using ClrDebug;
using MixDbg.Dap;
using MixDbg.Engine.DbgEng;

namespace MixDbg.Models;

/// <summary>
/// Mutable state for the native debug engine. Holds dbgeng COM interface
/// references (thread-affine to the engine thread), volatile flags for
/// cross-thread signaling, a command queue for marshaling DAP handler
/// calls to the engine thread, breakpoint tracking state, and ICorDebug V4
/// references for managed debugging piggybacked on the dbgeng session.
/// Dispose tears down the engine thread and releases all resources.
/// </summary>
public sealed class NativeDebuggerModel : IDisposable
{
    // COM interfaces — set during engine initialization on the engine thread.
    internal IDebugClient Client { get; set; } = null!;
    internal IDebugControl Control { get; set; } = null!;
    internal IDebugSymbols Symbols { get; set; } = null!;
    internal IDebugSystemObjects SysObjects { get; set; } = null!;
    internal IDebugDataSpaces DataSpaces { get; set; } = null!;
    internal IDebugAdvanced Advanced { get; set; } = null!;
    internal EventCallbacks Callbacks { get; set; } = null!;

    // Engine thread lifecycle.
    internal Thread? EngineThread { get; set; }
    internal volatile bool Terminated;
    internal volatile bool TargetExited;
    internal volatile bool ConfigDone;
    internal volatile bool Stepping;
    internal volatile bool PauseRequested;

    // Native breakpoint tracking.
    internal HashSet<uint> UserBreakpointIds { get; } = new();
    internal uint LastHitBpId;
    internal volatile bool HitUserBreakpoint;
    internal Dictionary<string, uint> BreakpointIds { get; } = new();
    internal int NextBpId;

    // Command queue: main thread queues, engine thread executes.
    internal BlockingCollection<Action> Commands { get; } = new();

    // Managed breakpoint tracking (ICorDebug V4).
    internal HashSet<uint> ManagedBreakpointIds { get; } = new();
    internal List<SetBreakpointsArguments> PendingManagedBreakpoints { get; } = new();

    // ICorDebug V4 state — piggybacked on the dbgeng session via OpenVirtualProcess.
    internal volatile bool ClrLoaded;
    internal volatile bool ManagedInitialized;
    internal volatile bool SosLoaded;
    internal ClrDebug.SOSDacInterface? SosDac { get; set; }
    internal ClrDebug.XCLRDataProcess? XclrProcess { get; set; }
    internal string? CoreClrPath { get; set; }
    internal ulong CoreClrBaseAddress { get; set; }
    internal CorDebugProcess? CorProcess { get; set; }
    internal Dictionary<long, ManagedModule> CorModules { get; } = new();
    internal Dictionary<int, CorDebugFunctionBreakpoint> CorManagedBreakpoints { get; } = new();
    internal Dictionary<string, List<int>> ManagedFileBreakpointIds { get; } = new();
    internal List<PendingManagedBreakpoint> PendingILBreakpoints { get; } = new();

    /// <summary>
    /// Managed breakpoints waiting for JIT compilation. Resolved when CLR notification
    /// exceptions (0xe0444143) fire and <c>GetFunctionFromToken(token).NativeCode</c>
    /// becomes available.
    /// </summary>
    internal List<DeferredManagedBreakpoint> DeferredManagedBreakpoints { get; } = new();

    /// <summary>
    /// Native addresses of active managed breakpoints (from ICorDebug IL breakpoints).
    /// Used to identify managed breakpoint hits from dbgeng EXCEPTION_BREAKPOINT events.
    /// </summary>
    internal HashSet<ulong> ManagedBreakpointAddresses { get; } = new();

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

/// <summary>
/// A managed breakpoint waiting for its module to load via ICorDebug.
/// </summary>
internal record PendingManagedBreakpoint(string FilePath, int Line, int BpId);

/// <summary>
/// A managed breakpoint waiting for JIT compilation. Stored when PDB resolution
/// succeeds but <c>GetFunctionFromToken(token).NativeCode</c> is null (method not
/// yet JIT-compiled). Resolved on CLR notification exceptions (0xe0444143) which
/// fire during JIT compilation.
/// </summary>
internal record DeferredManagedBreakpoint(string FilePath, int Line, int MethodToken, int ILOffset, int BpId, string? AssemblyName);

/// <summary>
/// Tracks a loaded managed module discovered via ICorDebug enumeration.
/// </summary>
internal sealed class ManagedModule
{
    public required CorDebugModule Module { get; init; }
    public required string? Path { get; init; }
    public required string? PdbPath { get; init; }
}
