using System.Text.Json.Serialization;
using MixDbg.Services.Interfaces;

namespace MixDbg.Dap;

public record ThreadsResponseBody : IDapMessage
{
    [JsonPropertyName("threads")]
    public DapThread[] Threads { get; set; } = [];
}
