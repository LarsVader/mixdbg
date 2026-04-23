using System.Collections.Concurrent;
using System.IO.Pipes;

using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Inspection;

namespace MixDbg.Models;

/// <summary>
/// Mutable state for the native debug engine. Holds the dbgeng and ICorDebug
/// wrapper models (which encapsulate COM interfaces), volatile flags for
/// cross-thread signaling, a command queue for marshaling DAP handler calls
/// to the engine thread, and breakpoint tracking state.
/// Dispose tears down the engine thread and releases all resources.
/// </summary>
public sealed class NativeDebuggerModel : IDisposable
{
    /// <summary>
    /// The dbgeng COM wrapper model. Initialized during engine startup.
    /// All dbgeng COM interaction goes through <see cref="Services.IDbgEngWrapper"/>
    /// methods that take this model.
    /// </summary>
    public DbgEngWrapperModel Wrapper { get; set; } = null!;

    /// <summary>
    /// The ICorDebug V4 wrapper model. Initialized when CLR is detected.
    /// All ICorDebug interaction goes through <see cref="Services.ICorDebugWrapper"/>
    /// methods that take this model.
    /// </summary>
    public CorDebugWrapperModel CorWrapper { get; set; } = null!;

    // Engine thread lifecycle.
    internal Thread? EngineThread { get; set; }
    internal volatile bool Terminated;
    internal volatile bool TargetExited;
    internal volatile bool ConfigDone;
    internal volatile bool Stepping;
    internal volatile bool PauseRequested;
    internal volatile bool InWaitForEvent;

    // Native breakpoint tracking.
    internal HashSet<uint> UserBreakpointIds { get; } = [];
    internal uint LastHitBpId = uint.MaxValue;
    internal uint LastContinuedBpId = uint.MaxValue;
    internal long ContinueTimestampTicks;
    internal volatile bool HitUserBreakpoint;
    internal Dictionary<string, uint> BreakpointIds { get; } = [];
    internal int NextBpId;

    // Command queue: main thread queues, engine thread executes.
    internal BlockingCollection<Action> Commands { get; } = [];

    // Managed breakpoint tracking (ICorDebug V4).
    internal HashSet<uint> ManagedBreakpointIds { get; } = [];

    internal List<SetBreakpointsArguments> PendingManagedBreakpoints { get; } = [];

    /// <summary>
    /// Per-method breakpoint plan keyed by (token, assembly). Created in
    /// <see cref="Services.ManagedBreakpointService.SetManagedBreakpoints"/> when
    /// the user sets breakpoints. Drives HW BP installation on FunctionEnter:
    /// when a method transitions from 0→1 activations, every site in its plan
    /// becomes an active HW BP; on the final LEAVE (count→0), those HW BPs are
    /// removed. Sites persist across continue/step; entries are removed only by
    /// <see cref="Services.ManagedBreakpointService.ClearManagedBreakpointsForFile"/>.
    /// </summary>
    internal Dictionary<(int Token, string Assembly), ManagedMethodBreakpointPlan> ManagedBpPlans { get; } =
        new(DeferredBreakpointKeyComparer.Instance);

    /// <summary>
    /// Runtime activation tracking keyed by (token, assembly). An entry exists for
    /// every method currently on the stack with at least one activation. Tracks
    /// the activation count (incremented on ENTER, decremented on LEAVE/TAILCALL)
    /// and the installed HW BP IDs so they can be removed when the count reaches 0.
    /// </summary>
    internal Dictionary<(int Token, string Assembly), ActiveMethodBreakpoint> ActiveMethodBreakpoints { get; } =
        new(DeferredBreakpointKeyComparer.Instance);

    /// <summary>
    /// Unified queue of notifications received from the profiler pipe
    /// (JIT, ENTER, LEAVE, TAILCALL). Written by the profiler reader thread,
    /// drained by the engine thread via
    /// <see cref="Services.IManagedBreakpointResolver.ProcessProfilerNotifications"/>.
    /// </summary>
    internal ConcurrentQueue<ProfilerNotification> ProfilerNotifications { get; } = new();

