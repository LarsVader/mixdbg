using System.Text.Json.Serialization;
using MixDbg.Models.Interfaces;

namespace MixDbg.Dap;

public record VariablesArguments : IDapMessageArguments
{
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}
