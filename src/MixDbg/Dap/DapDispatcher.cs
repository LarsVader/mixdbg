using System.Text.Json;

namespace MixDbg.Dap;

/// <summary>
/// Routes DAP requests to handler methods and manages the request/response lifecycle.
/// </summary>
public sealed class DapDispatcher
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly DapServer _server;
    private readonly Dictionary<string, Func<RequestMessage, object?>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public DapDispatcher(DapServer server)
    {
        _server = server;
    }

    public DapServer Server => _server;

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
            var request = _server.ReadRequest();
            if (request is null) break;

            try
            {
                if (_handlers.TryGetValue(request.Command, out var handler))
                {
                    var body = handler(request);
                    _server.SendResponse(request, body);
                }
                else
                {
                    _server.SendErrorResponse(request, $"Unknown command: {request.Command}");
                }
            }
            catch (DisconnectException)
            {
                _server.SendResponse(request);
                break;
            }
            catch (Exception ex)
            {
                _server.SendErrorResponse(request, ex.Message);
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
