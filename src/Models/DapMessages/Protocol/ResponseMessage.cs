using System.Text.Json.Serialization;

namespace MixDbg.Models.Dap;

public record ResponseMessage : ProtocolMessage
{
    [JsonPropertyName("type")]
    public override string Type => "response";

    [JsonPropertyName("request_seq")]
    public int RequestSeq { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; set; }
}