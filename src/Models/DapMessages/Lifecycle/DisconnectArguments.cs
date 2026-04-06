using System.Text.Json.Serialization;

using MixDbg.Models.Interfaces;

namespace MixDbg.Models.Dap;

public record DisconnectArguments : IDapMessageArguments
{
    [JsonPropertyName("restart")]
    public bool Restart { get; set; }

    [JsonPropertyName("terminateDebuggee")]
    public bool? TerminateDebuggee { get; set; }
}