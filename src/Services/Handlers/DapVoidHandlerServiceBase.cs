using System.Text.Json;
using MixDbg.Models.Interfaces;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers;

/// <summary>
/// Base class for DAP handlers that perform an action without returning a response body.
/// </summary>
public abstract class DapVoidHandlerServiceBase<TArgs>
        : IDapHandlerService
    where TArgs : IDapMessageArguments, new()
{
    public IDapMessage? Execute(JsonElement? args)
    {
		ExecuteInternal(DeserializeArgs(args));
		return null;
    }

	public abstract string Command { get; }

	public abstract void ExecuteInternal(TArgs args);

	private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

	public TArgs DeserializeArgs(JsonElement? request)
    {
        if (!request.HasValue)
            return new TArgs();

        return request.Value.Deserialize<TArgs>(JsonOpts)
            ?? Activator.CreateInstance<TArgs>();
    }
}
