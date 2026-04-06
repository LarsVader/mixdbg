using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Models.Dap;

public record StackTraceArguments : IDapMessageArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    [JsonPropertyName("levels")]
    public int Levels { get; set; }
}
