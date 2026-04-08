using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless deferred managed breakpoint resolution service. Handles JIT notifications
/// from the CLR profiler, DAC-based polling, module load binding, and profiler ENTER
/// hook breakpoints. All methods execute on the engine thread.
/// </summary>
internal sealed class ManagedBreakpointResolverService(
    ILoggingService _log,
    LogStore _logStore,
    IDapServer _server,
    DapServerModel _transport,
    IDbgEngWrapper _dbgEng,
    ICorDebugWrapper _corDebug,
    IManagedBreakpointService _bpService) : IManagedBreakpointResolver
{
    public Breakpoint[] TryResolveDeferredBreakpoints(NativeDebuggerModel model)
    {
        if (model.DeferredManagedBreakpoints.Count == 0)
            return [];

        _log.LogInfo(_logStore, $"TryResolveDeferredBreakpoints: {model.DeferredManagedBreakpoints.Count} deferred");

        // Recreate the DAC so it sees the latest JIT state.
        _corDebug.FlushProcessState(model.CorWrapper);
        try { _ = _corDebug.InitializeDac(model.CorWrapper, model.Wrapper, model.CoreClrPath!, model.CoreClrBaseAddress); }
        catch { }

        List<Breakpoint> resolved = [];
        List<DeferredManagedBreakpoint> bound = [];

        foreach (DeferredManagedBreakpoint deferred in model.DeferredManagedBreakpoints)
        {
            try
            {
                // Use the DAC (XCLRDataProcess) to find the real JIT native entry point.
                ulong nativeAddress = _corDebug.ResolveNativeEntryViaXclrData(model.CorWrapper, deferred.MethodToken, deferred.AssemblyName);
                if (nativeAddress == 0)
                    continue;

                _log.LogInfo(_logStore, $"  Deferred bp #{deferred.BpId}: XCLRData entry=0x{nativeAddress:X}");

                uint? hwBpId = _bpService.SetManagedCodeBreakpoint(model, nativeAddress, deferred.FilePath, deferred.Line);
                if (hwBpId != null)
                {
                    bound.Add(deferred);
                    resolved.Add(new Breakpoint
                    {
                        Id = deferred.BpId,
                        Verified = true,
                        Line = deferred.Line,
                        Source = new Source
                        {
                            Name = Path.GetFileName(deferred.FilePath),
                            Path = deferred.FilePath,
                        },
                    });
                    _log.LogInfo(_logStore, $"  Resolved deferred bp #{deferred.BpId} -> hw bp #{hwBpId} at 0x{nativeAddress:X}");
                }
                else
                {
                    bound.Add(deferred);
                    resolved.Add(new Breakpoint
                    {
                        Id = deferred.BpId,
                        Verified = false,
                        Line = deferred.Line,
                        Source = new Source
                        {
                            Name = Path.GetFileName(deferred.FilePath),
                            Path = deferred.FilePath,
                        },
                        Message = "Failed to set managed breakpoint",
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"  Deferred resolution failed for bp #{deferred.BpId}: {ex.Message}");
            }
        }

        foreach (DeferredManagedBreakpoint r in bound)
            _ = model.DeferredManagedBreakpoints.Remove(r);

        return [.. resolved];
    }

    public Breakpoint[] HandleJitNotifications(NativeDebuggerModel model)
    {
        if (model.DeferredManagedBreakpoints.Count == 0 || model.JitNotifications.IsEmpty)
            return [];

        List<Breakpoint> resolved = [];
        List<DeferredManagedBreakpoint> bound = [];

        // Drain all pending JIT notifications from the profiler pipe.
        while (model.JitNotifications.TryDequeue(out JitNotification? jit))
            TryMatchJitToDeferred(model, jit, resolved, bound);

        foreach (DeferredManagedBreakpoint r in bound)
            _ = model.DeferredManagedBreakpoints.Remove(r);

        // Signal the ACK event to unblock the profiler's JITCompilationFinished callback.
        // The hardware BP is now set, so when the profiler unblocks and the CLR dispatches
        // to the freshly JIT'd code, the BP will fire immediately.
        if (resolved.Count > 0)
            _ = (model.ProfilerAckEvent?.Set());

        return [.. resolved];
    }

    /// <summary>
    /// Matches a single JIT notification against all deferred breakpoints by token + assembly name.
    /// If hooks are active, skips the BP (ENTER path handles it). Otherwise sets a hardware BP.
    /// </summary>
    private void TryMatchJitToDeferred(
        NativeDebuggerModel model, JitNotification jit,
        List<Breakpoint> resolved, List<DeferredManagedBreakpoint> bound)
    {
        foreach (DeferredManagedBreakpoint deferred in model.DeferredManagedBreakpoints)
        {
            if (deferred.MethodToken != jit.MethodToken ||
                deferred.AssemblyName == null ||
                !deferred.AssemblyName.Equals(jit.AssemblyName, StringComparison.OrdinalIgnoreCase) ||
                bound.Contains(deferred))
            {
                continue;
            }

            _log.LogInfo(_logStore,
                $"  JIT notification matched deferred bp #{deferred.BpId}: " +
                $"token=0x{jit.MethodToken:X8} addr=0x{jit.NativeAddress:X} asm={jit.AssemblyName}");

            // Check if this method has ENTER hooks active. When hooks are active
            // and we have IL-to-native mapping, the ENTER path sets a transient BP
            // at the exact breakpointed line (more precise than method entry).
            string bpKey = $"{jit.AssemblyName}:{jit.MethodToken:X8}";
            bool hasEnterHooks = model.ProfilerHooksActive &&
                model.JitMethodMappings.ContainsKey(bpKey);
            if (hasEnterHooks)
            {
                _log.LogInfo(_logStore, $"  Hooks active: stored address, ENTER will set BP");
                continue;
            }

            // Without hooks: set hardware BP now.
            uint? hwBpId = _bpService.SetManagedCodeBreakpoint(model, jit.NativeAddress, deferred.FilePath, deferred.Line);

            bound.Add(deferred);
            resolved.Add(new Breakpoint
            {
                Id = deferred.BpId,
                Verified = hwBpId != null,
                Line = deferred.Line,
                Source = new Source
                {
                    Name = Path.GetFileName(deferred.FilePath),
                    Path = deferred.FilePath,
                },
                Message = hwBpId == null ? "Failed to set hardware breakpoint" : null,
            });
        }
    }

    public Breakpoint[] OnModuleLoad(NativeDebuggerModel model)
    {
        if (!model.ManagedInitialized)
            return [];

        // Re-enumerate ICorDebug modules to pick up newly loaded assemblies.
        _corDebug.FlushProcessState(model.CorWrapper);
        _corDebug.RefreshModules(model.CorWrapper);

        // Try to bind pending managed breakpoints against the new modules.
        return TryBindPendingBreakpoints(model);
    }

    public void TryBindManagedBreakpointsOnModuleLoad(NativeDebuggerModel model)
    {
        try
        {
            Breakpoint[] resolved = OnModuleLoad(model);
            foreach (Breakpoint bp in resolved)
            {
                _log.LogInfo(_logStore, $"Managed bp bound on module load: id={bp.Id} line={bp.Line}");
                _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                {
                    Reason = "changed",
                    Breakpoint = bp,
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"TryBindManagedBreakpointsOnModuleLoad failed: {ex.Message}");
        }
    }

    public void ProcessPendingManagedBreakpoints(NativeDebuggerModel model)
    {
        // Process JIT notifications from the CLR profiler pipe.
        if (model.DeferredManagedBreakpoints.Count > 0 && !model.JitNotifications.IsEmpty)
            ResolveAndNotify("Profiler JIT", () => HandleJitNotifications(model));

        // Fallback: try to resolve deferred managed breakpoints via DAC/XCLRData.
        // Skip when hooks are active — deferred BPs are consumed by ENTER notifications.
        if (!model.ProfilerHooksActive &&
            model.ManagedInitialized && model.DeferredManagedBreakpoints.Count > 0)
        {
            ResolveAndNotify("Deferred managed", () => TryResolveDeferredBreakpoints(model));
        }
    }

    /// <summary>
    /// Resolves breakpoints via the given resolver function and sends DAP breakpoint
    /// change events for each result. Logs and swallows exceptions.
    /// </summary>
    private void ResolveAndNotify(string label, Func<Breakpoint[]> resolver)
    {
        try
        {
            Breakpoint[] resolved = resolver();
            foreach (Breakpoint bp in resolved)
            {
                _log.LogInfo(_logStore, $"{label} bp resolved: id={bp.Id} verified={bp.Verified}");
                _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                {
                    Reason = "changed",
                    Breakpoint = bp,
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"{label} resolution failed: {ex.Message}");
        }
    }

    public bool HandleEnterBreakpoint(NativeDebuggerModel model)
    {
        if (!model.ProfilerHooksActive || !model.PendingEnterBreakpoint)
            return false;

        model.PendingEnterBreakpoint = false;
        // Find all matching deferred BPs and compute exact native addresses
        // from the IL-to-native mapping (resolves breakpoint line → native offset).
        // Multiple BPs may target the same method (e.g. lines before and after a native call).
        string bpKey = $"{model.EnterBreakpointAssembly}:{model.EnterBreakpointToken:X8}";
        bool matched = false;
        foreach (DeferredManagedBreakpoint deferred in model.DeferredManagedBreakpoints)
        {
            if (deferred.MethodToken == model.EnterBreakpointToken &&
                deferred.AssemblyName != null &&
                deferred.AssemblyName.Equals(model.EnterBreakpointAssembly, StringComparison.OrdinalIgnoreCase))
            {
                // Use IL-to-native mapping to get the exact address for the BP line.
                ulong addr = model.EnterBreakpointAddress; // fallback: body entry
                if (model.JitMethodMappings.TryGetValue(bpKey, out JitMethodMapping? mapping))
                {
                    addr = mapping.GetNativeAddress(deferred.ILOffset);
                    _log.LogInfo(_logStore,
                        $"  ENTER: IL offset {deferred.ILOffset} -> native 0x{addr:X}");
                }
                _bpService.SetTransientBreakpoint(model, addr, deferred.FilePath, deferred.Line);
                _log.LogInfo(_logStore, $"  ENTER: transient hw BP at 0x{addr:X} for {deferred.FilePath}:{deferred.Line}");
                matched = true;
            }
        }
        // ACK unblocks the profiler (hooks disabled during method body).
        _ = (model.ProfilerAckEvent?.Set());
        if (!matched)
        {
            // Non-BP method from assembly-level watch — re-enable hooks
            // immediately so the next method call also fires ENTER.
            _log.LogInfo(_logStore, $"  ENTER: no match for token=0x{model.EnterBreakpointToken:X8} — rehooking");
            _ = (model.ProfilerRehookEvent?.Set());
        }
        return true;
    }

    public void StartDeferredBreakpointPoller(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, $"Starting deferred BP poller ({model.DeferredManagedBreakpoints.Count} deferred)");
        Timer timer = new(_ =>
        {
            if (model.Terminated || model.DeferredManagedBreakpoints.Count == 0)
                return;
            try
            {
                _dbgEng.SetInterrupt(model.Wrapper);
            }
            catch { }
        }, null, 2000, 2000);

        // Store the timer so it can be disposed.
        model.DisposeAction = () =>
        {
            timer.Dispose();
            model.Terminated = true;
            model.Commands.CompleteAdding();
            _ = (model.EngineThread?.Join(3000));
            model.Commands.Dispose();
            model.Stopped.Dispose();
            model.EngineReady.Dispose();
        };
    }

    // ── Private ─────────────────────────────────────────

    private Breakpoint[] TryBindPendingBreakpoints(NativeDebuggerModel model)
    {
        List<Breakpoint> resolved = [];
        List<PendingManagedBreakpoint> bound = [];

        foreach (PendingManagedBreakpoint pending in model.PendingILBreakpoints)
        {
            if (_bpService.TryBindBreakpoint(model, pending.FilePath, pending.Line, pending.BpId))
            {
                bound.Add(pending);
                resolved.Add(new Breakpoint
                {
                    Id = pending.BpId,
                    Verified = true,
                    Line = pending.Line,
                    Source = new Source
                    {
                        Name = Path.GetFileName(pending.FilePath),
                        Path = pending.FilePath,
                    },
                });
            }
        }

        foreach (PendingManagedBreakpoint r in bound)
            _ = model.PendingILBreakpoints.Remove(r);

        return [.. resolved];
    }
}
