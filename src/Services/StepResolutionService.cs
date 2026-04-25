using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless service that resolves what action to take after a debug stop during
/// stepping. Determines stop reasons, validates step landings, and manages the
/// lifecycle of managed step state (temp BPs, one-shot sites).
/// All methods must be called on the engine thread.
/// </summary>
internal sealed class StepResolutionService(
    ILoggingService _log,
    LogStore _logStore,
    IDbgEngWrapper _wrapper) : IStepResolutionService
{
    /// <inheritdoc />
    public StopReason DetermineStopReason(NativeDebuggerModel model)
    {
        if (model.ActiveManagedStep != null)
        {
            bool hadBpHit = model.HitUserBreakpoint;
            StopReason managedResult = ResolveManagedStep(model);
            // If the managed step path consumed the BP hit (suppressed at depth),
            // return Continue directly — don't fall through to simple stop resolution
            // which would misread the still-set Stepping flag.
            if (managedResult != StopReason.Continue || hadBpHit)
                return managedResult;
        }

        return ResolveSimpleStopReason(model);
    }

    /// <inheritdoc />
    public StepAutoAction CheckStepLanding(NativeDebuggerModel model)
    {
        NativeStackFrame[] frames = _wrapper.GetStackTrace(model.Wrapper, 1);
        if (frames.Length == 0)
            return StepAutoAction.None;

        if (IsStackDeeperThanOrigin(model, frames[0].StackOffset))
            return StepAutoAction.ReStep;

        ulong ip = frames[0].InstructionOffset;
        (uint Line, string File)? lineInfo = _wrapper.GetLineByOffset(model.Wrapper, ip);

        if (lineInfo is not { } info || info.Line == 0)
        {
            _log.LogVerbose(_logStore, $"CheckStepLanding: ip=0x{ip:X} no source → StepOut");
            return StepAutoAction.StepOut;
        }

        return IsSameLineAsOrigin(model, info.File, (int)info.Line)
            || IsClosingBrace(model, info.File, (int)info.Line)
            ? StepAutoAction.ReStep
            : StepAutoAction.None;
    }

    /// <inheritdoc />
    public void CompleteManagedStep(NativeDebuggerModel model)
    {
        if (model.ActiveManagedStep == null)
            return;

        foreach (uint bpId in model.ActiveManagedStep.TempBreakpointIds)
        {
            _ = _wrapper.RemoveBreakpoint(model.Wrapper, bpId);
            _ = model.UserBreakpointIds.Remove(bpId);
        }

        _log.LogInfo(_logStore,
            $"Managed step complete: removed {model.ActiveManagedStep.TempBreakpointIds.Count} temp BPs");
        model.ActiveManagedStep = null;
        model.StepOriginLocation = null;
        // Clear stale LastHitBpId so the next step's LastContinuedBpId doesn't match
        // a recycled dbgeng BP ID and falsely suppress it.
        model.LastHitBpId = uint.MaxValue;
    }

    /// <summary>
    /// Resolves the stop reason when an active managed step is in progress.
    /// Returns the resolved <see cref="StopReason"/>, or <see cref="StopReason.Continue"/>
    /// to fall through to simple stop-reason handling.
    /// </summary>
    private StopReason ResolveManagedStep(NativeDebuggerModel model)
    {
        if (model.HitUserBreakpoint)
            return ResolveManagedStepBreakpointHit(model);

        // Non-BP stop during managed step (e.g. exception) — cancel step, fall through.
        if (model.Stepping || model.PauseRequested)
            CompleteManagedStep(model);

        return StopReason.Continue;
    }

    /// <summary>
    /// Handles a breakpoint hit during an active managed step. Determines whether the
    /// hit is a temp BP (step completion), a step-into one-shot, or a real user BP.
    /// Returns <see cref="StopReason.Continue"/> when the temp BP fires at a recursive depth and should be suppressed.
    /// </summary>
    private StopReason ResolveManagedStepBreakpointHit(NativeDebuggerModel model)
    {
        bool isTempBp = model.ActiveManagedStep!.TempBreakpointIds.Contains(model.LastHitBpId);

        if (isTempBp && ShouldSuppressTempBpAtDepth(model))
        {
            model.HitUserBreakpoint = false;
            return StopReason.Continue;
        }

        LogManagedStepBpHit(model, isTempBp);

        bool isStepIntoEnterBp = !isTempBp && IsStepIntoOneShotHit(model);
        model.HitUserBreakpoint = false;

        RemoveStepIntoOneShotSites(model);
        CompleteManagedStep(model);

        return isTempBp || isStepIntoEnterBp ? StopReason.Step : StopReason.Breakpoint;
    }

    /// <summary>
    /// Checks if a temp BP fired inside a recursive call (deeper stack than step origin)
    /// and suppresses it if so. Returns <c>true</c> if the hit was suppressed.
    /// </summary>
    private bool ShouldSuppressTempBpAtDepth(NativeDebuggerModel model)
    {
        if (model.ActiveManagedStep!.OriginStackPointer == 0)
            return false;

        NativeStackFrame[] frames = _wrapper.GetStackTrace(model.Wrapper, 1);
        if (frames.Length == 0 || frames[0].StackOffset >= model.ActiveManagedStep.OriginStackPointer)
            return false;

        _log.LogInfo(_logStore,
            $"Managed step temp BP suppressed: RSP 0x{frames[0].StackOffset:X} < origin 0x{model.ActiveManagedStep.OriginStackPointer:X}");
        return true;
    }

    private void LogManagedStepBpHit(NativeDebuggerModel model, bool isTempBp)
    {
        string message = (isTempBp, model.ActiveManagedStep!.OriginStackPointer) switch
        {
            (true, 0) => $"DetermineStop: temp BP id={model.LastHitBpId} originDepth=0 (depth check skipped)",
            (true, _) => $"DetermineStop: temp BP id={model.LastHitBpId} at valid depth (depth check passed)",
            _ => $"DetermineStop: non-temp BP id={model.LastHitBpId} during active step",
        };
        _log.LogInfo(_logStore, message);
    }

    /// <summary>
    /// Resolves a simple (non-managed-step) stop reason from model flags.
    /// Priority: breakpoint > step > pause > null (auto-continue).
    /// </summary>
    private StopReason ResolveSimpleStopReason(NativeDebuggerModel model)
    {
        if (model.HitUserBreakpoint)
            return ResolveBreakpointDuringStep(model);

        if (model.Stepping)
        {
            model.Stepping = false;
            return StopReason.Step;
        }

        if (model.PauseRequested)
        {
            model.PauseRequested = false;
            return StopReason.Pause;
        }

        return StopReason.Continue;
    }

    /// <summary>
    /// Handles a user breakpoint hit, with special suppression logic when a native
    /// step-over lands inside a recursive call (deeper stack than step origin).
    /// </summary>
    private StopReason ResolveBreakpointDuringStep(NativeDebuggerModel model)
    {
        if (model.Stepping && model.StepOriginStackPointer > 0)
        {
            NativeStackFrame[] frames = _wrapper.GetStackTrace(model.Wrapper, 1);
            if (frames.Length > 0 && frames[0].StackOffset < model.StepOriginStackPointer)
            {
                _log.LogInfo(_logStore,
                    $"Native step BP suppressed: RSP 0x{frames[0].StackOffset:X} < origin 0x{model.StepOriginStackPointer:X}");
                model.HitUserBreakpoint = false;
                // Stepping stays true — caller uses it to re-step instead of Go.
                return StopReason.Continue;
            }
        }

        model.HitUserBreakpoint = false;
        model.Stepping = false;
        return StopReason.Breakpoint;
    }

    private bool IsStackDeeperThanOrigin(NativeDebuggerModel model, ulong currentRsp)
    {
        if (model.StepOriginStackPointer == 0)
            return false;

        if (currentRsp >= model.StepOriginStackPointer)
            return false;

        _log.LogInfo(_logStore,
            $"CheckStepLanding: deeper stack (RSP 0x{currentRsp:X} < origin 0x{model.StepOriginStackPointer:X}) → ReStep");
        return true;
    }

    private bool IsSameLineAsOrigin(NativeDebuggerModel model, string file, int line)
    {
        if (model.StepOriginLocation is not { } origin)
            return false;

        if (origin.Line != line || !string.Equals(origin.File, file, StringComparison.OrdinalIgnoreCase))
            return false;

        _log.LogVerbose(_logStore, $"CheckStepLanding: same line {line} → ReStep");
        return true;
    }

    private bool IsClosingBrace(NativeDebuggerModel model, string file, int line)
    {
        if (!model.SourceFileCache.TryGetValue(file, out string[]? lines))
        {
            try
            {
                if (File.Exists(file))
                {
                    lines = File.ReadAllLines(file);
                    model.SourceFileCache[file] = lines;
                }
            }
            catch { }
        }

        int lineIndex = line - 1;
        if (lines == null
                || lineIndex < 0
                || lineIndex >= lines.Length
                || lines[lineIndex].Trim() is not string trimmed
                || trimmed != "}" && trimmed != "};")
        {
            return false;
        }

        _log.LogVerbose(_logStore, $"CheckStepLanding: closing brace at {file}:{line} → ReStep");
        return true;
    }

    /// <summary>
    /// Returns true if the breakpoint that just fired (<see cref="NativeDebuggerModel.LastHitBpId"/>)
    /// matches an installed one-shot step-into site. Only checks sites whose installed hardware
    /// breakpoint ID equals the last hit ID, preventing misclassification of unrelated user BPs.
    /// </summary>
    private static bool IsStepIntoOneShotHit(NativeDebuggerModel model)
    {
        foreach (ManagedMethodBreakpointPlan plan in model.ManagedBpPlans.Values)
        {
            foreach (MethodBreakpointSite site in plan.Sites)
            {
                if (!site.IsStepIntoOneShot)
                    continue;
                string siteKey = $"{site.FilePath}:{site.Line}";
                if (model.BreakpointIds.TryGetValue(siteKey, out uint bpId) && bpId == model.LastHitBpId)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes one-shot step-into sites from their plans, including their installed
    /// hardware breakpoints so they don't fire during subsequent step-over operations.
    /// </summary>
    private void RemoveStepIntoOneShotSites(NativeDebuggerModel model)
    {
        List<(int Token, string Assembly)> emptyKeys = [];
        foreach (KeyValuePair<(int Token, string Assembly), ManagedMethodBreakpointPlan> kv in model.ManagedBpPlans)
        {
            List<MethodBreakpointSite> oneShotSites = kv.Value.Sites.FindAll(s => s.IsStepIntoOneShot);
            if (oneShotSites.Count == 0)
            {
                if (kv.Value.Sites.Count == 0)
                    emptyKeys.Add(kv.Key);
                continue;
            }

            _ = model.ActiveMethodBreakpoints.TryGetValue(kv.Key, out ActiveMethodBreakpoint? active);
            foreach (MethodBreakpointSite site in oneShotSites)
                RemoveOneShotSite(model, kv.Key, site, active);

            _ = kv.Value.Sites.RemoveAll(s => s.IsStepIntoOneShot);
            if (kv.Value.Sites.Count == 0)
                emptyKeys.Add(kv.Key);
        }

        foreach ((int Token, string Assembly) k in emptyKeys)
            _ = model.ManagedBpPlans.Remove(k);
    }

    private void RemoveOneShotSite(
        NativeDebuggerModel model,
        (int Token, string Assembly) methodKey,
        MethodBreakpointSite site,
        ActiveMethodBreakpoint? active)
    {
        string siteKey = $"{site.FilePath}:{site.Line}";
        if (model.BreakpointIds.TryGetValue(siteKey, out uint bpId))
        {
            _ = _wrapper.RemoveBreakpoint(model.Wrapper, bpId);
            _ = model.UserBreakpointIds.Remove(bpId);
            _ = model.ManagedBreakpointIds.Remove(bpId);
            _ = model.BreakpointIds.Remove(siteKey);
            _ = active?.InstalledBpIds.Remove(bpId);
        }

        // Clean up address tracking via JIT mapping. Keep ManagedBreakpointSources
        // because the user may request a stack trace after the step completes.
        if (model.JitMethodMappings.TryGetValue(methodKey, out JitMethodMapping? mapping))
        {
            ulong addr = mapping.GetNativeAddress(site.ILOffset);
            _ = model.ManagedBreakpointAddresses.Remove(addr);
            _ = active?.InstalledAddresses.Remove(addr);
        }
    }
}
