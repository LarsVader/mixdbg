using MixDbg.Models;
using MixDbg.Models.DapMessages.Protocol;
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

            // Track whether the response has gone out so a failure AFTER the
            // response is on the wire (SendResponse mid-write IO error, or a
            // throw from OnAfterResponse) doesn't fall through to the outer
            // catch and emit SendErrorResponse for the same request_seq.
            bool responseSent = false;
            try
            {
                string argsStr = request.Arguments.HasValue
                    ? request.Arguments.Value.ToString() : "null";
                _log.LogVerbose(_logStore, $"DAP request: seq={request.Seq} cmd={request.Command} args={argsStr}");

                if (_handlers.TryGetValue(request.Command, out IDapHandlerService? handler))
                {
                    IDapMessage? body = handler.Execute(request.Arguments);
                    // Mark BEFORE SendResponse, not after: if SendResponse
                    // throws mid-write we cannot know whether bytes already
                    // hit the wire, and emitting an error response on top
                    // could duplicate or corrupt framing. Treat the call as
                    // "committed" the moment we attempt it.
                    responseSent = true;
                    _server.SendResponse(_transport, request, body);
                    _log.LogVerbose(_logStore, $"DAP response: cmd={request.Command} success=true");
                    if (handler is IDapAfterResponseAction afterResponse)
                    {
                        // Isolated try: the response has already been written to the
                        // wire, so any exception here must NOT escape into the outer
                        // catch (which would emit a second response — corrupting
                        // the request/response correlation on the client side). The
                        // nested try wraps LogError too, because the logger lazily
                        // opens a file handle and could itself throw (e.g. disk full).
                        try { afterResponse.OnAfterResponse(); }
                        catch (Exception afterEx)
                        {
                            try { _log.LogError(_logStore, $"DAP after-response error: cmd={request.Command} {afterEx.GetType().Name}: {afterEx.Message}"); }
                            catch { /* logger failed; nothing left to do */ }
                        }
                    }
                }
                else
                {
                    responseSent = true;
                    _server.SendErrorResponse(_transport, request, $"Unknown command: {request.Command}");
                    _log.LogInfo(_logStore, $"DAP response: cmd={request.Command} UNKNOWN");
                }
            }
            catch (DisconnectException) when (!responseSent)
            {
                _server.SendResponse(_transport, request);
                _log.LogInfo(_logStore, $"DAP disconnect");
                break;
            }
            catch (DisconnectException)
            {
                // Response already on the wire; treat as a clean disconnect
                // signal without emitting a second response on the same seq.
                _log.LogInfo(_logStore, $"DAP disconnect (post-response)");
                break;
            }
            catch (Exception ex) when (!responseSent)
            {
                // Defensively wrap SendErrorResponse — a wedged transport
                // would otherwise kill the dispatch loop and we'd lose the
                // chance to read the next request.
                try { _server.SendErrorResponse(_transport, request, ex.Message); }
                catch (Exception sendEx)
                {
                    try { _log.LogError(_logStore, $"DAP error response failed: cmd={request.Command} {sendEx.GetType().Name}: {sendEx.Message}"); } catch { }
                }
                _log.LogError(_logStore, $"DAP error: cmd={request.Command} {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Response already on the wire — emitting another would corrupt
                // request/response correlation. Log only.
                try { _log.LogError(_logStore, $"DAP post-response error: cmd={request.Command} {ex.GetType().Name}: {ex.Message}"); }
                catch { }
            }
        }
    }
}
