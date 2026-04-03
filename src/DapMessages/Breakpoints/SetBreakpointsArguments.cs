using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record SetBreakpointsArguments
{
    [JsonPropertyName("source")]
    public Source Source { get; set; } = new();

    [JsonPropertyName("breakpoints")]
    public SourceBreakpoint[] Breakpoints { get; set; } = [];
}
