using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Models.Dap;

public record LaunchRequestArguments : IDapMessageArguments
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
