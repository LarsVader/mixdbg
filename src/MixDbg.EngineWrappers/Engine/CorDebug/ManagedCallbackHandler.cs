using ClrDebug;

namespace MixDbg.Engine.Clr;

/// <summary>
/// Wraps <see cref="CorDebugManagedCallback"/> from ClrDebug, subscribing to its
/// events and re-exposing them as simple C# events for the engine service.
/// Each callback runs on the CLR's debugger helper thread. Handlers must call
/// <c>Continue(false)</c> to resume execution, or leave the process stopped.
/// </summary>
internal sealed class ManagedCallbackHandler
{
    public CorDebugManagedCallback Callback { get; } = new();

    // ── Events for the engine service ────────────────────

    /// <summary>Fired when the debugged process is created.</summary>
    public event Action<CorDebugProcess>? ProcessCreated;

    /// <summary>Fired when the debugged process exits.</summary>
    public event Action? ProcessExited;

    /// <summary>Fired when a managed module is loaded.</summary>
    public event Action<CorDebugAppDomain, CorDebugModule>? ModuleLoaded;

    /// <summary>Fired when a managed breakpoint is hit.</summary>
    public event Action<CorDebugAppDomain, CorDebugThread, CorDebugBreakpoint>? BreakpointHit;

    /// <summary>Fired when a step operation completes.</summary>
    public event Action<CorDebugAppDomain, CorDebugThread>? StepCompleted;

    /// <summary>Fired when Debugger.Break() is called.</summary>
    public event Action<CorDebugAppDomain, CorDebugThread>? DebuggerBreak;

    public ManagedCallbackHandler()
    {
        Callback.OnCreateProcess += (s, e) =>
        {
            ProcessCreated?.Invoke(e.Process);
            e.Process.Continue(false);
        };

        Callback.OnExitProcess += (s, e) =>
        {
            ProcessExited?.Invoke();
        };

        Callback.OnLoadModule += (s, e) =>
        {
            ModuleLoaded?.Invoke(e.AppDomain, e.Module);
            e.AppDomain.Continue(false);
        };

        Callback.OnBreakpoint += (s, e) =>
        {
            if (BreakpointHit != null)
            {
                // Don't auto-continue — engine decides when to resume.
                BreakpointHit.Invoke(e.AppDomain, e.Thread, e.Breakpoint);
            }
            else
            {
                e.AppDomain.Continue(false);
            }
        };

        Callback.OnStepComplete += (s, e) =>
        {
            if (StepCompleted != null)
            {
                StepCompleted.Invoke(e.AppDomain, e.Thread);
            }
            else
            {
                e.AppDomain.Continue(false);
            }
        };

        Callback.OnBreak += (s, e) =>
        {
            if (DebuggerBreak != null)
            {
                DebuggerBreak.Invoke(e.AppDomain, e.Thread);
            }
            else
            {
                e.AppDomain.Continue(false);
            }
        };

        Callback.OnException += (s, e) => e.AppDomain.Continue(false);
        Callback.OnCreateThread += (s, e) => e.AppDomain.Continue(false);
        Callback.OnExitThread += (s, e) => e.AppDomain.Continue(false);
        Callback.OnLoadAssembly += (s, e) => e.AppDomain.Continue(false);
        Callback.OnUnloadAssembly += (s, e) => e.AppDomain.Continue(false);
        Callback.OnUnloadModule += (s, e) => e.AppDomain.Continue(false);
        Callback.OnCreateAppDomain += (s, e) => e.AppDomain.Continue(false);
        Callback.OnExitAppDomain += (s, e) => e.AppDomain.Continue(false);
        Callback.OnNameChange += (s, e) => e.AppDomain.Continue(false);
        Callback.OnLogMessage += (s, e) => e.AppDomain.Continue(false);
        Callback.OnDebuggerError += (s, e) => e.Process.Continue(false);
    }
}
