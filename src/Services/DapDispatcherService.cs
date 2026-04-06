using System.Text.Json;
using MixDbg.Dap;
using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless DAP dispatcher service. Routes incoming DAP commands to
/// registered <see cref="IDapHandlerService"/> implementations.
/// </summary>
internal sealed class DapDispatcherService(
	IEnumerable<IDapHandlerService> handlers,
    IDapServer server,
    DapServerModel transport,
    ILoggingService log,
    LogStore logStore) : IDapDispatcher
{
	private readonly Dictionary<string, IDapHandlerService> _handlers
		= handlers.ToDictionary(s => s.Command, s => s);

    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;

    public void Run()
    {
        while (true)
        {
            var request = _server.ReadRequest(_transport);
            if (request is null) break;

            try
            {
                var argsStr = request.Arguments.HasValue
                    ? request.Arguments.Value.ToString() : "null";
                _log.LogInfo(_logStore, $"DAP request: seq={request.Seq} cmd={request.Command} args={argsStr}");

				if (_handlers.TryGetValue(request.Command, out var handler))
				{
					var body = handler.Execute(request.Arguments);
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
