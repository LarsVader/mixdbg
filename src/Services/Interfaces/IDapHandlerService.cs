using System.Text.Json;

namespace MixDbg.Services.Interfaces;

public interface IDapHandlerService
{
    string Command { get; }
    IDapMessage? Execute(JsonElement? args);
}