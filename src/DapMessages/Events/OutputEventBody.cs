using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record OutputEventBody
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "console";

    [JsonPropertyName("output")]
    public string Output { get; set; } = "";
}
