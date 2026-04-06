using System.Text.Json.Serialization;

namespace MixDbg.Models.DapMessages.Protocol;

public record EventMessage : ProtocolMessage
{
    [JsonPropertyName("type")]
    public override string Type => "event";

    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; set; }
}