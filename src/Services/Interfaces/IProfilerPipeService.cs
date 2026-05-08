using MixDbg.Models;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Manages the named pipe connection to the CLR profiler DLL running in the target process.
/// Handles setup of profiler environment variables, pipe creation, and reading JIT/ENTER
/// notifications from the profiler.
/// </summary>
public interface IProfilerPipeService
{
    /// <summary>
    /// Launch path: creates the named pipes and ACK event, then sets the
    /// <c>CORECLR_*</c>/<c>MIXDBG_*</c> environment variables that the
    /// child process will inherit on <c>CreateProcess</c>.
    /// </summary>
    void SetupProfilerPipe(NativeDebuggerModel model);

    /// <summary>
    /// Attach path: creates the named pipes and ACK event, then loads the
    /// profiler into the already-running target via the .NET diagnostic IPC
    /// pipe (<c>AttachProfiler</c> command). The configuration that env vars
    /// carry in the launch path is passed inline as the <c>InitializeForAttach</c>
    /// client-data blob.
    /// </summary>
    void SetupProfilerPipeForAttach(NativeDebuggerModel model, int pid);

    /// <summary>Starts the background thread that reads profiler notifications from the named pipe.</summary>
    void StartProfilerReader(NativeDebuggerModel model);
}