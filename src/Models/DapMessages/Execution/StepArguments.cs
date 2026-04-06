using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Models.Dap;

public record StepArguments : IDapMessageArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("granularity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Granularity { get; set; }
}
