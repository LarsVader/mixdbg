using MixDbg.Dap;

namespace MixDbg.Models;

/// <summary>
/// Mutable state for the DAP dispatcher. Holds the registry of
/// command name to handler function mappings.
/// </summary>
public sealed class DapDispatcherModel
{
    internal Dictionary<string, Func<RequestMessage, object?>> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);
}
