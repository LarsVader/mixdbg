using MixDbg.Dap;

namespace MixDbg.Services;

public interface IDapServer
{
    RequestMessage? ReadRequest();
    void SendResponse(RequestMessage request, object? body = null);
    void SendErrorResponse(RequestMessage request, string message);
    void SendEvent(string eventName, object? body = null);
}
