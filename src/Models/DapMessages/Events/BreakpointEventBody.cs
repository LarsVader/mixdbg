using System.Text.Json.Serialization;

namespace MixDbg.Models.Dap;

public record BreakpointEventBody
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "changed";

    [JsonPropertyName("breakpoint")]
    public Breakpoint Breakpoint { get; set; } = new();
}
