using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless debug session orchestrator. Manages state transitions,
/// pending breakpoints, and coordinates between the DAP layer and the
/// native debug engine. All mutable state lives in <see cref="DebugSessionModel"/>.
/// </summary>
public interface IDebugSession
{
    /// <summary>Creates a new session model in the Uninitialized state.</summary>
    DebugSessionModel CreateModel();

    /// <summary>Handles the DAP initialize handshake, sends the initialized event, and returns capabilities.</summary>
    Capabilities Initialize(DebugSessionModel session, InitializeRequestArguments args);

    /// <summary>Applies pending breakpoints and resumes the target after configuration.</summary>
    void ConfigurationDone(DebugSessionModel session);

    /// <summary>Launches a new process under the debugger.</summary>
    void Launch(DebugSessionModel session, LaunchRequestArguments args);

    /// <summary>Attaches to an existing process by PID.</summary>
    void Attach(DebugSessionModel session, AttachRequestArguments args);

    /// <summary>Sets breakpoints for a source file. Queues them as pending if the engine is not ready.</summary>
    SetBreakpointsResponseBody SetBreakpoints(DebugSessionModel session, SetBreakpointsArguments args);

    /// <summary>Continues execution of all threads.</summary>
    void Continue(DebugSessionModel session);

    /// <summary>Steps over one source line.</summary>
    void StepOver(DebugSessionModel session);

    /// <summary>Steps into the next call.</summary>
    void StepInto(DebugSessionModel session);

    /// <summary>Steps out of the current function.</summary>
    void StepOut(DebugSessionModel session);

    /// <summary>Requests the target to break (pause).</summary>
    void Pause(DebugSessionModel session);

    /// <summary>Returns the current call stack.</summary>
    StackTraceResponseBody GetStackTrace(DebugSessionModel session, StackTraceArguments args);

    /// <summary>Returns the scopes (locals, arguments) for a stack frame.</summary>
    ScopesResponseBody GetScopes(DebugSessionModel session, ScopesArguments args);

    /// <summary>Returns the variables for a variablesReference handle.</summary>
    VariablesResponseBody GetVariables(DebugSessionModel session, VariablesArguments args);

    /// <summary>Returns all debugger threads.</summary>
    ThreadsResponseBody GetThreads(DebugSessionModel session);

    /// <summary>Terminates or detaches from the target and transitions to Terminated state.</summary>
    void Disconnect(DebugSessionModel session, DisconnectArguments args);
}
