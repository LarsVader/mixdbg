using System.Text.Json.Serialization;

namespace MixDbg.Models.Dap;

public record DapThread
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}