using System.Text.Json.Serialization;

namespace MixDbg.Models.DapMessages.Inspection;

public record Scope
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }

    [JsonPropertyName("expensive")]
    public bool Expensive { get; set; }
}