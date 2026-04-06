using MixDbg.Models;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless wrapper around ICorDebug V4 (piggybacked on the dbgeng session).
/// Encapsulates all ClrDebug COM interop so the rest of the codebase never
/// references ClrDebug types directly. All methods must be called on the engine thread.
/// </summary>
public interface ICorDebugWrapper
{
    // ── Lifecycle ──

    /// <summary>Creates a new wrapper model (no ICorDebug objects yet).</summary>
    CorDebugWrapperModel CreateModel();

    /// <summary>
    /// Opens a piggybacked ICorDebugProcess via OpenVirtualProcessImpl.
    /// Creates the DbgEngDataTarget bridge internally. Does NOT enumerate
    /// modules or initialize the DAC — the caller orchestrates those steps.
    /// </summary>
    bool InitializeProcess(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress);

    /// <summary>
    /// Initializes or refreshes the DAC (SOSDacInterface + XCLRDataProcess).
    /// </summary>
    bool InitializeDac(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress);

    /// <summary>Whether ICorDebug V4 has been initialized.</summary>
    bool IsInitialized(CorDebugWrapperModel model);

    // ── Process State ──

    /// <summary>
    /// Notifies ICorDebug that the process state has changed
    /// (calls ProcessStateChanged(FLUSH_ALL)). Call before reading
    /// process state after a break.
    /// </summary>
    void FlushProcessState(CorDebugWrapperModel model);

    // ── Module Enumeration ──

    /// <summary>
    /// Re-enumerates ICorDebug modules (AppDomains → Assemblies → Modules).
    /// Updates the internal module cache.
    /// </summary>
    void RefreshModules(CorDebugWrapperModel model);

    /// <summary>Returns info for all loaded modules.</summary>
    ManagedModuleInfo[] GetModules(CorDebugWrapperModel model);

    /// <summary>Finds a module by assembly name (case-insensitive filename match).</summary>
    ManagedModuleInfo? FindModuleByName(CorDebugWrapperModel model, string assemblyName);

    // ── Stack Traces ──

    /// <summary>
    /// Gets raw managed stack frames for the given OS thread ID by walking
    /// ICorDebug chains and frames. Uses MetaDataImport for method names.
    /// Does NOT resolve PDB source locations — the caller does that.
    /// </summary>
    RawManagedFrame[] GetRawManagedFrames(CorDebugWrapperModel model, uint osThreadId);

    // ── DAC Operations ──

    /// <summary>
    /// Resolves a method token to its JIT'd native entry point via XCLRDataProcess.
    /// Returns 0 if not yet JIT'd.
    /// </summary>
    ulong ResolveNativeEntryViaXclrData(CorDebugWrapperModel model, int methodToken, string? assemblyName);

    // ── Breakpoint Support ──

    /// <summary>
    /// Deactivates and removes a legacy ICorDebug function breakpoint by its DAP ID.
    /// </summary>
    void DeactivateLegacyBreakpoint(CorDebugWrapperModel model, int bpId);
}