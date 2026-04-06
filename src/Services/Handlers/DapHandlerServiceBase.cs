using System.Text.Json;

using MixDbg.Models.Interfaces;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services.Handlers;

public abstract class DapHandlerServiceBase<TResponse, TArgs>
        : IDapHandlerService
    where TResponse : IDapMessage
    where TArgs : IDapMessageArguments, new()
{
    public IDapMessage? Execute(JsonElement? args) => ExecuteInternal(DeserializeArgs(args));

    public abstract string Command { get; }

    public abstract TResponse ExecuteInternal(TArgs args);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TArgs DeserializeArgs(JsonElement? request) => !request.HasValue
            ? new TArgs()
            : request.Value.Deserialize<TArgs>(JsonOpts)
            ?? Activator.CreateInstance<TArgs>();
}