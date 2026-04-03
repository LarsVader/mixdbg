using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record EvaluateArguments
{
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("frameId")]
    public int? FrameId { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }
}
