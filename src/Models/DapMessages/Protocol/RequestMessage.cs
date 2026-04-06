using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixDbg.Models.Dap;

public record RequestMessage : ProtocolMessage
{
    [JsonPropertyName("type")]
    public override string Type => "request";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}
