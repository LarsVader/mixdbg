using System.Text.Json.Serialization;

using MixDbg.Models.Interfaces;

namespace MixDbg.Models.Dap;

public record SetBreakpointsArguments : IDapMessageArguments
{
    [JsonPropertyName("source")]
    public Source Source { get; set; } = new();

    [JsonPropertyName("breakpoints")]
    public SourceBreakpoint[] Breakpoints { get; set; } = [];
}