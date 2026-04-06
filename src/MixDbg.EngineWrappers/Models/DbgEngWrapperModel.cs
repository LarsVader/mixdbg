using MixDbg.Engine.DbgEng;

namespace MixDbg.Models;

/// <summary>
/// Mutable state for the dbgeng COM wrapper. Holds COM interface references
/// (internal, only accessed by <see cref="Services.DbgEngWrapperService"/>),
/// engine callback events (public, subscribed by higher-level services),
/// and variable/stack frame caches for scope inspection.
/// </summary>
public sealed class DbgEngWrapperModel
{
    // ── COM interfaces — internal, only for DbgEngWrapperService ──

    internal IDebugClient Client { get; set; } = null!;
    internal IDebugControl Control { get; set; } = null!;
    internal IDebugSymbols Symbols { get; set; } = null!;
    internal IDebugSystemObjects SysObjects { get; set; } = null!;
    internal IDebugDataSpaces DataSpaces { get; set; } = null!;
    internal IDebugAdvanced Advanced { get; set; } = null!;
    internal EventCallbacks Callbacks { get; set; } = null!;

    // ── Engine callback events (public for NativeDebuggerService) ──

    /// <summary>Raised when a breakpoint fires. Parameter is the dbgeng breakpoint ID.</summary>
    public event Action<uint>? OnBreakpointHit;

    /// <summary>Raised when the debugged process exits. Parameter is the exit code.</summary>
    public event Action<uint>? OnExitProcess;

    /// <summary>Raised when a module loads. Parameters: module name, image path, base address.</summary>
    public event Action<string?, string?, ulong>? OnLoadModule;

    /// <summary>Raised when a process is created. Parameter is the image name.</summary>
    public event Action<string?>? OnCreateProcess;

    /// <summary>Raised on EXCEPTION_BREAKPOINT (0x80000003/4). Parameter is the exception address.</summary>
    public event Action<ulong>? OnExceptionBreakpoint;

    /// <summary>Raised on CLR notification exception (0xe0444143).</summary>
    public event Action? OnClrNotification;

    /// <summary>
    /// Set by subscribers of <see cref="OnClrNotification"/> to request the engine
    /// to break on the next CLR notification instead of auto-continuing.
    /// </summary>
    public bool ClrNotificationShouldBreak { get; set; }

    // ── Internal raise methods (called by DbgEngWrapperService) ──

    internal void RaiseBreakpointHit(uint bpId) => OnBreakpointHit?.Invoke(bpId);
    internal void RaiseExitProcess(uint exitCode) => OnExitProcess?.Invoke(exitCode);
    internal void RaiseLoadModule(string? name, string? image, ulong baseAddress) => OnLoadModule?.Invoke(name, image, baseAddress);
    internal void RaiseCreateProcess(string? name) => OnCreateProcess?.Invoke(name);
    internal void RaiseExceptionBreakpoint(ulong address) => OnExceptionBreakpoint?.Invoke(address);
    internal void RaiseClrNotification() => OnClrNotification?.Invoke();

    // ── Variable inspection state (internal, managed by DbgEngWrapperService) ──

    internal VariableStore Variables { get; } = new();

    // ── Cached stack frames for SetScope (internal, raw dbgeng structs) ──

    internal DEBUG_STACK_FRAME[] CachedStackFrames { get; set; } = [];
}
