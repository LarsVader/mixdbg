using MixDbg.Dap;

namespace MixDbg.Models;

public sealed class DapDispatcherModel
{
    internal Dictionary<string, Func<RequestMessage, object?>> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);
}
