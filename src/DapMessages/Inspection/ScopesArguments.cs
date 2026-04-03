using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record ScopesArguments
{
    [JsonPropertyName("frameId")]
    public int FrameId { get; set; }
}
