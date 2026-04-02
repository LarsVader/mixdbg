using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record ThreadsResponseBody
{
    [JsonPropertyName("threads")]
    public DapThread[] Threads { get; set; } = [];
}
