using MixDbg.Models;
using MixDbg.Models.DapMessages.Protocol;

namespace MixDbg.Services.Interfaces;

/// <summary>
/// Stateless DAP transport service. Reads and writes DAP messages
/// using Content-Length framed JSON-RPC over stdin/stdout.
/// All mutable state lives in <see cref="DapServerModel"/>.
/// </summary>
public interface IDapServer
{
    /// <summary>Creates a new transport model bound to the given streams.</summary>
    DapServerModel CreateModel(Stream input, Stream output);

    /// <summary>Reads the next DAP request from the input stream. Returns null on EOF.</summary>
    RequestMessage? ReadRequest(DapServerModel model);

    /// <summary>Sends a successful response to a request.</summary>
    void SendResponse(DapServerModel model, RequestMessage request, object? body = null);

    /// <summary>Sends an error response to a request.</summary>
    void SendErrorResponse(DapServerModel model, RequestMessage request, string message);

    /// <summary>Sends a DAP event (e.g. stopped, terminated, output).</summary>
    void SendEvent(DapServerModel model, string eventName, object? body = null);
}