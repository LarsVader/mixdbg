using System.Text.Json;
using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless DAP dispatcher service. All mutable state lives in
/// <see cref="DapDispatcherModel"/>.
/// </summary>
internal sealed class DapDispatcherService(
    IDapServer server,
    DapServerModel transport,
    ILoggingService log,
    LogStore logStore) : IDapDispatcher
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;

    public DapDispatcherModel CreateModel()
        => new();

    public void Register(DapDispatcherModel model, string command, Func<RequestMessage, object?> handler)
    {
        model.Handlers[command] = handler;
    }

    public void Register<TArgs>(DapDispatcherModel model, string command, Func<TArgs, object?> handler)
    {
        model.Handlers[command] = req =>
        {
            var args = DeserializeArgs<TArgs>(req);
            return handler(args);
        };
    }

    public void Run(DapDispatcherModel model)
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

                if (model.Handlers.TryGetValue(request.Command, out var handler))
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

    internal static TArgs DeserializeArgs<TArgs>(RequestMessage request)
    {
        if (request.Arguments is null)
            return Activator.CreateInstance<TArgs>();

        return request.Arguments.Value.Deserialize<TArgs>(JsonOpts)
            ?? Activator.CreateInstance<TArgs>();
    }
}
