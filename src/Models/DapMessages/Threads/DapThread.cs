using System.Text.Json.Serialization;

namespace MixDbg.Models.DapMessages.Threads;

public record DapThread
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}