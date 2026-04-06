using System.Text.Json.Serialization;

using MixDbg.Models.Interfaces;

namespace MixDbg.Models.DapMessages.Inspection;

public record EvaluateArguments : IDapMessageArguments
{
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("frameId")]
    public int? FrameId { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }
}