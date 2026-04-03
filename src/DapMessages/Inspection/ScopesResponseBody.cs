using System.Text.Json.Serialization;

namespace MixDbg.Dap;

public record ScopesResponseBody
{
    [JsonPropertyName("scopes")]
    public Scope[] Scopes { get; set; } = [];
}
