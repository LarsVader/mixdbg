using MixDbg.Models;
using MixDbg.Models.Dap;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless DAP dispatcher service. Routes incoming DAP commands to
/// registered <see cref="IDapHandlerService"/> implementations.
/// </summary>
internal sealed class DapDispatcherService(
    IEnumerable<IDapHandlerService> handlers,
    IDapServer _server,
    DapServerModel _transport,
    ILoggingService _log,
    LogStore _logStore) : IDapDispatcher
{
    private readonly Dictionary<string, IDapHandlerService> _handlers
        = handlers.ToDictionary(s => s.Command, s => s);

    public void Run()
    {
        while (true)
        {
            RequestMessage? request = _server.ReadRequest(_transport);
            if (request is null) break;

            try
            {
                string argsStr = request.Arguments.HasValue
                    ? request.Arguments.Value.ToString() : "null";
                _log.LogInfo(_logStore, $"DAP request: seq={request.Seq} cmd={request.Command} args={argsStr}");

                if (_handlers.TryGetValue(request.Command, out IDapHandlerService? handler))
                {
                    IDapMessage? body = handler.Execute(request.Arguments);
                    _server.SendResponse(_transport, request, body);
                    _log.LogInfo(_logStore, $"DAP response: cmd={request.Command} success=true");
                }
                else
                {
                    _server.SendErrorResponse(_transport, request, $"Unknown command: {request.Command}");
                    _log.LogInfo(_logStore, $"DAP response: cmd={request.Command} UNKNOWN");
                }
            }
            catch (DisconnectException)
            {
                _server.SendResponse(_transport, request);
                _log.LogInfo(_logStore, $"DAP disconnect");
                break;
            }
            catch (Exception ex)
            {
                _server.SendErrorResponse(_transport, request, ex.Message);
                _log.LogError(_logStore, $"DAP error: cmd={request.Command} err={ex.Message}");
            }
        }
    }
}