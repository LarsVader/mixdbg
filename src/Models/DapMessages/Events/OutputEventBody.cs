using System.Text.Json.Serialization;

namespace MixDbg.Models.DapMessages.Events;

public record OutputEventBody
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "console";

    [JsonPropertyName("output")]
    public string Output { get; set; } = "";
}