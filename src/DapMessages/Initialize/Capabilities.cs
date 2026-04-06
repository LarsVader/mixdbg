using System.Text.Json.Serialization;
using MixDbg.Services.Interfaces;

namespace MixDbg.Dap;

public record Capabilities : IDapMessage
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
