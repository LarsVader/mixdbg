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
        HashSet<DeferredManagedBreakpoint> bound = [];

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
                    _ = bound.Add(deferred);
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
                    _ = bound.Add(deferred);
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

        if (bound.Count > 0)
        {
            _ = model.DeferredManagedBreakpoints.RemoveAll(bound.Contains);
            model.RebuildDeferredBreakpointIndex();
        }

        return [.. resolved];
    }

    /// <summary>
    /// Folds a single JIT notification into existing <see cref="NativeDebuggerModel.ManagedBpPlans"/>
    /// entries. When a deferred BP matches, creates/merges a plan entry so the next ENTER
    /// can install HW BPs for all of the method's sites. Does not install HW BPs here —
    /// they are method-lifetime-scoped and only exist while the method has an activation.
    /// </summary>
    private List<Breakpoint> FoldJitIntoPlans(NativeDebuggerModel model, JitNotification jit)
    {
        List<Breakpoint> resolved = [];
        if (model.DeferredManagedBreakpoints.Count == 0)
            return resolved;

        HashSet<DeferredManagedBreakpoint> bound = [];
        foreach (DeferredManagedBreakpoint deferred in model.DeferredManagedBreakpoints)
        {
            if (deferred.MethodToken != jit.MethodToken ||
                deferred.AssemblyName == null ||
                !deferred.AssemblyName.Equals(jit.AssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _log.LogInfo(_logStore,
                $"  JIT notification matched deferred bp #{deferred.BpId}: " +
                $"token=0x{jit.MethodToken:X8} addr=0x{jit.NativeAddress:X} asm={jit.AssemblyName}");

            // Create/merge a method plan. ENTER hook will install the HW BP.
            AddSiteToPlan(
                model, deferred.MethodToken, deferred.AssemblyName,
                new MethodBreakpointSite
                {
                    BpId = deferred.BpId,
                    ILOffset = deferred.ILOffset,
                    FilePath = deferred.FilePath,
                    Line = deferred.Line,
                });

            _ = bound.Add(deferred);
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
        }

        if (bound.Count > 0)
        {
            _ = model.DeferredManagedBreakpoints.RemoveAll(bound.Contains);
            model.RebuildDeferredBreakpointIndex();
        }
        return resolved;
    }

    private static void AddSiteToPlan(
        NativeDebuggerModel model, int token, string assembly, MethodBreakpointSite site)
    {
        (int Token, string Assembly) key = (token, assembly);
        if (!model.ManagedBpPlans.TryGetValue(key, out ManagedMethodBreakpointPlan? plan))
        {
            plan = new ManagedMethodBreakpointPlan
            {
                MethodToken = token,
                AssemblyName = assembly,
            };
            model.ManagedBpPlans[key] = plan;
        }
        // Dedupe on (ILOffset, BpId) so repeated setBreakpoints / JIT-refold doesn't stack sites.
        if (!plan.Sites.Exists(s => s.ILOffset == site.ILOffset && s.BpId == site.BpId))
            plan.Sites.Add(site);
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
        // Fallback: try to resolve deferred managed breakpoints via DAC/XCLRData.
        // Skip when hooks are active — profiler ENTER notifications handle BP installation.
        if (!model.ProfilerHooksActive &&
            model.ManagedInitialized && model.DeferredManagedBreakpoints.Count > 0)
        {
            ResolveAndNotify("Deferred managed", () => TryResolveDeferredBreakpoints(model));
        }
    }

    public bool ProcessProfilerNotifications(NativeDebuggerModel model)
    {
        if (model.ProfilerNotifications.IsEmpty)
            return false;

        List<Breakpoint> resolved = [];
        bool drained = false;

        while (model.ProfilerNotifications.TryDequeue(out ProfilerNotification? notification))
        {
            drained = true;
            switch (notification)
            {
                case JitNotification jit:
                    resolved.AddRange(FoldJitIntoPlans(model, jit));
                    break;

                case EnterNotification enter:
                    HandleEnter(model, enter);
                    break;

                case LeaveNotification leave:
                    HandleLeaveOrTailcall(model, leave.MethodToken, leave.AssemblyName);
                    break;

                case TailcallNotification tailcall:
                    HandleLeaveOrTailcall(model, tailcall.MethodToken, tailcall.AssemblyName);
                    break;
            }
        }

        foreach (Breakpoint bp in resolved)
        {
            _log.LogInfo(_logStore, $"Plan bp resolved: id={bp.Id} verified={bp.Verified}");
            _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
            {
                Reason = "changed",
                Breakpoint = bp,
            });
        }

        // Only signal "auto-resume" when this was a pure bookkeeping stop. A concurrent
        // user BP hit (HitUserBreakpoint = true) takes precedence — let DetermineStopReason
        // report it.
        return drained && !model.HitUserBreakpoint;
    }

    /// <summary>
    /// Processes a single ENTER notification. Installs HW BPs on first entry (count 0→1);
    /// on nested entries, just increments the count. Always signals the ACK event so the
    /// profiler can resume the application thread.
    /// </summary>
    private void HandleEnter(NativeDebuggerModel model, EnterNotification enter)
    {
        (int Token, string Assembly) key = (enter.MethodToken, enter.AssemblyName);

        // Watched method with no plan (assembly-level watch, no BPs): ACK and skip.
        if (!model.ManagedBpPlans.TryGetValue(key, out ManagedMethodBreakpointPlan? plan))
        {
            _log.LogVerbose(_logStore,
                $"  ENTER: no plan for token=0x{enter.MethodToken:X8} asm={enter.AssemblyName} — ACK-only");
            _ = (model.ProfilerAckEvent?.Set());
            return;
        }

        // Nested/recursive entry — count++ and ACK immediately (HW BP already installed).
        if (model.ActiveMethodBreakpoints.TryGetValue(key, out ActiveMethodBreakpoint? active))
        {
            active.ActivationCount++;
            _log.LogVerbose(_logStore,
                $"  ENTER: nested activation for token=0x{enter.MethodToken:X8} (count={active.ActivationCount})");
            _ = (model.ProfilerAckEvent?.Set());
            return;
        }

        // First activation — install HW BPs for every site in the plan.
        active = new ActiveMethodBreakpoint { ActivationCount = 1 };
        model.ActiveMethodBreakpoints[key] = active;

        _ = model.JitMethodMappings.TryGetValue(key, out JitMethodMapping? mapping);
        foreach (MethodBreakpointSite site in plan.Sites)
        {
            ulong addr = mapping != null
                ? mapping.GetNativeAddress(site.ILOffset)
                : enter.BodyAddress;

            uint? bpId = _bpService.SetManagedCodeBreakpoint(model, addr, site.FilePath, site.Line);
            if (bpId == null)
            {
                _log.LogWarning(_logStore,
                    $"  ENTER: HW BP limit reached for site {site.FilePath}:{site.Line} (token=0x{enter.MethodToken:X8})");
                continue;
            }
            active.InstalledBpIds.Add(bpId.Value);
            _ = active.InstalledAddresses.Add(addr);
            _log.LogInfo(_logStore,
                $"  ENTER: installed hw BP #{bpId} at 0x{addr:X} for {site.FilePath}:{site.Line}");
        }

        _ = (model.ProfilerAckEvent?.Set());
    }

    /// <summary>
    /// Processes a LEAVE or TAILCALL notification. Decrements the activation count.
    /// On the last LEAVE (count reaches 0), removes all installed HW BPs for the method.
    /// </summary>
    private void HandleLeaveOrTailcall(NativeDebuggerModel model, int token, string assembly)
    {
        (int Token, string Assembly) key = (token, assembly);
        if (!model.ActiveMethodBreakpoints.TryGetValue(key, out ActiveMethodBreakpoint? active))
            return; // No active record — nothing to do.

        active.ActivationCount--;
        if (active.ActivationCount > 0)
        {
            _log.LogVerbose(_logStore,
                $"  LEAVE: decremented token=0x{token:X8} (count={active.ActivationCount})");
            return;
        }

        // Last activation — remove all HW BPs and drop the entry.
        foreach (uint bpId in active.InstalledBpIds)
        {
            _ = _dbgEng.RemoveBreakpoint(model.Wrapper, bpId);
            _ = model.UserBreakpointIds.Remove(bpId);
            _ = model.ManagedBreakpointIds.Remove(bpId);
        }
        foreach (ulong addr in active.InstalledAddresses)
        {
            _ = model.ManagedBreakpointAddresses.Remove(addr);
            _ = model.ManagedBreakpointSources.Remove(addr);
        }
        // Clear file:line key→id mappings that reference the removed BP IDs.
        List<string> keysToDrop = [.. model.BreakpointIds
            .Where(kv => active.InstalledBpIds.Contains(kv.Value))
            .Select(kv => kv.Key)];
        foreach (string k in keysToDrop)
            _ = model.BreakpointIds.Remove(k);

        _ = model.ActiveMethodBreakpoints.Remove(key);
        _log.LogInfo(_logStore,
            $"  LEAVE: cleared {active.InstalledBpIds.Count} HW BPs for token=0x{token:X8} asm={assembly}");
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

    public void StartDeferredBreakpointPoller(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, $"Starting deferred BP poller ({model.DeferredManagedBreakpoints.Count} deferred)");
        Timer timer = new(_ =>
        {
            if (model.Terminated || model.DeferredManagedBreakpoints.Count == 0)
                return;
            // Skip during cooldown after continue to avoid native BP re-fires.
            if (Environment.TickCount64 - model.ContinueTimestampTicks < 200)
                return;
            // Call SetInterrupt only when engine is in WaitForEvent (safe per dbgeng docs).
            // During other COM operations, cross-thread calls corrupt .NET RCW state.
            if (model.InWaitForEvent)
            {
                try { _dbgEng.SetInterrupt(model.Wrapper); } catch { }
            }
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
