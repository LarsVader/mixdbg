using MixDbg.Dap;

namespace MixDbg.Models;

public enum SessionState
{
    Uninitialized,
    Initialized,
    Configured,
    Running,
    Stopped,
    Terminated,
}

public sealed class DebugSessionModel : IDisposable
{
    public SessionState State { get; internal set; } = SessionState.Uninitialized;

    internal NativeDebuggerModel? Engine { get; set; }
    internal int NextPendingBpId { get; set; } = 1000;
    internal List<SetBreakpointsArguments> PendingBreakpoints { get; } = new();

    public void Dispose()
    {
        Engine?.Dispose();
    }
}
