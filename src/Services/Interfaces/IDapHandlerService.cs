using System.Text.Json;

namespace MixDbg.Services.Interfaces;

public interface IDapHandlerService
{
	public string Command { get; }
    public IDapMessage? Execute(JsonElement? args);
}
