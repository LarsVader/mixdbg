using System.Text.Json.Serialization;
using MixDbg.Services.Interfaces;

namespace MixDbg.Dap;

public record EvaluateResponseBody : IDapMessage
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}
