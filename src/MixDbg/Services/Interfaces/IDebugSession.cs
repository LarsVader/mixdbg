using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

public interface IDebugSession
{
    DebugSessionModel CreateModel();
    Capabilities Initialize(DebugSessionModel session, InitializeRequestArguments args);
    void ConfigurationDone(DebugSessionModel session);
    void Launch(DebugSessionModel session, LaunchRequestArguments args);
    void Attach(DebugSessionModel session, AttachRequestArguments args);
    SetBreakpointsResponseBody SetBreakpoints(DebugSessionModel session, SetBreakpointsArguments args);
    void Continue(DebugSessionModel session);
    void StepOver(DebugSessionModel session);
    void StepInto(DebugSessionModel session);
    void StepOut(DebugSessionModel session);
    void Pause(DebugSessionModel session);
    StackTraceResponseBody GetStackTrace(DebugSessionModel session, StackTraceArguments args);
    ThreadsResponseBody GetThreads(DebugSessionModel session);
    void Disconnect(DebugSessionModel session, DisconnectArguments args);
}
