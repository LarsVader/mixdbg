using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record VariablesResponseBody
{
    [JsonPropertyName("variables")]
    public Variable[] Variables { get; set; } = [];
}
