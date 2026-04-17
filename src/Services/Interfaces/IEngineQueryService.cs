using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Threads;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless engine query service. Provides stack trace, scope, variable, and
/// thread inspection. All methods must be called on the engine thread.
/// </summary>
public interface IEngineQueryService
{
    /// <summary>Gets the current call stack with resolved function names and source locations.</summary>
    StackFrame[] GetStackTraceOnEngine(NativeDebuggerModel model, int maxFrames);

    /// <summary>Gets the scopes (locals, arguments) for a stack frame by frame ID.</summary>
    Scope[] GetScopesOnEngine(NativeDebuggerModel model, int frameId);

    /// <summary>Gets variables for a variablesReference handle.</summary>
    Variable[] GetVariablesOnEngine(NativeDebuggerModel model, int variablesReference);

    /// <summary>Gets all debugger threads with engine and system IDs.</summary>
    DapThread[] GetThreadsOnEngine(NativeDebuggerModel model);

    /// <summary>Gets the engine thread ID that hit the last event.</summary>
    int GetStoppedThreadIdOnEngine(NativeDebuggerModel model);
}
