using System.Text.Json.Serialization;

using MixDbg.Models.Interfaces;

namespace MixDbg.Models.DapMessages.Lifecycle;

public record DisconnectArguments : IDapMessageArguments
{
    [JsonPropertyName("restart")]
    public bool Restart { get; set; }

    [JsonPropertyName("terminateDebuggee")]
    public bool? TerminateDebuggee { get; set; }
}