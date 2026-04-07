using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless breakpoint management service. Handles setting, removing, and
/// responding to breakpoint hits. All <c>OnEngine</c> methods must be called
/// on the engine thread.
/// </summary>
public interface IBreakpointService
{
    /// <summary>Sets breakpoints for a source file. Uses deferred breakpoints when symbols are not yet loaded.</summary>
    Breakpoint[] SetBreakpointsOnEngine(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested);

    /// <summary>Handles a breakpoint hit callback from dbgeng. Called on the engine thread.</summary>
    void HandleBreakpointHit(NativeDebuggerModel model, uint breakpointId);

    /// <summary>Handles an exception-based breakpoint (managed IL breakpoint). Called on the engine thread.</summary>
    void HandleExceptionBreakpoint(NativeDebuggerModel model, ulong address);
}
