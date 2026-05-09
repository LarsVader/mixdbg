namespace MixDbg.Services.Interfaces;

/// <summary>
/// Optional companion interface to <see cref="IDapHandlerService"/>. The
/// dispatcher invokes <see cref="OnAfterResponse"/> immediately after the
/// response for the request has been written to the DAP transport, before
/// processing the next request. Used by handlers that must emit events in a
/// specific order relative to the response (e.g. <c>InitializeRequestHandler</c>
/// must send the <c>initialized</c> event AFTER the initialize response —
/// nvim-dap's <c>event_initialized</c> handler synchronously calls
/// <c>set_breakpoints</c>; with no breakpoints, the on-done callback fires
/// before the initialize response has been processed and
/// <c>configurationDone</c> is skipped, hanging the engine forever).
/// </summary>
public interface IDapAfterResponseAction
{
    /// <summary>
    /// Invoked on the dispatcher thread after the response is sent. Implementations
    /// should not throw — the response has already gone out, so an exception here
    /// cannot be surfaced to the client as an error response. The dispatcher logs
    /// and swallows any exception thrown from this method, including
    /// <c>DisconnectException</c>: throwing <c>DisconnectException</c> from
    /// <see cref="OnAfterResponse"/> does NOT shut down the dispatcher loop
    /// (only <see cref="IDapHandlerService.Execute"/> can do that).
    /// Implementations should also avoid blocking: the dispatcher is single-threaded,
    /// so a slow <see cref="OnAfterResponse"/> delays the next <c>ReadRequest</c>.
    /// </summary>
    void OnAfterResponse();
}
