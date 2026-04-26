using MixDbg.Models;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless stepping and execution control service. Handles continue, step over,
/// step into, and step out across native and managed code boundaries.
/// All state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
internal sealed class SteppingService(
    ILoggingService _log,
    LogStore _logStore,
    IManagedDebugger _managedDebugger,
    ICorDebugWrapper _corDebug,
    IPdbSourceMapper _pdbMapper,
    IDbgEngWrapper _wrapper) : ISteppingService
{
    public void ExecuteContinueOnEngine(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Continue executing: SetExecutionStatus(GO)");
        CancelActiveManagedStep(model);
        model.LastContinuedBpId = model.LastHitBpId;
        model.ContinueTimestampTicks = Environment.TickCount64;
        model.ConfigDone = true;
        model.CachedStackTraceResult = null;
        model.CachedThreadsResult = null;
        _wrapper.ClearVariables(model.Wrapper);
        if (model.CorWrapper != null)
            _corDebug.ClearManagedVariables(model.CorWrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
    }

    public void ExecuteStepOnEngine(NativeDebuggerModel model, EngineExecutionStatus stepKind)
    {
        CancelActiveManagedStep(model);
        _wrapper.ClearVariables(model.Wrapper);
        if (model.CorWrapper != null)
            _corDebug.ClearManagedVariables(model.CorWrapper);

        // Check if current IP is in managed code.
        if (model.JitMethodMap.Count > 0)
        {
            NativeStackFrame[] frames = _wrapper.GetStackTrace(model.Wrapper, 2);
            if (frames.Length > 0)
            {
                ulong ip = frames[0].InstructionOffset;
                JitMethodInfo? method;
                lock (model.JitMethodMap)
                {
                    method = ManagedDebuggerService.FindContainingMethod(model, ip);
                }

                if (method != null)
                {
                    if (stepKind == EngineExecutionStatus.StepOver)
                    {
                        if (TryManagedStepOver(model, method, ip))
                            return;
                    }
                    else if (stepKind == EngineExecutionStatus.StepInto)
                    {
                        if (TryManagedStepInto(model, method, ip))
                            return;
                    }
                }
            }
        }

        // Native step — record the current source location so the event loop can detect "no progress."
        NativeStackFrame[] nativeFrames = _wrapper.GetStackTrace(model.Wrapper, 1);
        if (nativeFrames.Length > 0)
        {
            (uint Line, string File)? lineInfo = _wrapper.GetLineByOffset(
                model.Wrapper, nativeFrames[0].InstructionOffset);
            model.StepOriginLocation = lineInfo != null
                ? (lineInfo.Value.File, (int)lineInfo.Value.Line)
                : null;
            model.StepOriginKind = stepKind;
            model.StepReStepCount = 0;
            model.StepIntoEnteredCallee = false;
            // Only record stack depth for step-over. For step-into, a deeper stack
            // (lower RSP) is expected — entering the callee. Without this, the
            // CheckStepLanding depth check would auto-re-step past the callee entry,
            // turning step-into into step-over.
            model.StepOriginStackPointer = stepKind == EngineExecutionStatus.StepInto
                ? 0
                : nativeFrames[0].StackOffset;
        }
        _wrapper.SetExecutionStatus(model.Wrapper, stepKind);
    }

    public void ExecuteStepOutOnEngine(NativeDebuggerModel model)
    {
        CancelActiveManagedStep(model);
        _wrapper.ClearVariables(model.Wrapper);
        if (model.CorWrapper != null)
            _corDebug.ClearManagedVariables(model.CorWrapper);

        // Use temp BP at caller's return address for reliable cross-boundary step-out.
        // The dbgeng "gu" command doesn't stop reliably when returning from native to managed.
        NativeStackFrame[] frames = _wrapper.GetStackTrace(model.Wrapper, 5);
        if (frames.Length >= 2)
        {
            ulong? stepOutTarget = FindStepOutTarget(model, frames);
            if (stepOutTarget != null && SetManagedStepBreakpoint(model, stepOutTarget.Value))
            {
                _log.LogInfo(_logStore, $"Step-out: temp BP at 0x{stepOutTarget.Value:X}");
                model.LastContinuedBpId = model.LastHitBpId;
                _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
                return;
            }
        }

        // Fallback: native step-out via dbgeng "gu".
        _ = _wrapper.ExecuteCommand(model.Wrapper, "gu");
    }

    /// <summary>
    /// Walks the stack from frame[1] upward to find the best step-out target address.
    /// For managed callers with PDB sequence points, advances past the call site to the
    /// next source line. Skips frames without source info (e.g. C++/CLI wrappers that
    /// have no portable PDB) and targets the first ancestor with resolvable source.
    /// </summary>
    private ulong? FindStepOutTarget(NativeDebuggerModel model, NativeStackFrame[] frames)
    {
        for (int i = 1; i < frames.Length; i++)
        {
            ulong address = frames[i].InstructionOffset;
            JitMethodInfo? method;
            lock (model.JitMethodMap)
            {
                method = ManagedDebuggerService.FindContainingMethod(model, address);
            }

            if (method == null)
            {
                // Not in JitMethodMap — could be native code or a C++/CLI thunk.
                // Only use if dbgeng can resolve source; otherwise skip (e.g. JIT helpers).
                (uint Line, string File)? lineInfo = _wrapper.GetLineByOffset(model.Wrapper, address);
                if (lineInfo != null && lineInfo.Value.Line > 0)
                {
                    _log.LogInfo(_logStore, $"Step-out: targeting native frame[{i}] at 0x{address:X}");
                    return address;
                }
                _log.LogInfo(_logStore,
                    $"Step-out: frame[{i}] at 0x{address:X} has no source, skipping");
                continue;
            }

            string? asmPath = _managedDebugger.FindAssemblyPath(model, method.AssemblyName);
            if (asmPath == null)
                continue;

            (int ILOffset, string File, int Line)[] seqPoints =
                _pdbMapper.GetMethodSequencePoints(asmPath, method.MethodToken);
            if (seqPoints.Length == 0)
            {
                // No portable PDB sequence points (e.g. C++/CLI) — skip to next frame.
                _log.LogInfo(_logStore,
                    $"Step-out: frame[{i}] ({method.AssemblyName}) has no sequence points, skipping");
                continue;
            }

            // Managed frame with source info — advance past the call site line.
            int returnIL = ManagedDebuggerService.ComputeILOffset(model, method, address);
            int returnLine = 0;
            foreach ((int ILOffset, string File, int Line) sp in seqPoints)
            {
                if (sp.ILOffset <= returnIL)
                    returnLine = sp.Line;
            }

            if (returnLine > 0
                && model.JitMethodMappings.TryGetValue((method.MethodToken, method.AssemblyName), out JitMethodMapping? mapping))
            {
                foreach ((int ILOffset, string File, int Line) sp in seqPoints)
                {
                    if (sp.Line > returnLine)
                    {
                        ulong target = mapping.GetNativeAddress(sp.ILOffset);
                        _log.LogInfo(_logStore,
                            $"Step-out: frame[{i}] advancing past call site line {returnLine} → line {sp.Line}");
                        return target;
                    }
                }
            }

            // Has source but no next line (end of method) — use the return address as-is.
            _log.LogInfo(_logStore, $"Step-out: frame[{i}] at end of method, using return addr 0x{address:X}");
            return address;
        }

        // No frame with source info found — return null to avoid setting a BP in
        // sourceless framework code (e.g. async infrastructure, thread pool).
        return null;
    }

    /// <summary>
    /// Attempts a managed step-over by setting a temp BP at the next source line's
    /// native address. Returns <c>true</c> if handled, <c>false</c> to fall back to native.
    /// </summary>
    private bool TryManagedStepOver(NativeDebuggerModel model, JitMethodInfo method,
        ulong ip)
    {
        string? assemblyPath = _managedDebugger.FindAssemblyPath(model, method.AssemblyName);
        if (assemblyPath == null)
            return false;

        int currentIL = ManagedDebuggerService.ComputeILOffset(model, method, ip);
        (int ILOffset, string File, int Line)[] seqPoints = _pdbMapper.GetMethodSequencePoints(assemblyPath, method.MethodToken);

        if (seqPoints.Length > 0)
        {
            // Determine the current source line so we skip sequence points on the same line.
            int currentLine = 0;
            foreach ((int ILOffset, string File, int Line) sp in seqPoints)
            {
                if (sp.ILOffset <= currentIL)
                    currentLine = sp.Line;
            }

            // Collect the first few DISTINCT next lines after the current IL offset.
            // Branching code (if/else, switch) may skip the immediately-next sequence
            // point, so we must cover multiple branch targets. Limit to 2 distinct lines
            // to stay within hardware BP limits (4 DR registers: user BP + 2 temp + step-out).
            const int maxDistinctLines = 2;
            HashSet<int> seenLines = [];
            List<(int ILOffset, string File, int Line)> nextPoints = [];
            foreach ((int ILOffset, string File, int Line) sp in seqPoints)
            {
                if (sp.ILOffset > currentIL && sp.Line != currentLine && seenLines.Add(sp.Line))
                {
                    nextPoints.Add(sp);
                    if (seenLines.Count >= maxDistinctLines)
                        break;
                }
            }

            if (nextPoints.Count > 0 && model.JitMethodMappings.TryGetValue((method.MethodToken, method.AssemblyName), out JitMethodMapping? mapping))
            {
                bool anyBpSet = false;
                foreach ((int ILOffset, string File, int Line) np in nextPoints)
                {
                    ulong targetAddr = mapping.GetNativeAddress(np.ILOffset);
                    if (SetManagedStepBreakpoint(model, targetAddr))
                    {
                        _log.LogInfo(_logStore,
                            $"Managed step-over: temp BP at 0x{targetAddr:X} (IL 0x{np.ILOffset:X} -> {np.File}:{np.Line})");
                        anyBpSet = true;
                    }
                }

                if (anyBpSet)
                {
                    bool isAsync = IsAsyncStateMachine(assemblyPath, method.MethodToken);

                    // Record stack pointer for recursive call detection.
                    // Skip for async MoveNext — the continuation resumes on a different
                    // stack, so depth comparison is meaningless and would suppress the
                    // correct temp BP.
                    NativeStackFrame[] callerFrames = _wrapper.GetStackTrace(model.Wrapper, 5);
                    if (!isAsync && callerFrames.Length > 0)
                        model.ActiveManagedStep!.OriginStackPointer = callerFrames[0].StackOffset;

                    // Also set a step-out BP in the caller to handle early returns
                    // (e.g. "return true;" mid-method won't reach any next sequence point).
                    // Skip for async MoveNext — MoveNext returns normally when an await
                    // yields, which would trigger the step-out BP in framework
                    // infrastructure (ExecutionContext.RunInternal) instead of the real
                    // continuation at the next source line.
                    if (!isAsync && callerFrames.Length >= 2)
                    {
                        ulong? stepOutAddr = FindStepOutTarget(model, callerFrames);
                        if (stepOutAddr != null)
                        {
                            _ = SetManagedStepBreakpoint(model, stepOutAddr.Value);
                            _log.LogInfo(_logStore,
                                $"Managed step-over: step-out fallback at 0x{stepOutAddr.Value:X}");
                        }
                    }

                    model.LastContinuedBpId = model.LastHitBpId;
                    _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
                    return true;
                }
            }
        }

        // No next line (end of method) or no sequence points (C++/CLI) —
        // fall back to step-out via FindStepOutTarget which skips sourceless frames.
        NativeStackFrame[] stepOutFrames = _wrapper.GetStackTrace(model.Wrapper, 5);
        if (stepOutFrames.Length >= 2)
        {
            ulong? target = FindStepOutTarget(model, stepOutFrames);
            if (target != null && SetManagedStepBreakpoint(model, target.Value))
            {
                _log.LogInfo(_logStore, $"Managed step-over (end of method): step-out to 0x{target.Value:X}");
                model.LastContinuedBpId = model.LastHitBpId;
                _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Managed step-into: parses IL at the current offset to find the call target,
    /// then sets a temp BP at the target method's first source line. Also sets a
    /// fallback BP at the next line in the caller (step-over behavior if target
    /// can't be resolved). Returns true if handled.
    /// </summary>
    private bool TryManagedStepInto(NativeDebuggerModel model, JitMethodInfo method, ulong ip)
    {
        string? assemblyPath = _managedDebugger.FindAssemblyPath(model, method.AssemblyName);
        if (assemblyPath == null)
            return false;

        int currentIL = ManagedDebuggerService.ComputeILOffset(model, method, ip);
        (int Token, string Assembly) callerBpKey = (method.MethodToken, method.AssemblyName);
        bool haveBp = false;

        // 1. Parse IL to find the call target at this offset.
        (int TargetToken, string? TargetAssembly, string? TargetMethodName, int CallILOffset)? callTarget =
            _pdbMapper.GetCallTargetAtOffset(assemblyPath, method.MethodToken, currentIL);

        if (callTarget != null)
        {
            // Same-assembly calls (MethodDefinition tokens) have TargetAssembly=null.
            // Fill in the caller's assembly name so JitMethodMap lookups work.
            if (callTarget.Value.TargetAssembly == null)
            {
                callTarget = (callTarget.Value.TargetToken, method.AssemblyName,
                    callTarget.Value.TargetMethodName, callTarget.Value.CallILOffset);
            }

            _log.LogInfo(_logStore,
                $"Managed step-into: IL call target token=0x{callTarget.Value.TargetToken:X} asm={callTarget.Value.TargetAssembly} name={callTarget.Value.TargetMethodName}");

            // 2a. Try JitMethodMap (for JIT'd managed methods, including same-assembly).
            if (!haveBp)
                haveBp = TrySetStepIntoBpFromJitMap(model, callTarget.Value);

            // 2b. Try profiler WATCH (for C++/CLI ahead-of-time compiled methods).
            if (!haveBp)
            {
                _log.LogInfo(_logStore, "Managed step-into: JitMap miss, trying profiler WATCH");
                haveBp = TrySetStepIntoBpViaProfiler(model, callTarget.Value);
            }

            // 2c. Last resort: try native symbol resolution via dbgeng.
            if (!haveBp && callTarget.Value.TargetMethodName != null)
            {
                haveBp = TrySetNativeStepIntoBp(model, callTarget.Value.TargetMethodName);
            }
        }

        // 3. Set fallback BP at the next source line PAST the call instruction.
        // The call instruction is 5 bytes (1 opcode + 4 token). The fallback must
        // be after the call returns, not at the call site — otherwise it fires before
        // the call is made when the current IP is on a preceding brace line.
        int fallbackThreshold = callTarget != null
            ? callTarget.Value.CallILOffset + 5  // Past the call/callvirt instruction.
            : currentIL;                          // No call found — use current offset.
        (int ILOffset, string File, int Line)[] seqPoints = _pdbMapper.GetMethodSequencePoints(assemblyPath, method.MethodToken);
        if (model.JitMethodMappings.TryGetValue(callerBpKey, out JitMethodMapping? callerMapping))
        {
            foreach ((int ILOffset, string File, int Line) sp in seqPoints)
            {
                if (sp.ILOffset > fallbackThreshold)
                {
                    ulong fallbackAddr = callerMapping.GetNativeAddress(sp.ILOffset);
                    _ = SetManagedStepBreakpoint(model, fallbackAddr);
                    _log.LogInfo(_logStore,
                        $"Managed step-into: fallback BP at 0x{fallbackAddr:X} ({sp.File}:{sp.Line})");
                    haveBp = true;
                    break;
                }
            }
        }

        if (!haveBp)
            return false;

        // Go — whichever temp BP fires first wins.
        model.LastContinuedBpId = model.LastHitBpId;
        _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
        return true;
    }

    /// <summary>
    /// For native call targets (C++/CLI calling native C++): resolves the function
    /// via dbgeng symbols and sets a hardware BP at the first source line.
    /// </summary>
    private bool TrySetNativeStepIntoBp(NativeDebuggerModel model, string targetMethodName)
    {
        // Convert .NET name "<Module>.NativeLib.Calculator.Add" to dbgeng symbol.
        // Strip "<Module>." prefix if present.
        string name = targetMethodName;
        if (name.StartsWith("<Module>.", StringComparison.Ordinal))
            name = name["<Module>.".Length..];

        // "NativeLib.Calculator.Add" → module="NativeLib", symbol="NativeLib!NativeLib::Calculator::Add"
        int firstDot = name.IndexOf('.');
        if (firstDot < 0)
            return false;
        string moduleName = name[..firstDot];
        string cppName = name[..].Replace(".", "::");
        string symbol = $"{moduleName}!{cppName}";

        (ulong offset, bool success) = _wrapper.GetOffsetByName(model.Wrapper, symbol);
        if (!success || offset == 0)
        {
            _log.LogInfo(_logStore, $"Managed step-into: GetOffsetByName('{symbol}') failed");
            return false;
        }

        // Get source line — skip the opening brace by advancing to the next line.
        (uint line, string file)? lineInfo = _wrapper.GetLineByOffset(model.Wrapper, offset);
        if (lineInfo != null)
        {
            // The function entry resolves to the opening brace '{'. Advance to the
            // next line to land on the first statement instead.
            (ulong nextLineOffset, bool nextLineSuccess) = _wrapper.GetOffsetByLine(
                model.Wrapper, lineInfo.Value.line + 1, lineInfo.Value.file);
            if (nextLineSuccess)
            {
                offset = nextLineOffset;
                lineInfo = _wrapper.GetLineByOffset(model.Wrapper, offset);
            }
        }

        if (SetManagedStepBreakpoint(model, offset))
        {
            _log.LogInfo(_logStore,
                $"Managed step-into: native target BP at 0x{offset:X} ({lineInfo?.file}:{lineInfo?.line}) via {symbol}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// For C++/CLI call targets not in JitMethodMap: sends a WATCH command to the
    /// profiler and registers a one-shot step-into site on the target method's plan.
    /// The next ENTER hook installs the HW BP; the site is removed on LEAVE.
    /// </summary>
    private bool TrySetStepIntoBpViaProfiler(NativeDebuggerModel model,
        (int TargetToken, string? TargetAssembly, string? TargetMethodName, int CallILOffset) callTarget)
    {
        if (callTarget.TargetMethodName == null || callTarget.TargetAssembly == null)
            return false;

        // Find the target assembly DLL path.
        string? targetAsmPath = _managedDebugger.FindAssemblyPath(model, callTarget.TargetAssembly);
        if (targetAsmPath == null)
        {
            _log.LogInfo(_logStore,
                $"Managed step-into: no assembly path for {callTarget.TargetAssembly}");
            return false;
        }

        // Extract type and method names: "CliWrapper.ManagedCalculator.Add" → type=ManagedCalculator, method=Add.
        string fullName = callTarget.TargetMethodName;
        int lastDot = fullName.LastIndexOf('.');
        if (lastDot < 0)
            return false;
        string methodName = fullName[(lastDot + 1)..];
        string remaining = fullName[..lastDot];
        int secondLastDot = remaining.LastIndexOf('.');
        string typeName = secondLastDot >= 0 ? remaining[(secondLastDot + 1)..] : remaining;

        // Find the MethodDef token in the target assembly's PE.
        int? targetToken = _pdbMapper.FindMethodToken(targetAsmPath, typeName, methodName);
        if (targetToken == null)
        {
            _log.LogInfo(_logStore,
                $"Managed step-into: FindMethodToken({typeName}.{methodName}) not found in {callTarget.TargetAssembly}");
            return false;
        }

        _log.LogInfo(_logStore,
            $"Managed step-into: resolved {typeName}.{methodName} -> token 0x{targetToken.Value:X8} in {callTarget.TargetAssembly}");

        // Get source file and line for the deferred BP record.
        // For C++/CLI: portable PDB won't have data; use dbgeng symbol resolution instead.
        string filePath = targetAsmPath;
        int line = 1;
        int ilOffset = 0;

        (int ILOffset, string File, int Line)[] seqPoints =
            _pdbMapper.GetMethodSequencePoints(targetAsmPath, targetToken.Value);
        if (seqPoints.Length > 0)
        {
            // Skip the method prologue sequence point (IL offset 0, typically the
            // opening brace for out/ref param init) when the next point is on a later line.
            int spIndex = 0;
            if (seqPoints.Length > 1
                && seqPoints[0].ILOffset == 0
                && seqPoints[1].ILOffset > 0
                && seqPoints[1].Line > seqPoints[0].Line)
            {
                spIndex = 1;
            }
            filePath = seqPoints[spIndex].File;
            line = seqPoints[spIndex].Line;
            ilOffset = seqPoints[spIndex].ILOffset;
        }
        else
        {
            // C++/CLI fallback: resolve via dbgeng native PDB symbols.
            string cppName = $"{callTarget.TargetAssembly}!{callTarget.TargetAssembly}::{typeName}::{methodName}";
            (ulong offset, bool success) = _wrapper.GetOffsetByName(model.Wrapper, cppName);
            if (success && offset != 0)
            {
                (uint dbgLine, string dbgFile)? lineInfo = _wrapper.GetLineByOffset(model.Wrapper, offset);
                if (lineInfo != null)
                {
                    filePath = lineInfo.Value.dbgFile;
                    line = (int)lineInfo.Value.dbgLine;
                    _log.LogInfo(_logStore,
                        $"Managed step-into: dbgeng resolved source -> {filePath}:{line}");
                }

                // Store the native address → source mapping so the managed frame resolver
                // can use it for C++/CLI stack frames (same as ManagedBreakpointSources).
                model.ManagedBreakpointSources[offset] = (filePath, line);
            }
        }

        // Register a one-shot site on the target method's plan so the next ENTER hook
        // installs an HW BP. The site is marked IsStepIntoOneShot so it's removed on
        // the final LEAVE (same as any other site) — the user sees the BP hit once.
        (int Token, string Assembly) planKey = (targetToken.Value, callTarget.TargetAssembly);
        if (!model.ManagedBpPlans.TryGetValue(planKey, out ManagedMethodBreakpointPlan? plan))
        {
            plan = new ManagedMethodBreakpointPlan
            {
                MethodToken = targetToken.Value,
                AssemblyName = callTarget.TargetAssembly,
            };
            model.ManagedBpPlans[planKey] = plan;
        }
        plan.Sites.Add(new MethodBreakpointSite
        {
            BpId = -1, // Step-into — no DAP breakpoint ID.
            ILOffset = ilOffset,
            FilePath = filePath,
            Line = line,
            IsStepIntoOneShot = true,
        });

        // Send WATCH command to the profiler so it enables ENTER hooks for this method.
        string watchLine = $"WATCH:{callTarget.TargetAssembly}:{targetToken.Value:X8}";
        StreamWriter? writer = model.ProfilerCmdPipeWriter;
        if (writer != null)
        {
            try
            {
                writer.WriteLine(watchLine);
                _log.LogInfo(_logStore, $"Managed step-into: sent {watchLine}");
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"Managed step-into: WATCH send failed: {ex.Message}");
                return false;
            }
        }
        else
        {
            _log.LogInfo(_logStore, $"Managed step-into: profiler pipe not connected");
            return false;
        }

        model.ProfilerHooksActive = true;

        _log.LogInfo(_logStore,
            $"Managed step-into: WATCH sent, plan site added for {callTarget.TargetAssembly}:0x{targetToken.Value:X8}");
        return true;
    }

    /// <summary>
    /// Finds the call target method in JitMethodMap and sets a temp BP at its first source line.
    /// Uses O(1) token lookup when target token and assembly are known; falls back to
    /// name-based scan across JIT'd methods otherwise.
    /// </summary>
    private bool TrySetStepIntoBpFromJitMap(NativeDebuggerModel model,
        (int TargetToken, string? TargetAssembly, string? TargetMethodName, int CallILOffset) callTarget)
    {
        // Fast path: direct lookup by token + assembly (O(1) via secondary index).
        if (callTarget.TargetToken != 0 && callTarget.TargetAssembly != null)
        {
            JitMethodInfo? directMatch;
            lock (model.JitMethodMap)
            {
                _ = model.JitMethodMapByToken.TryGetValue(
                    (callTarget.TargetToken, callTarget.TargetAssembly), out directMatch);
            }

            if (directMatch != null && TrySetBpOnJitMethod(model, directMatch))
                return true;
        }

        // Slow path: scan by method name suffix when token lookup fails.
        if (callTarget.TargetMethodName == null)
            return false;

        string targetName = callTarget.TargetMethodName;
        int lastDot = targetName.LastIndexOf('.');
        string shortName = lastDot >= 0 ? targetName[(lastDot + 1)..] : targetName;
        string dotShortName = string.Concat(".", shortName);

        lock (model.JitMethodMap)
        {
            foreach (JitMethodInfo jitMethod in model.JitMethodMap.Values)
            {
                if (callTarget.TargetAssembly != null &&
                    !jitMethod.AssemblyName.Equals(callTarget.TargetAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? targetAsmPath = _managedDebugger.FindAssemblyPath(model, jitMethod.AssemblyName);
                if (targetAsmPath == null)
                {
                    _log.LogInfo(_logStore,
                        $"Managed step-into: no assembly path for {jitMethod.AssemblyName}");
                    continue;
                }

                string? jitMethodName = _pdbMapper.GetMethodName(targetAsmPath, jitMethod.MethodToken);
                if (jitMethodName == null || !jitMethodName.EndsWith(dotShortName, StringComparison.Ordinal))
                    continue;

                if (TrySetBpOnJitMethod(model, jitMethod))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sets a temp BP at the first source line of a JIT'd method.
    /// </summary>
    private bool TrySetBpOnJitMethod(NativeDebuggerModel model, JitMethodInfo jitMethod)
    {
        string? targetAsmPath = _managedDebugger.FindAssemblyPath(model, jitMethod.AssemblyName);
        if (targetAsmPath == null)
            return false;

        (int ILOffset, string File, int Line)[] targetSeqPoints =
            _pdbMapper.GetMethodSequencePoints(targetAsmPath, jitMethod.MethodToken);
        if (targetSeqPoints.Length == 0)
            return false;

        if (!model.JitMethodMappings.TryGetValue((jitMethod.MethodToken, jitMethod.AssemblyName), out JitMethodMapping? targetMapping))
            return false;

        // Skip the method prologue sequence point (IL offset 0, typically the opening
        // brace for methods with out/ref params) when the next point is on a later line.
        int spIndex = 0;
        if (targetSeqPoints.Length > 1
            && targetSeqPoints[0].ILOffset == 0
            && targetSeqPoints[1].ILOffset > 0
            && targetSeqPoints[1].Line > targetSeqPoints[0].Line)
        {
            spIndex = 1;
        }

        ulong targetAddr = targetMapping.GetNativeAddress(targetSeqPoints[spIndex].ILOffset);
        if (SetManagedStepBreakpoint(model, targetAddr))
        {
            _log.LogInfo(_logStore,
                $"Managed step-into: target BP at 0x{targetAddr:X} ({targetSeqPoints[spIndex].File}:{targetSeqPoints[spIndex].Line}) in {jitMethod.AssemblyName}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sets a temporary hardware breakpoint for a managed step operation.
    /// Tracks the BP ID in <see cref="NativeDebuggerModel.ActiveManagedStep"/>.
    /// </summary>
    private bool SetManagedStepBreakpoint(NativeDebuggerModel model, ulong address)
    {
        (uint bpId, bool success) = _wrapper.AddHardwareBreakpoint(model.Wrapper, address, 1);
        if (!success)
        {
            _log.LogWarning(_logStore, $"Failed to set managed step temp BP at 0x{address:X}");
            return false;
        }

        _ = model.UserBreakpointIds.Add(bpId);
        model.ActiveManagedStep ??= new ManagedStepState();
        model.ActiveManagedStep.TempBreakpointIds.Add(bpId);
        // Clear the Stepping flag — managed step completion is detected via ActiveManagedStep.
        model.Stepping = false;
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> if the method is an async (or iterator) state machine's
    /// <c>MoveNext</c> method. These are compiler-generated types with names like
    /// <c>Namespace.Type+&lt;Handler&gt;d__5.MoveNext</c>.
    /// </summary>
    private bool IsAsyncStateMachine(string assemblyPath, int methodToken)
    {
        string? name = _pdbMapper.GetMethodName(assemblyPath, methodToken);
        if (name == null)
            return false;

        // Compiler-generated state machines: method is "MoveNext" and the
        // containing type has angle brackets (e.g. "<OnAsyncClick>d__5").
        return name.EndsWith(".MoveNext", StringComparison.Ordinal)
            && name.Contains('<');
    }

    /// <summary>
    /// Cancels any active managed step operation by removing temp breakpoints
    /// and clearing the state.
    /// </summary>
    private void CancelActiveManagedStep(NativeDebuggerModel model)
    {
        if (model.ActiveManagedStep == null)
            return;

        foreach (uint bpId in model.ActiveManagedStep.TempBreakpointIds)
        {
            _ = _wrapper.RemoveBreakpoint(model.Wrapper, bpId);
            _ = model.UserBreakpointIds.Remove(bpId);
        }

        _log.LogInfo(_logStore,
            $"Cancelled managed step: removed {model.ActiveManagedStep.TempBreakpointIds.Count} temp BPs");
        model.ActiveManagedStep = null;
        model.StepOriginLocation = null;
        model.StepOriginStackPointer = 0;
        model.StepOriginKind = default;
        model.StepReStepCount = 0;
        model.StepIntoEnteredCallee = false;
    }
}
