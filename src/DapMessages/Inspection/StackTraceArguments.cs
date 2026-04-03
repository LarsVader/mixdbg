using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record StackTraceArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    [JsonPropertyName("levels")]
    public int Levels { get; set; }
}
