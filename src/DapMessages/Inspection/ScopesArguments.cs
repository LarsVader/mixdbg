using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Dap;

public record ScopesArguments : IDapMessageArguments
{
    [JsonPropertyName("frameId")]
    public int FrameId { get; set; }
}
