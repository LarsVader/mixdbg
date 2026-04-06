using System.Text.Json.Serialization;

using MixDbg.Services.Interfaces;

namespace MixDbg.Models.Dap;

public record StackTraceResponseBody : IDapMessage
{
    [JsonPropertyName("stackFrames")]
    public StackFrame[] StackFrames { get; set; } = [];

    [JsonPropertyName("totalFrames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TotalFrames { get; set; }
}