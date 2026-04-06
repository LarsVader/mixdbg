using System.Text.Json.Serialization;

using MixDbg.Services.Interfaces;

namespace MixDbg.Models.Dap;

public record SetBreakpointsResponseBody : IDapMessage
{
    [JsonPropertyName("breakpoints")]
    public Breakpoint[] Breakpoints { get; set; } = [];
}