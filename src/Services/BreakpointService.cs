using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless breakpoint management service. Handles setting, removing, and
/// responding to breakpoint hits. All state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
internal sealed class BreakpointService(
    IDapServer _server,
    DapServerModel _transport,
    ILoggingService _log,
    LogStore _logStore,
    ISourceFileService _sourceFiles,
    IManagedBreakpointService _managedBp,
    IDbgEngWrapper _wrapper) : IBreakpointService
{
    public Breakpoint[] SetBreakpointsOnEngine(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested)
    {
        _log.LogInfo(_logStore, $"SetBreakpointsOnEngine: file={filePath} count={requested.Length}");
        foreach (SourceBreakpoint r in requested)
            _log.LogInfo(_logStore, $"  requested: line={r.Line}");

        // DAP sends setBreakpoints per source file, so all requested breakpoints share the
        // same filePath and are either all managed or all native — no mixing within one call.
        Breakpoint[]? managedResult = TrySetManagedBreakpoints(model, filePath, requested);
        if (managedResult != null)
            return managedResult;

        RemoveExistingBreakpoints(model, filePath);

        Breakpoint[] results = new Breakpoint[requested.Length];
        for (int i = 0; i < requested.Length; i++)
            results[i] = SetNativeBreakpoint(model, filePath, requested[i]);

        return results;
    }

    /// <summary>
    /// Delegates breakpoint setting to the managed debugger if the file is not native.
    /// Returns <c>null</c> if the file is native and should use the dbgeng path.
    /// </summary>
    private Breakpoint[]? TrySetManagedBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested)
    {
        if (_sourceFiles.IsNativeFile(filePath))
            return null;

        if (model.ManagedInitialized)
        {
            _log.LogInfo(_logStore, $"  Delegating to managed debugger: {filePath}");
            return _managedBp.SetManagedBreakpoints(model, filePath, requested);
        }

        // CLR not loaded yet — store as pending, return optimistic verified: true.
        _log.LogInfo(_logStore, $"  CLR not ready, storing as pending managed bp: {filePath}");
        model.PendingManagedBreakpoints.Add(new SetBreakpointsArguments
        {
            Source = CreateSource(filePath),
            Breakpoints = requested,
        });
        return [.. requested.Select((bp, i) => new Breakpoint
        {
            Id = ++model.NextBpId,
            Verified = true,
            Line = bp.Line,
            Source = CreateSource(filePath),
            Message = "Pending — managed debugger not yet initialized",
        })];
    }

    /// <summary>
    /// Removes all existing native breakpoints for the given file before re-setting them.
    /// </summary>
    private void RemoveExistingBreakpoints(NativeDebuggerModel model, string filePath)
    {
        DbgEngWrapperModel w = model.Wrapper;
        List<string> keysToRemove = [.. model.BreakpointIds.Keys.Where(k => k.StartsWith(filePath + ":", StringComparison.OrdinalIgnoreCase))];
        foreach (string key in keysToRemove)
        {
            if (model.BreakpointIds.TryGetValue(key, out uint oldId))
            {
                _ = _wrapper.RemoveBreakpoint(w, oldId);
                _ = model.UserBreakpointIds.Remove(oldId);
                _ = model.BreakpointIds.Remove(key);
            }
        }
    }

    /// <summary>
    /// Sets a single native breakpoint via dbgeng. Tries direct offset resolution first,
    /// falls back to a deferred breakpoint (<c>bu</c> command) if the module isn't loaded yet.
    /// </summary>
    private Breakpoint SetNativeBreakpoint(NativeDebuggerModel model, string filePath, SourceBreakpoint req)
    {
        DbgEngWrapperModel w = model.Wrapper;
        string key = $"{filePath}:{req.Line}";

        (ulong offset, bool resolved) = _wrapper.GetOffsetByLine(w, (uint)req.Line, filePath);
        _log.LogInfo(_logStore, $"  GetOffsetByLine({req.Line}, {filePath}) -> resolved={resolved} offset=0x{offset:X}");

        if (!resolved)
            return SetDeferredBreakpoint(model, filePath, req, key);

        (uint bpId, bool bpOk) = _wrapper.AddCodeBreakpoint(w, offset);
        if (!bpOk)
        {
            return new Breakpoint
            {
                Id = ++model.NextBpId,
                Verified = false,
                Line = req.Line,
                Message = "Failed to create breakpoint",
            };
        }

        model.BreakpointIds[key] = bpId;
        _ = model.UserBreakpointIds.Add(bpId);

        // Resolve back to verify the actual line
        int actualLine = req.Line;
        (uint Line, string File)? lineInfo = _wrapper.GetLineByOffset(w, offset);
        if (lineInfo != null)
            actualLine = (int)lineInfo.Value.Line;

        return new Breakpoint
        {
            Id = (int)bpId,
            Verified = true,
            Line = actualLine,
            Source = CreateSource(filePath),
        };
    }

    /// <summary>
    /// Sets a deferred breakpoint via the <c>bu</c> command when the module isn't loaded yet.
    /// </summary>
    private Breakpoint SetDeferredBreakpoint(NativeDebuggerModel model, string filePath, SourceBreakpoint req, string key)
    {
        DbgEngWrapperModel w = model.Wrapper;
        _log.LogInfo(_logStore, $"  Trying deferred breakpoint: bu `{filePath}:{req.Line}`");
        (uint deferredId, bool buOk) = _wrapper.AddDeferredBreakpoint(w, filePath, req.Line);
        _log.LogInfo(_logStore, $"  bu result: ok={buOk} id={deferredId}");

        if (!buOk)
        {
            return new Breakpoint
            {
                Id = ++model.NextBpId,
                Verified = false,
                Line = req.Line,
                Message = "Could not resolve source line",
            };
        }

        model.BreakpointIds[key] = deferredId;
        _ = model.UserBreakpointIds.Add(deferredId);
        return new Breakpoint
        {
            Id = (int)deferredId,
            Verified = true,
            Line = req.Line,
            Source = CreateSource(filePath),
        };
    }

    public void HandleBreakpointHit(NativeDebuggerModel model, uint breakpointId)
    {
        model.LastHitBpId = breakpointId;
        model.HitUserBreakpoint = model.UserBreakpointIds.Contains(breakpointId)
            || model.ManagedBreakpointIds.Contains(breakpointId);
        _log.LogInfo(_logStore, $"OnBreakpoint: id={breakpointId} isUser={model.HitUserBreakpoint} (native: [{string.Join(",", model.UserBreakpointIds)}] managed: [{string.Join(",", model.ManagedBreakpointIds)}])");

        // Send verified update so nvim-dap clears the "rejected" marker.
        if (!model.HitUserBreakpoint)
        {
            return;
        }
        // Find the source:line for this breakpoint ID
        KeyValuePair<string, uint> entry = model.BreakpointIds.FirstOrDefault(kv => kv.Value == breakpointId);
        if (entry.Key == null)
        {
            return;
        }
        string[] parts = entry.Key.Split(':', 2);
        string path = parts[0];
        int line = int.TryParse(parts[1], out int l) ? l : 0;

        _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
        {
            Reason = "changed",
            Breakpoint = new Breakpoint
            {
                Id = (int)breakpointId,
                Verified = true,
                Line = line,
                Source = CreateSource(path),
            },
        });
    }

    public void HandleExceptionBreakpoint(NativeDebuggerModel model, ulong address)
    {
        // Check if this EXCEPTION_BREAKPOINT is from a managed IL breakpoint.
        if (!model.ManagedInitialized ||
                !model.ManagedBreakpointAddresses.Contains(address) &&
                 ((model.CorWrapper?.HasLegacyBreakpoints) != true || model.UserBreakpointIds.Contains(model.LastHitBpId)))
        {
            return;
        }
        model.HitUserBreakpoint = true;

        // Resolve the hw BP ID so RemoveTransientManagedBreakpoints knows which
        // transient BP was hit (needed when multiple BPs target the same method).
        if (model.ManagedBreakpointSources.TryGetValue(address, out (string File, int Line) source))
        {
            string key = $"{source.File}:{source.Line}";
            if (model.BreakpointIds.TryGetValue(key, out uint bpId))
                model.LastHitBpId = bpId;
        }

        _log.LogInfo(_logStore, $"Managed breakpoint hit at 0x{address:X}");
    }

    /// <summary>
    /// Creates a DAP <see cref="Source"/> from a file path.
    /// </summary>
    private static Source CreateSource(string filePath) => new()
    {
        Name = Path.GetFileName(filePath),
        Path = filePath,
    };
}