    // SOS extension state.
    internal volatile bool SosLoaded;

    // CLR detection state.
    internal volatile bool ClrLoaded;
    internal volatile bool ManagedInitialized;
    internal string? CoreClrPath { get; set; }
    internal ulong CoreClrBaseAddress { get; set; }
    internal Dictionary<string, List<int>> ManagedFileBreakpointIds { get; } = [];
    internal List<PendingManagedBreakpoint> PendingILBreakpoints { get; } = [];

    /// <summary>
    /// Managed breakpoints waiting for JIT compilation. Resolved when CLR notification
    /// exceptions (0xe0444143) fire and <c>GetFunctionFromToken(token).NativeCode</c>
    /// becomes available.
    /// </summary>
    internal List<DeferredManagedBreakpoint> DeferredManagedBreakpoints { get; } = [];

    /// <summary>
    /// Fast lookup index for <see cref="DeferredManagedBreakpoints"/>. Contains
    /// (MethodToken, AssemblyName) pairs for O(1) matching on profiler JIT notifications.
    /// Must be rebuilt via <see cref="RebuildDeferredBreakpointIndex"/> after any mutation
    /// of the deferred list.
    /// </summary>
    internal HashSet<(int Token, string Assembly)> DeferredBreakpointIndex { get; } =
        new(DeferredBreakpointKeyComparer.Instance);

    /// <summary>
    /// Rebuilds <see cref="DeferredBreakpointIndex"/> from <see cref="DeferredManagedBreakpoints"/>.
    /// Call after any Add/Remove/RemoveAll on the deferred list.
    /// </summary>
    internal void RebuildDeferredBreakpointIndex()
    {
        DeferredBreakpointIndex.Clear();
        foreach (DeferredManagedBreakpoint d in DeferredManagedBreakpoints)
        {
            if (d.AssemblyName != null)
                _ = DeferredBreakpointIndex.Add((d.MethodToken, d.AssemblyName));
        }
    }

    /// <summary>
    /// Native addresses of active managed breakpoints (from ICorDebug IL breakpoints).
    /// Used to identify managed breakpoint hits from dbgeng EXCEPTION_BREAKPOINT events.
    /// </summary>
    internal HashSet<ulong> ManagedBreakpointAddresses { get; } = [];

    /// <summary>
    /// Maps managed breakpoint native addresses to their source file and line.
    /// Used for C++/CLI stack trace resolution where PdbSourceMapper can't read Windows PDBs.
    /// </summary>
    internal Dictionary<ulong, (string File, int Line)> ManagedBreakpointSources { get; } = [];

    // JIT profiler pipe — receives JIT compilation notifications from MixDbgProfiler.dll.
    internal NamedPipeServerStream? ProfilerPipe { get; set; }
    internal StreamReader? ProfilerPipeReader { get; set; }
    internal Thread? ProfilerReaderThread { get; set; }
    internal string? ProfilerPipeName { get; set; }

    // Command pipe — sends WATCH commands to the profiler for mid-session breakpoints.
    internal NamedPipeServerStream? ProfilerCmdPipe { get; set; }
    internal StreamWriter? ProfilerCmdPipeWriter { get; set; }

    /// <summary>
    /// Pending WATCH lines queued while the command pipe was not yet connected.
    /// Drained by the cmd pipe connect thread once the profiler connects.
    /// </summary>
    internal ConcurrentQueue<string> PendingWatchCommands { get; } = new();
    internal volatile bool ProfilerConnected;
    internal volatile bool ProfilerHooksActive; // True when profiler uses ENTER: notifications (enter/leave hooks).

