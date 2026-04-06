namespace MixDbg.Services;

/// <summary>
/// Stateless DAP request dispatcher. Routes incoming DAP commands to
/// registered handler services and manages the request/response lifecycle.
/// </summary>
public interface IDapDispatcher
{
    /// <summary>Reads and dispatches requests until EOF or disconnect.</summary>
    void Run();
}