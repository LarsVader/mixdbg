using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record VariablesArguments
{
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}
