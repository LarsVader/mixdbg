using System.Text.Json.Serialization;

using MixDbg.Models.Interfaces;

namespace MixDbg.Models.DapMessages.Inspection;

public record ScopesArguments : IDapMessageArguments
{
    [JsonPropertyName("frameId")]
    public int FrameId { get; set; }
}