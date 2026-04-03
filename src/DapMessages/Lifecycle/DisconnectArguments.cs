using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record DisconnectArguments
{
    [JsonPropertyName("restart")]
    public bool Restart { get; set; }

    [JsonPropertyName("terminateDebuggee")]
    public bool? TerminateDebuggee { get; set; }
}
