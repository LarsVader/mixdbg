using System.Text.Json.Serialization;

using MixDbg.Models.Interfaces;

namespace MixDbg.Models.DapMessages.Inspection;

public record VariablesArguments : IDapMessageArguments
{
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; set; }
}