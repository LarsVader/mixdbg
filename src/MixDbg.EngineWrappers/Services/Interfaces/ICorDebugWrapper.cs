using MixDbg.Models;

namespace MixDbg.Services;

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
    /// Initializes ICorDebug V4 via <c>OpenVirtualProcess</c>, piggybacked on
    /// the dbgeng session. Enumerates modules on success.
    /// </summary>
    bool InitializeRuntime(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress);

    /// <summary>
    /// Initializes or refreshes the DAC (SOSDacInterface + XCLRDataProcess).
    /// </summary>
    bool InitializeDac(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress);

    /// <summary>Whether ICorDebug V4 has been initialized.</summary>
    bool IsInitialized(CorDebugWrapperModel model);

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
    /// Gets managed stack frames for the given OS thread ID by walking
    /// ICorDebug chains and frames. Uses MetaDataImport for method names
    /// and PdbSourceMapper for source resolution.
    /// </summary>
    ManagedFrameInfo[] GetManagedStackFrames(CorDebugWrapperModel model, uint osThreadId);

    // ── DAC Operations ──

    /// <summary>
    /// Resolves a method token to its JIT'd native entry point via XCLRDataProcess.
    /// Returns 0 if not yet JIT'd.
    /// </summary>
    ulong ResolveNativeEntryViaXclrData(CorDebugWrapperModel model, int methodToken, string? assemblyName);

    /// <summary>
    /// Resolves a native address to a method descriptor's native code address
    /// via SOSDacInterface. Returns the input address if DAC lookup fails.
    /// </summary>
    ulong ResolveNativeEntryPoint(CorDebugWrapperModel model, ulong symbolAddress);

    // ── Breakpoint Support ──

    /// <summary>
    /// Deactivates and removes a legacy ICorDebug function breakpoint by its DAP ID.
    /// </summary>
    void DeactivateLegacyBreakpoint(CorDebugWrapperModel model, int bpId);
}
