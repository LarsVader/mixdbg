using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record Capabilities
{
    [JsonPropertyName("supportsConfigurationDoneRequest")]
    public bool SupportsConfigurationDoneRequest { get; set; }

    [JsonPropertyName("supportsFunctionBreakpoints")]
    public bool SupportsFunctionBreakpoints { get; set; }

    [JsonPropertyName("supportsEvaluateForHovers")]
    public bool SupportsEvaluateForHovers { get; set; }

    [JsonPropertyName("supportsTerminateRequest")]
    public bool SupportsTerminateRequest { get; set; }
}
