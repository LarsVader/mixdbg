using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record StepArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("granularity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Granularity { get; set; }
}
