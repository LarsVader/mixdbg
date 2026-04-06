using System.Text.Json.Serialization;
using MixDbg.Services.Interfaces;

namespace MixDbg.Dap;

public record ScopesResponseBody : IDapMessage
{
    [JsonPropertyName("scopes")]
    public Scope[] Scopes { get; set; } = [];
}
