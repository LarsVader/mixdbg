using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record StackTraceResponseBody
{
    [JsonPropertyName("stackFrames")]
    public StackFrame[] StackFrames { get; set; } = [];

    [JsonPropertyName("totalFrames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TotalFrames { get; set; }
}
