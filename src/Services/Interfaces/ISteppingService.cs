using MixDbg.Models;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless stepping and execution control service. Provides continue, step over,
/// step into, and step out operations across native and managed code boundaries.
/// All methods must be called on the engine thread.
/// </summary>
public interface ISteppingService
{
    /// <summary>Resumes execution, cancels any active managed step, and clears variable state.</summary>
    void ExecuteContinueOnEngine(NativeDebuggerModel model);

    /// <summary>Steps over or into by setting the execution status, with managed code awareness.</summary>
    void ExecuteStepOnEngine(NativeDebuggerModel model, EngineExecutionStatus stepKind);

    /// <summary>Steps out via temp BP at caller's return address or dbgeng "gu" fallback.</summary>
    void ExecuteStepOutOnEngine(NativeDebuggerModel model);
}
