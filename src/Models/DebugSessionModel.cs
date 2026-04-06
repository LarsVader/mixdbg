using MixDbg.Models.Dap;

namespace MixDbg.Models;

/// <summary>
/// Lifecycle state of the debug session, from initial handshake through
/// active debugging to termination.
/// </summary>
public enum SessionState
{
    Uninitialized,
    Initialized,
    Configured,
    Running,
    Stopped,
    Terminated,
}

/// <summary>
/// Mutable state for the debug session. Tracks session lifecycle state,
/// holds a reference to the native debugger engine (created on launch/attach),
/// and buffers breakpoints received before the engine is ready.
/// Disposing this model disposes the underlying engine.
/// </summary>
public sealed class DebugSessionModel : IDisposable
{
    public SessionState State { get; internal set; } = SessionState.Uninitialized;

    internal NativeDebuggerModel? Engine { get; set; }
    internal int NextPendingBpId { get; set; } = 1000;
    internal List<SetBreakpointsArguments> PendingBreakpoints { get; } = [];

    public void Dispose() => Engine?.Dispose();
}