    /// <summary>
    /// Maps (assembly:token) to the code start address and IL-to-native offset mapping
    /// from the profiler's JIT notification. Used by ENTER hooks to compute the exact
    /// native address for a breakpointed line.
    /// </summary>
    internal Dictionary<(int Token, string Assembly), JitMethodMapping> JitMethodMappings { get; } =
        new(DeferredBreakpointKeyComparer.Instance);
    internal EventWaitHandle? ProfilerAckEvent { get; set; }

    /// <summary>
    /// Map of all JIT-compiled methods reported by the profiler, keyed by native code
    /// start address. Written by profiler reader thread (O(1) insert), read by engine
    /// thread (under stop). <see cref="JitMethodMapSnapshot"/> is a sorted array built
    /// lazily for binary search during stack trace resolution.
    /// </summary>
    internal Dictionary<ulong, JitMethodInfo> JitMethodMap { get; } = [];

    /// <summary>
    /// Secondary index into <see cref="JitMethodMap"/> keyed by (MethodToken, AssemblyName).
    /// Provides O(1) lookup by token+assembly, replacing O(n) scans of JitMethodMap.Values.
    /// Populated alongside JitMethodMap under the same lock.
    /// </summary>
    internal Dictionary<(int Token, string Assembly), JitMethodInfo> JitMethodMapByToken { get; } =
        new(DeferredBreakpointKeyComparer.Instance);

    /// <summary>
    /// Sorted snapshot of <see cref="JitMethodMap"/> for binary search. Invalidated
    /// (set to null) whenever a new entry is added to JitMethodMap. Rebuilt lazily by
    /// <see cref="ManagedDebuggerService.FindContainingMethod"/>.
    /// </summary>
    internal (ulong Key, JitMethodInfo Value)[]? JitMethodMapSnapshot;

    // Managed step state — tracks active managed step operation with temp BPs.
    internal ManagedStepState? ActiveManagedStep;

    // Set by managed step-into when the tight loop completes inside ProcessCommandsUntilResume.
    // Checked by ProcessCommandsUntilResume to send the stopped event without exiting.
    internal volatile bool ManagedStepIntoCompleted;

    // Tracks the source file and line before a step operation, so the event loop
    // can detect "no progress" (same line) or "closing brace" stops and auto-step-out.
    internal (string File, int Line)? StepOriginLocation;

    /// <summary>
    /// Stack pointer (RSP) of frame[0] when a step started. A recursive call has a
    /// lower stack pointer (stack grows downward on x86-64). Zero means no check.
    /// </summary>
    internal ulong StepOriginStackPointer;

    // Per-stop caches — invalidated on continue/step.
    internal StackFrame[]? CachedStackTraceResult { get; set; }
    internal DapMessages.Threads.DapThread[]? CachedThreadsResult { get; set; }

    /// <summary>
    /// Caches source file lines for <c>CheckStepLanding</c> to avoid
    /// re-reading entire files from disk on every step.
    /// </summary>
    internal Dictionary<string, string[]> SourceFileCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Signaled when the target is stopped and ready for commands.
    internal ManualResetEventSlim Stopped { get; } = new(false);

    /// <summary>
    /// Breakpoint file:line hints from DAP setBreakpoints (received before launch).
    /// Used by <see cref="Services.ProfilerPipeService.SetupProfilerPipe"/> to resolve
    /// assembly names so the profiler knows which assemblies to block on during JIT.
    /// </summary>
    internal List<(string FilePath, int Line)> ProfilerBreakpointHints { get; } = [];

    // Saved launch/attach parameters — actual work happens on engine thread.
    internal string? LaunchProgram;
    internal string? LaunchCwd;
    internal string[]? LaunchArgs;
    internal uint AttachPid;
    internal string? SymbolPath;
    internal bool IsAttach;
    internal ManualResetEventSlim EngineReady { get; } = new(false);
    internal Exception? EngineInitError;

