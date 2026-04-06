using System.Text.Json.Serialization;

using MixDbg.Services.Interfaces;

namespace MixDbg.Models.DapMessages.Threads;

public record ThreadsResponseBody : IDapMessage
{
    [JsonPropertyName("threads")]
    public DapThread[] Threads { get; set; } = [];
}