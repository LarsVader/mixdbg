using MixDbg.Models;
using MixDbg.Models.DapMessages.Lifecycle;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers.Lifecycle;

/// <summary>
/// Handles the DAP attach request by attaching to an existing process.
/// </summary>
public class AttachRequestHandlerService(
        IEngineLifecycleService nativeDebugger,
        DebugSessionModel sessionModel)
    : DapVoidHandlerServiceBase<AttachRequestArguments>
{
    public const string DapMessage = "attach";

    public override string Command => DapMessage;

    public override void ExecuteInternal(AttachRequestArguments args)
    {
        sessionModel.Engine = nativeDebugger.CreateModel();

        if (args.Pid.HasValue
            && sessionModel.Engine is NativeDebuggerModel debuggerModel)
        {
            debuggerModel.IsAttach = true;
            debuggerModel.AttachPid = (uint)args.Pid.Value;
            debuggerModel.SymbolPath = null;
            nativeDebugger.StartEngineThread(debuggerModel);
            debuggerModel.EngineReady.Wait();
            if (debuggerModel.EngineInitError != null)
                throw debuggerModel.EngineInitError;
            // nativeDebugger.Attach(sessionModel.Engine, (uint)args.Pid.Value, null);
        }
        else
        {
            throw new InvalidOperationException("PID is required for attach");
        }

        sessionModel.State = SessionState.Running;
    }
}