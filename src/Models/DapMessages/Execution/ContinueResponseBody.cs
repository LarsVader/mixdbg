using System.Text.Json.Serialization;

using MixDbg.Services.Interfaces;

namespace MixDbg.Models.Dap;

public record ContinueResponseBody : IDapMessage
{
    [JsonPropertyName("allThreadsContinued")]
    public bool AllThreadsContinued { get; set; } = true;
}