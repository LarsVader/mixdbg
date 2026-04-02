using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

public interface IDapServer
{
    DapServerModel CreateModel(Stream input, Stream output);
    RequestMessage? ReadRequest(DapServerModel model);
    void SendResponse(DapServerModel model, RequestMessage request, object? body = null);
    void SendErrorResponse(DapServerModel model, RequestMessage request, string message);
    void SendEvent(DapServerModel model, string eventName, object? body = null);
}
