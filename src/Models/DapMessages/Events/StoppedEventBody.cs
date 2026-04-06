using System.Text.Json.Serialization;

namespace MixDbg.Models.Dap;

public record StoppedEventBody
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("allThreadsStopped")]
    public bool AllThreadsStopped { get; set; } = true;
}
