using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record EvaluateResponseBody
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}
