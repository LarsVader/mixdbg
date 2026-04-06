using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Dap;

public record ContinueArguments : IDapMessageArguments
{
    [JsonPropertyName("threadId")]
    public int ThreadId { get; set; }
}
