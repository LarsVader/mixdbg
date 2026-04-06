using System.Text.Json.Serialization;
using MixDbg.Services.Interfaces;

namespace MixDbg.Models.Dap;

public record VariablesResponseBody : IDapMessage
{
    [JsonPropertyName("variables")]
    public Variable[] Variables { get; set; } = [];
}
