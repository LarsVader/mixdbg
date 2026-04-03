using MixDbg.Dap;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless DAP request dispatcher. Routes incoming DAP commands to
/// registered handler functions and manages the request/response lifecycle.
/// All mutable state lives in <see cref="DapDispatcherModel"/>.
/// </summary>
public interface IDapDispatcher
{
    /// <summary>Creates a new dispatcher model with an empty handler registry.</summary>
    DapDispatcherModel CreateModel();

    /// <summary>Registers a raw handler for a DAP command.</summary>
    void Register(DapDispatcherModel model, string command, Func<RequestMessage, object?> handler);

    /// <summary>Registers a typed handler that deserializes arguments before invoking.</summary>
    void Register<TArgs>(DapDispatcherModel model, string command, Func<TArgs, object?> handler);

    /// <summary>Reads and dispatches requests until EOF or disconnect.</summary>
    void Run(DapDispatcherModel model);
}
