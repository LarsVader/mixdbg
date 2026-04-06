using System.Text.Json.Serialization;

namespace MixDbg.Models.Dap;

public abstract record ProtocolMessage
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("type")]
    public abstract string Type { get; }
}
