using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Dap;

public record AttachRequestArguments : IDapMessageArguments
{
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    [JsonPropertyName("program")]
    public string? Program { get; set; }

    [JsonPropertyName("symbolPath")]
    public string[]? SymbolPath { get; set; }
}
