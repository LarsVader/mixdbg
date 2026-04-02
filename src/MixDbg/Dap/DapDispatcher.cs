using System.Text.Json;
using MixDbg.Models;
using MixDbg.Services;

namespace MixDbg.Dap;

/// <summary>
/// Routes DAP requests to handler methods and manages the request/response lifecycle.
/// </summary>
public sealed class DapDispatcher(
    IDapServer server,
    DapServerModel transport,
    ILoggingService log,
    LogStore logStore)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly Dictionary<string, Func<RequestMessage, object?>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a handler for a DAP command. The handler returns
    /// a response body (or null for commands with no body).
    /// </summary>
    public void Register(string command, Func<RequestMessage, object?> handler)
    {
        _handlers[command] = handler;
    }

    /// <summary>
    /// Convenience overload for handlers that deserialize arguments.
    /// </summary>
    public void Register<TArgs>(string command, Func<TArgs, object?> handler)
    {
        _handlers[command] = req =>
        {
            var args = DeserializeArgs<TArgs>(req);
            return handler(args);
        };
    }

    /// <summary>
    /// Reads and dispatches requests until the stream closes or
    /// disconnect is received.
    /// </summary>
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
                    var body = handler(request);
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

    public static TArgs DeserializeArgs<TArgs>(RequestMessage request)
    {
        if (request.Arguments is null)
            return Activator.CreateInstance<TArgs>();

        return request.Arguments.Value.Deserialize<TArgs>(JsonOpts)
            ?? Activator.CreateInstance<TArgs>();
    }
}

/// <summary>
/// Thrown by the disconnect handler to cleanly exit the dispatch loop.
/// </summary>
public sealed class DisconnectException : Exception;
