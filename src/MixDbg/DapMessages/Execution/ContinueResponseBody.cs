using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record ContinueResponseBody
{
    [JsonPropertyName("allThreadsContinued")]
    public bool AllThreadsContinued { get; set; } = true;
}
