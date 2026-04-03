namespace MixDbg.Dap;

/// <summary>
/// Thrown by the disconnect handler to cleanly exit the dispatch loop.
/// </summary>
public sealed class DisconnectException : Exception;
