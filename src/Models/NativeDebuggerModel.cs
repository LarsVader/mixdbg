using System.Collections.Concurrent;
using System.IO.Pipes;
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

    /// <summary>
    /// Maps managed breakpoint native addresses to their source file and line.
    /// Used for C++/CLI stack trace resolution where PdbSourceMapper can't read Windows PDBs.
    /// </summary>
    internal Dictionary<ulong, (string File, int Line)> ManagedBreakpointSources { get; } = new();

    // JIT profiler pipe — receives JIT compilation notifications from MixDbgProfiler.dll.
    internal NamedPipeServerStream? ProfilerPipe { get; set; }
    internal StreamReader? ProfilerPipeReader { get; set; }
    internal Thread? ProfilerReaderThread { get; set; }
    internal string? ProfilerPipeName { get; set; }
    internal ConcurrentQueue<JitNotification> JitNotifications { get; } = new();
    internal volatile bool ProfilerConnected;
    internal volatile bool ProfilerHooksActive; // True when profiler uses ENTER: notifications (enter/leave hooks).
    internal volatile bool PendingEnterBreakpoint; // True when an ENTER notification matched — treat next stop as breakpoint.
    internal uint EnterBreakpointThreadId; // OS thread ID of the thread frozen in the profiler's enter hook.
    internal int EnterBreakpointToken; // Method token of the method being entered.
    internal ulong EnterBreakpointAddress; // BP address (method body start, past hook preamble).
    internal string? EnterBreakpointAssembly; // Assembly name of the method being entered.

    /// <summary>
    /// Maps (assembly:token) to the code start address and IL-to-native offset mapping
    /// from the profiler's JIT notification. Used by ENTER hooks to compute the exact
    /// native address for a breakpointed line.
    /// </summary>
    internal Dictionary<string, JitMethodMapping> JitMethodMappings { get; } = new();
    internal EventWaitHandle? ProfilerAckEvent { get; set; }
    internal EventWaitHandle? ProfilerRehookEvent { get; set; }

    /// <summary>
    /// Sorted map of all JIT-compiled methods reported by the profiler, keyed by native
    /// code start address. Used for stack trace resolution: given an instruction pointer,
    /// binary search finds the containing method → token + assembly → PDB source info.
    /// Written by profiler reader thread, read by engine thread (under stop).
    /// </summary>
    internal SortedList<ulong, JitMethodInfo> JitMethodMap { get; } = new();

    // Variable inspection — invalidated on continue/step.
    internal VariableStore Variables { get; } = new();
    internal DEBUG_STACK_FRAME[] CachedStackFrames { get; set; } = [];

    // Signaled when the target is stopped and ready for commands.
    internal ManualResetEventSlim Stopped { get; } = new(false);

    /// <summary>
    /// Breakpoint file:line hints from DAP setBreakpoints (received before launch).
    /// Used by <see cref="Services.NativeDebuggerService.SetupProfilerPipe"/> to resolve
    /// assembly names so the profiler knows which assemblies to block on during JIT.
    /// </summary>
    internal List<(string FilePath, int Line)> ProfilerBreakpointHints { get; } = new();

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
internal record DeferredManagedBreakpoint(string FilePath, int Line, int MethodToken, int ILOffset, int BpId, string? AssemblyName, bool IsCliMethod = false);

/// <summary>
/// A JIT compilation notification received from the CLR profiler DLL via named pipe.
/// Contains the metadata token, native code start address, code size, and assembly name
/// of the freshly JIT-compiled method.
/// </summary>
internal record JitNotification(int MethodToken, ulong NativeAddress, uint CodeSize, string AssemblyName);

/// <summary>
/// IL-to-native offset mapping for a JIT'd method. Used to compute exact native
/// addresses for breakpoints at specific source lines.
/// </summary>
internal sealed class JitMethodMapping
{
    public required ulong CodeStart { get; init; }
    public required List<(int ILOffset, int NativeOffset)> ILToNativeMap { get; init; }

    /// <summary>
    /// Finds the native address for a given IL offset by searching the mapping.
    /// Returns the code start + native offset, or the code start if no match.
    /// </summary>
    public ulong GetNativeAddress(int ilOffset)
    {
        // Find the mapping entry with the largest IL offset ≤ the requested one.
        int bestNativeOffset = 0;
        foreach (var (il, native) in ILToNativeMap)
        {
            if (il <= ilOffset)
                bestNativeOffset = native;
        }
        return CodeStart + (ulong)bestNativeOffset;
    }
}

/// <summary>
/// Entry in the JIT method map. Stores enough information to resolve a native
/// instruction pointer to a managed method name and source location via PDB lookup.
/// </summary>
internal record JitMethodInfo(int MethodToken, ulong StartAddress, uint CodeSize, string AssemblyName);

/// <summary>
/// Tracks a loaded managed module discovered via ICorDebug enumeration.
/// </summary>
internal sealed class ManagedModule
{
    public required CorDebugModule Module { get; init; }
    public required string? Path { get; init; }
    public required string? PdbPath { get; init; }
}
