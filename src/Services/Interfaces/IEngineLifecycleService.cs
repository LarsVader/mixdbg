using MixDbg.Models;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless engine lifecycle service. Owns the engine thread, the event loop,
/// and thread-safe control methods (break, terminate, detach). All mutable state
/// lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
public interface IEngineLifecycleService
{
    /// <summary>Creates a new engine model with dispose action wired up.</summary>
    NativeDebuggerModel CreateModel();

    /// <summary>Starts the engine thread. Caller must set model parameters first, then wait on EngineReady.</summary>
    void StartEngineThread(NativeDebuggerModel model);

    // ── Thread-safe methods ──

    /// <summary>Requests the target to break. Thread-safe — uses SetInterrupt.</summary>
    void Break(NativeDebuggerModel model);

    /// <summary>Terminates the debugged process and ends the session.</summary>
    void Terminate(NativeDebuggerModel model);

    /// <summary>Detaches from the debugged process without terminating it.</summary>
    void Detach(NativeDebuggerModel model);
}
