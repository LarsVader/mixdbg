using System.Text.Json.Serialization;

using MixDbg.Models.DapMessages.Breakpoints;

namespace MixDbg.Models.DapMessages.Events;

public record BreakpointEventBody
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "changed";

    [JsonPropertyName("breakpoint")]
    public Breakpoint Breakpoint { get; set; } = new();
}