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
                // Use the DAC (XCLRDataProcess) to find the JIT native entry point.
                ulong entryAddress = _corDebug.ResolveNativeEntryViaXclrData(model.CorWrapper, deferred.MethodToken, deferred.AssemblyName);
                if (entryAddress == 0)
                    continue;

                // If IL-to-native mapping is available, resolve the exact native address
                // for the target IL offset instead of using the method entry point.
                ulong nativeAddress = entryAddress;
                (int Token, string Assembly) mapKey = (deferred.MethodToken, deferred.AssemblyName!);
                if (model.JitMethodMappings.TryGetValue(mapKey, out JitMethodMapping? mapping))
                {
                    if (deferred.ILOffset > 0)
                    {
                        nativeAddress = mapping.GetNativeAddress(deferred.ILOffset);
                        _log.LogInfo(_logStore, $"  Deferred bp #{deferred.BpId}: XCLRData entry=0x{entryAddress:X} -> IL 0x{deferred.ILOffset:X} at 0x{nativeAddress:X}");
                    }
                    else
                    {
                        _log.LogInfo(_logStore, $"  Deferred bp #{deferred.BpId}: XCLRData entry=0x{entryAddress:X} (IL offset 0 — entry)");
                    }
                }
                else if (deferred.ILOffset > 0)
                {
                    // Symmetric to InstallEagerHardwareBp: at IL offset > 0
                    // without a JIT IL map, we have no way to find the right
                    // native address. Refusing here keeps the BP deferred so
                    // a future JIT/rejit notification with a map can install
                    // it correctly — silently using entryAddress would fire
                    // the BP on method entry every time instead of on the
                    // user's chosen line.
                    _log.LogWarning(_logStore,
                        $"  Deferred bp #{deferred.BpId}: XCLRData entry=0x{entryAddress:X} found, " +
                        $"but no IL map for token=0x{deferred.MethodToken:X8} asm={deferred.AssemblyName}; " +
                        $"refusing to bind at {deferred.FilePath}:{deferred.Line} (IL offset 0x{deferred.ILOffset:X}) " +
                        $"to avoid silently misplacing it on method entry");
                    continue;
                }
                else
                {
                    _log.LogInfo(_logStore, $"  Deferred bp #{deferred.BpId}: XCLRData entry=0x{entryAddress:X} (IL offset 0 — entry; no map)");
                }

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
            MethodBreakpointSite site = new()
            {
                BpId = deferred.BpId,
                ILOffset = deferred.ILOffset,
                FilePath = deferred.FilePath,
                Line = deferred.Line,
            };
            AddSiteToPlan(model, deferred.MethodToken, deferred.AssemblyName, site);

            // Attach mode (IsRejitMode): no ENTER/LEAVE notifications will fire
            // for runtime hooks (COR_PRF_MONITOR_ENTERLEAVE is rejected by the
            // CLR for attached profilers), so install the HW BP immediately at
            // the IL-mapped native address. Subject to the 4-concurrent
            // hardware-debug-register cap until the IL rewriter is implemented.
            bool eagerOk = true;
            if (model.IsRejitMode)
            {
                eagerOk = InstallEagerHardwareBp(
                    model, deferred.MethodToken, deferred.AssemblyName, site, jit.NativeAddress);
            }

            // Only finalize the deferred entry when we actually bound the BP.
            // If eager install failed (e.g. IL map missing), leave it deferred
            // so the periodic DAC poll can retry — orphaning it here would
            // permanently strand the BP unverified with no further attempts.
            if (eagerOk)
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
            }
        }

        if (bound.Count > 0)
        {
            _ = model.DeferredManagedBreakpoints.RemoveAll(bound.Contains);
            model.RebuildDeferredBreakpointIndex();
        }

        // Attach mode: signal the ACK event after processing. The profiler's
        // JITCompilationFinished blocks every watched JIT on this event
        // (500 ms timeout) so MixDbg can install a HW BP before the method
        // body executes. We signal here unconditionally — even when the
        // foreach found no installable site (deferred entry filtered out
        // by AssemblyName==null, or stale plan) — so a stuck watched JIT
        // unblocks instead of hitting the 500 ms timeout.
        //
        // Reaching this code path means a JIT notification was queued by
        // ParseJitNotification, which only enqueues when MatchesDeferred-
        // Breakpoint is true — so this never fires for unrelated/framework
        // JITs (those don't reach the queue).
        //
        // Known limitation: the auto-reset event semantics are imperfect
        // for batched processing — if multiple JIT notifications dequeue
        // back-to-back and each Set fires while no waiter is in
        // WaitForSingleObject yet, the Sets collapse into one signal and
        // only one waiter wakes. Surfaces under heavy concurrent JIT
        // pressure (tier promotion, multi-thread JIT). Documented in
        // CLAUDE.md M7 PARTIAL — proper fix is M9 IL injection (no ACK
        // protocol).
        if (model.IsRejitMode)
            _ = (model.ProfilerAckEvent?.Set());

        return resolved;
    }

    /// <summary>
    /// Installs a permanent HW BP at the IL-mapped native address for an attach-mode
    /// breakpoint. Used in lieu of the ENTER-driven plan activation that requires
    /// runtime ENTER/LEAVE hooks (unavailable to attached profilers). The installed
    /// BP is recorded in <see cref="NativeDebuggerModel.ActiveMethodBreakpoints"/>
    /// with an artificial activation count of 1 — never decremented, never removed
    /// while the BP is set. There is no LEAVE-driven cleanup; the BP persists for
    /// the life of the session.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a HW BP was installed at the requested IL offset (or was
    /// already installed there), <c>false</c> if the BP could not be installed
    /// at the correct address — caller should mark the BP as unverified.
    /// </returns>
    private bool InstallEagerHardwareBp(NativeDebuggerModel model, int token, string assembly,
        MethodBreakpointSite site, ulong nativeAddress)
    {
        (int Token, string Assembly) key = (token, assembly);

        // Resolve the exact native address. If the IL→native map is missing
        // we can only honor a BP at IL offset 0 (method entry) — anything else
        // would silently install a permanent BP at the wrong line. Refusing
        // here surfaces the failure to the user as an unverified BP rather
        // than a BP that fires on the wrong line every time.
        ulong addr;
        if (model.JitMethodMappings.TryGetValue(key, out JitMethodMapping? mapping))
        {
            addr = mapping.GetNativeAddress(site.ILOffset);
        }
        else if (site.ILOffset == 0)
        {
            addr = nativeAddress;
        }
        else
        {
            _log.LogWarning(_logStore,
                $"  ATTACH-EAGER: no IL map for token=0x{token:X8} asm={assembly}; " +
                $"refusing to install BP at {site.FilePath}:{site.Line} (IL offset 0x{site.ILOffset:X}) " +
                $"to avoid silently misplacing it on method entry");
            return false;
        }

        if (!model.ActiveMethodBreakpoints.TryGetValue(key, out ActiveMethodBreakpoint? active))
        {
            active = new ActiveMethodBreakpoint { ActivationCount = 1 };
            model.ActiveMethodBreakpoints[key] = active;
        }

        if (active.InstalledAddresses.Contains(addr))
            return true; // Already installed at this address.

        uint? bpId = _bpService.SetManagedCodeBreakpoint(model, addr, site.FilePath, site.Line);
        if (bpId == null)
        {
            _log.LogWarning(_logStore,
                $"  ATTACH-EAGER: HW BP limit reached for site {site.FilePath}:{site.Line} (token=0x{token:X8})");
            return false;
        }
        active.InstalledBpIds.Add(bpId.Value);
        _ = active.InstalledAddresses.Add(addr);
        _log.LogInfo(_logStore,
            $"  ATTACH-EAGER: installed hw BP #{bpId} at 0x{addr:X} for {site.FilePath}:{site.Line}");
        return true;
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
        // Track methods that got HW BPs installed in this batch. Their LEAVE must
        // be deferred — the method body hasn't executed yet (ENTER and LEAVE arrived
        // in the same engine stop). This is bounded: installedThisBatch only contains
        // methods where HandleEnter returned true (first activation, count 0→1).
        // Recursive re-entries (count > 1) return false, so a deferred LEAVE that
        // gets re-enqueued won't be deferred again on the next pass.
        HashSet<(int, string)>? installedThisBatch = null;
        List<ProfilerNotification>? deferred = null;

        while (model.ProfilerNotifications.TryDequeue(out ProfilerNotification? notification))
        {
            drained = true;
            switch (notification)
            {
                case JitNotification jit:
                    // Attach-mode ACK signaling is done inside FoldJitIntoPlans
                    // after the install attempt — see the explanation there.
                    resolved.AddRange(FoldJitIntoPlans(model, jit));
                    break;

                case EnterNotification enter:
                    if (HandleEnter(model, enter))
                    {
                        _ = (installedThisBatch ??= new(DeferredBreakpointKeyComparer.Instance))
                            .Add((enter.MethodToken, enter.AssemblyName));
                    }
                    break;

                case LeaveNotification leave:
                    if (installedThisBatch?.Contains((leave.MethodToken, leave.AssemblyName)) == true)
                    {
                        _log.LogInfo(_logStore,
                            $"  LEAVE deferred: token=0x{leave.MethodToken:X8} has BPs installed in same batch");
                        (deferred ??= []).Add(leave);
                    }
                    else
                    {
                        HandleLeaveOrTailcall(model, leave.MethodToken, leave.AssemblyName);
                    }
                    break;

                case TailcallNotification tailcall:
                    if (installedThisBatch?.Contains((tailcall.MethodToken, tailcall.AssemblyName)) == true)
                    {
                        _log.LogInfo(_logStore,
                            $"  TAILCALL deferred: token=0x{tailcall.MethodToken:X8} has BPs installed in same batch");
                        (deferred ??= []).Add(tailcall);
                    }
                    else
                    {
                        HandleLeaveOrTailcall(model, tailcall.MethodToken, tailcall.AssemblyName);
                    }
                    break;
            }
        }

        // Re-enqueue deferred notifications after draining so they're processed on the
        // next engine stop (after the method body has had a chance to execute and hit the BP).
        if (deferred != null)
        {
            foreach (ProfilerNotification d in deferred)
            {
                model.ProfilerNotifications.Enqueue(d);
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
    /// <returns>True if HW breakpoints were installed (first activation with a plan).</returns>
    private bool HandleEnter(NativeDebuggerModel model, EnterNotification enter)
    {
        (int Token, string Assembly) key = (enter.MethodToken, enter.AssemblyName);

        // Method with no plan (e.g. cleared breakpoint): ACK and skip.
        if (!model.ManagedBpPlans.TryGetValue(key, out ManagedMethodBreakpointPlan? plan))
        {
            _log.LogVerbose(_logStore,
                $"  ENTER: no plan for token=0x{enter.MethodToken:X8} asm={enter.AssemblyName} — ACK-only");
            _ = (model.ProfilerAckEvent?.Set());
            return false;
        }

        // Nested/recursive entry — count++ and ACK immediately (HW BP already installed).
        if (model.ActiveMethodBreakpoints.TryGetValue(key, out ActiveMethodBreakpoint? active))
        {
            active.ActivationCount++;
            _log.LogVerbose(_logStore,
                $"  ENTER: nested activation for token=0x{enter.MethodToken:X8} (count={active.ActivationCount})");
            _ = (model.ProfilerAckEvent?.Set());
            return false;
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

            // The ENTER hook blocks at BodyAddress. After ACK, execution resumes there.
            // If the BP address is before BodyAddress, the BP would be skipped — use
            // BodyAddress as a minimum so the BP fires on the first instruction executed.
            if (addr < enter.BodyAddress)
            {
                addr = enter.BodyAddress;
            }

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
        return active.InstalledBpIds.Count > 0;
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

        // Chain the timer dispose onto the existing DisposeAction set by
        // EngineLifecycleService.CreateModel — clobbering it would skip
        // the profiler-pipe / cmd-pipe / ack-event cleanup and leave
        // pipe handles open in the target process (especially bad for
        // attach mode, where the profiler DLL stays loaded).
        Action? prior = model.DisposeAction;
        model.DisposeAction = () =>
        {
            timer.Dispose();
            prior?.Invoke();
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
