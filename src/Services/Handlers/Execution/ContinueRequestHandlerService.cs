using MixDbg.Models;
using MixDbg.Models.DapMessages.Execution;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Execution;

/// <summary>
/// Handles the DAP continue request by resuming execution.
/// </summary>
public class ContinueRequestHandlerService(
        ILoggingService log,
        LogStore logStore,
        INativeDebugger nativeDebugger,
        DebugSessionModel sessionModel)
    : DapHandlerServiceBase<ContinueResponseBody, ContinueArguments>
{
    public const string DapMessage = "continue";

    public override string Command => DapMessage;

    public override ContinueResponseBody ExecuteInternal(ContinueArguments args)
    {
        if (sessionModel.Engine is NativeDebuggerModel model)
        {
            log.LogInfo(logStore, "Continue queued");
            model.CachedStackTraceResult = null;
            model.Commands.Add(() => nativeDebugger.ExecuteContinueOnEngine(model));
            sessionModel.State = SessionState.Running;
        }
        return new ContinueResponseBody { AllThreadsContinued = true };
    }
}