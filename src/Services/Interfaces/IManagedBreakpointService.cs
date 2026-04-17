using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless managed breakpoint setting and removal service. Handles binding
/// breakpoints to loaded modules via PDB resolution, setting hardware breakpoints,
/// and cleaning up managed breakpoints. All methods execute on the engine thread.
/// All mutable state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
public interface IManagedBreakpointService
{
    /// <summary>
    /// Sets managed breakpoints for a source file using PDB resolution to find
    /// method tokens, then hardware breakpoints or deferred tracking for pre-JIT methods.
    /// </summary>
    Breakpoint[] SetManagedBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested);

    /// <summary>
    /// Tries to bind a breakpoint to a loaded module. First attempts direct resolution
    /// via <c>GetOffsetByLine</c> (works if method is JIT'd). If not JIT'd, stores as
    /// a deferred breakpoint for resolution via JIT notifications or DAC polling.
    /// </summary>
    /// <returns><c>true</c> if the breakpoint was bound or deferred successfully.</returns>
    bool TryBindBreakpoint(NativeDebuggerModel model, string filePath, int line, int bpId);

    /// <summary>
    /// Sets a hardware execution breakpoint (<c>ba e1</c>) at the given native address.
    /// Uses CPU debug registers — no code patching, so safe for managed code.
    /// Returns the dbgeng breakpoint ID, or <c>null</c> on failure.
    /// </summary>
    uint? SetManagedCodeBreakpoint(NativeDebuggerModel model, ulong address, string filePath, int line);

    /// <summary>
    /// Resolves exact (assembly, token) pairs from breakpoint file:line hints by
    /// searching for PDB files on disk. Used to tell the CLR profiler which exact
    /// methods to block on during JIT (zero overhead for all other JITs).
    /// </summary>
    List<(string Assembly, int Token)> ResolveTokensFromBreakpoints(IEnumerable<(string FilePath, int Line)> breakpoints);

    /// <summary>
    /// Resolves C++/CLI assembly names from breakpoint file:line hints. Returns
    /// distinct assembly names for C++/CLI files that have CLRSupport in their vcxproj.
    /// Used to set <c>MIXDBG_WATCH_ASSEMBLIES</c> so the profiler hooks all methods
    /// from these assemblies (enabling first-click breakpoints).
    /// </summary>
    List<string> ResolveWatchAssemblies(IEnumerable<(string FilePath, int Line)> breakpoints);
}
