using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record ContinueArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }
}
