using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record AttachRequestArguments
{
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    [JsonPropertyName("program")]
    public string? Program { get; set; }

    [JsonPropertyName("symbolPath")]
    public string[]? SymbolPath { get; set; }
}
