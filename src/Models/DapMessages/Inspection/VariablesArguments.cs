using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Models.Dap;

public record VariablesArguments : IDapMessageArguments
{
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}
