namespace MixDbg.Services.Interfaces;

/// <summary>
/// Sends an <c>AttachProfiler</c> command (command_set 0x03, command_id 0x01) over
/// the .NET diagnostic IPC pipe (<c>\\.\pipe\dotnet-diagnostic-{pid}</c>) to load a
/// CLR profiler into a running .NET process. The launched-process path injects the
/// profiler via <c>CORECLR_*</c> environment variables — those cannot be set on a
/// process that is already running, which is why attach uses this protocol instead.
/// </summary>
public interface IProfilerAttachIpcService
{
    /// <summary>
    /// Synchronously sends the AttachProfiler command and waits for the response.
    /// Throws on non-S_OK HRESULT, distinguishing well-known failures
    /// (<c>CORPROF_E_PROFILER_ALREADY_ACTIVE</c>, <c>E_ACCESSDENIED</c>).
    /// </summary>
    /// <param name="pid">Target process id.</param>
    /// <param name="profilerClsid">CLSID of the profiler COM class.</param>
    /// <param name="profilerPath">Absolute path to the profiler DLL (UTF-16 on the wire).</param>
    /// <param name="clientData">Configuration blob handed to <c>InitializeForAttach</c>.</param>
    /// <param name="attachTimeoutMs">Timeout the runtime gives the profiler to attach.</param>
    /// <param name="ipcTimeoutMs">Timeout for the IPC round-trip itself.</param>
    void AttachProfiler(
        int pid,
        Guid profilerClsid,
        string profilerPath,
        byte[] clientData,
        uint attachTimeoutMs = 10_000,
        int ipcTimeoutMs = 30_000);
}
