using System.Text.Json.Serialization;

namespace MixDbg.Models.Dap;

public record SourceBreakpoint
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }
}
