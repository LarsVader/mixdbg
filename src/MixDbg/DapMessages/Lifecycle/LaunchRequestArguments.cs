using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record LaunchRequestArguments
{
    [JsonPropertyName("program")]
    public string Program { get; set; } = "";

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("symbolPath")]
    public string[]? SymbolPath { get; set; }

    [JsonPropertyName("noDebug")]
    public bool NoDebug { get; set; }
}