    /// <summary>
    /// Calls SetInterrupt via IDbgEngWrapper. Set by EngineLifecycleService during
    /// engine creation. Used by <see cref="QueueEngineQuery{T}"/> to wake the engine
    /// when it's in WaitForEvent. Same cross-thread pattern as
    /// <see cref="Services.ProfilerPipeService.RequestInterrupt"/>.
    /// </summary>
    internal Action? InterruptAction { get; set; }

    internal Action? DisposeAction { get; set; }

    public bool IsTargetStopped => Stopped.IsSet;

    /// <summary>
    /// Queues a query on the engine thread and blocks until it completes.
    /// Used by handlers to marshal calls that return a result to the engine thread.
    /// When the engine is in WaitForEvent (GO state), sets InterruptRequested so the
    /// engine wakes up and processes the command promptly instead of waiting for the
    /// next debug event.
    /// </summary>
    public T QueueEngineQuery<T>(Func<T> engineCall)
    {
        TaskCompletionSource<T> tcs = new();
        Commands.Add(() =>
        {
            try { tcs.SetResult(engineCall()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        // Wake the engine if it's in WaitForEvent so the command is processed promptly.
        // Same cross-thread SetInterrupt pattern used by ProfilerPipeService.RequestInterrupt.
        if (InWaitForEvent)
        {
            try { InterruptAction?.Invoke(); } catch { }
        }

        return tcs.Task.Result;
    }

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
/// Base type for notifications received from the CLR profiler DLL over the named pipe.
/// Dispatched on the engine thread via
/// <see cref="Services.IManagedBreakpointResolver.ProcessProfilerNotifications"/>.
/// </summary>
internal abstract record ProfilerNotification;

/// <summary>
/// A JIT compilation notification received from the CLR profiler DLL via named pipe.
/// Contains the metadata token, native code start address, code size, and assembly name
/// of the freshly JIT-compiled method.
/// </summary>
internal sealed record JitNotification(int MethodToken, ulong NativeAddress, uint CodeSize, string AssemblyName)
    : ProfilerNotification;

/// <summary>
/// A FunctionEnter notification for a watched method. The profiler blocks on the ACK
/// event after sending this until MixDbg has handled it (either installed HW BPs on
/// first entry or acknowledged a nested/recursive entry).
/// </summary>
internal sealed record EnterNotification(int MethodToken, ulong BodyAddress, uint ThreadId, string AssemblyName)
    : ProfilerNotification;

/// <summary>
/// A FunctionLeave notification for a watched method. Fire-and-forget from the
/// profiler's perspective; MixDbg decrements activation count and, when it reaches 0,
/// removes all HW BPs for that method.
/// </summary>
internal sealed record LeaveNotification(int MethodToken, uint ThreadId, string AssemblyName)
    : ProfilerNotification;

/// <summary>
/// A FunctionTailcall notification. Semantically identical to a LEAVE — the current
/// activation is ending (control is being transferred to the tailcall target without
/// returning through the current frame).
/// </summary>
internal sealed record TailcallNotification(int MethodToken, uint ThreadId, string AssemblyName)
    : ProfilerNotification;

/// <summary>
/// Declarative breakpoint plan for a single managed method (keyed by token + assembly).
/// Holds one or more <see cref="MethodBreakpointSite"/>s. While at least one activation
/// of the method is on the stack, each site becomes an installed hardware BP.
/// </summary>
internal sealed class ManagedMethodBreakpointPlan
{
    public required int MethodToken { get; init; }
    public required string AssemblyName { get; init; }
    public List<MethodBreakpointSite> Sites { get; } = [];
}

/// <summary>
/// A single breakpoint location inside a method. <see cref="IsStepIntoOneShot"/> marks
/// temporary step-into sites that are removed on first hit instead of on LEAVE.
/// </summary>
internal sealed class MethodBreakpointSite
{
    public required int BpId { get; init; }
    public required int ILOffset { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public bool IsStepIntoOneShot { get; init; }
}

/// <summary>
/// Runtime activation tracker for a method currently on the stack. Created on the
/// first FunctionEnter (count 0→1), removed on the final LEAVE/TAILCALL (count→0).
/// </summary>
internal sealed class ActiveMethodBreakpoint
{
    public int ActivationCount { get; set; }
    public List<uint> InstalledBpIds { get; } = [];
    public HashSet<ulong> InstalledAddresses { get; } = [];
}

/// <summary>
/// IL-to-native offset mapping for a JIT'd method. Used to compute exact native
/// addresses for breakpoints at specific source lines. Entries are sorted at
/// construction time for O(log n) binary search lookups.
/// </summary>
internal sealed class JitMethodMapping(ulong codeStart, List<(int ILOffset, int NativeOffset)> entries)
{
    public ulong CodeStart { get; } = codeStart;

    /// <summary>Entries sorted by IL offset for IL→native lookups.</summary>
    public (int ILOffset, int NativeOffset)[] ILToNativeMap { get; } = [.. entries.OrderBy(e => e.ILOffset)];

    /// <summary>Entries sorted by native offset for native→IL lookups.</summary>
    private readonly (int ILOffset, int NativeOffset)[] _byNative = [.. entries.OrderBy(e => e.NativeOffset)];

    /// <summary>
    /// Finds the native address for a given IL offset using binary search.
    /// Returns the code start + native offset, or the code start if no match.
    /// </summary>
    public ulong GetNativeAddress(int ilOffset)
    {
        int bestNativeOffset = 0;
        int lo = 0, hi = ILToNativeMap.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ILToNativeMap[mid].ILOffset <= ilOffset)
            {
                bestNativeOffset = ILToNativeMap[mid].NativeOffset;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return CodeStart + (ulong)bestNativeOffset;
    }

    /// <summary>
    /// Finds the IL offset for a native offset using binary search.
    /// Returns 0 if no mapping entry has a native offset ≤ the target.
    /// </summary>
    public int GetILOffset(uint nativeOffset)
    {
        int ilOffset = 0;
        int lo = 0, hi = _byNative.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if ((uint)_byNative[mid].NativeOffset <= nativeOffset)
            {
                ilOffset = _byNative[mid].ILOffset;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ilOffset;
    }
}

/// <summary>
/// Entry in the JIT method map. Stores enough information to resolve a native
/// instruction pointer to a managed method name and source location via PDB lookup.
/// </summary>
internal record JitMethodInfo(int MethodToken, ulong StartAddress, uint CodeSize, string AssemblyName);

/// <summary>
/// Tracks an active managed step operation. The temp breakpoints are removed
/// when the step completes or is cancelled (by continue or a new step).
/// </summary>
internal sealed class ManagedStepState
{
    /// <summary>
    /// dbgeng breakpoint IDs of temporary hardware BPs set for this step.
    /// </summary>
    public List<uint> TempBreakpointIds { get; } = [];

    /// <summary>
    /// Stack pointer (RSP) when the step started. A lower RSP means deeper stack
    /// on x86-64 — we're inside a recursive call and should suppress the temp BP hit.
    /// </summary>
    public ulong OriginStackPointer { get; set; }
}

/// <summary>
/// Case-insensitive comparer for (Token, Assembly) tuples used by
/// <see cref="NativeDebuggerModel.DeferredBreakpointIndex"/>.
/// </summary>
internal sealed class DeferredBreakpointKeyComparer : IEqualityComparer<(int Token, string Assembly)>
{
    public static readonly DeferredBreakpointKeyComparer Instance = new();

    public bool Equals((int Token, string Assembly) x, (int Token, string Assembly) y)
        => x.Token == y.Token
        && string.Equals(x.Assembly, y.Assembly, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((int Token, string Assembly) obj)
        => HashCode.Combine(obj.Token, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Assembly));
}