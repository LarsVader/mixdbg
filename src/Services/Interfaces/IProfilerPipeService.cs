using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Manages the named pipe connection to the CLR profiler DLL running in the target process.
/// Handles setup of profiler environment variables, pipe creation, and reading JIT/ENTER
/// notifications from the profiler.
/// </summary>
public interface IProfilerPipeService
{
    /// <summary>Sets up the profiler pipe, resolves watch tokens/assemblies, and configures env vars on the model.</summary>
    void SetupProfilerPipe(NativeDebuggerModel model);

    /// <summary>Starts the background thread that reads profiler notifications from the named pipe.</summary>
    void StartProfilerReader(NativeDebuggerModel model);
}