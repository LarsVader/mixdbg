using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record SetBreakpointsResponseBody
{
    [JsonPropertyName("breakpoints")]
    public Breakpoint[] Breakpoints { get; set; } = [];
}
