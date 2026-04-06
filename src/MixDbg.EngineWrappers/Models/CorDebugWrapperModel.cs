using ClrDebug;

namespace MixDbg.Models;

/// <summary>
/// Mutable state for the piggybacked ICorDebug V4 session. Holds ICorDebug
/// references (internal, only accessed by <see cref="Services.CorDebugWrapperService"/>),
/// the DAC interfaces, and the module cache.
/// </summary>
public sealed class CorDebugWrapperModel
{
    // ── ICorDebug V4 — internal, only for CorDebugWrapperService ──

    internal CorDebugProcess? Process { get; set; }
    internal SOSDacInterface? SosDac { get; set; }
    internal XCLRDataProcess? XclrProcess { get; set; }
    internal IntPtr DacHandle { get; set; }

    /// <summary>Whether ICorDebug V4 has been initialized.</summary>
    public bool Initialized { get; internal set; }

    /// <summary>Whether the DAC (SOSDacInterface) has been loaded.</summary>
    public bool DacLoaded { get; internal set; }

    // ── Module cache — CorDebugModule references stay internal ──

    internal Dictionary<long, CorDebugWrapperModule> Modules { get; } = new();

    // ── Legacy ICorDebug breakpoints (deactivate-only) ──

    internal Dictionary<int, CorDebugFunctionBreakpoint> LegacyBreakpoints { get; } = new();

    /// <summary>Whether any legacy ICorDebug breakpoints exist (for hit detection).</summary>
    public bool HasLegacyBreakpoints => LegacyBreakpoints.Count > 0;
}

/// <summary>
/// Internal module entry storing the ICorDebug module reference alongside
/// resolved paths. Only accessed by <see cref="Services.CorDebugWrapperService"/>.
/// </summary>
internal sealed class CorDebugWrapperModule
{
    public required CorDebugModule Module { get; init; }
    public required string? Path { get; init; }
    public required string? PdbPath { get; init; }
}
