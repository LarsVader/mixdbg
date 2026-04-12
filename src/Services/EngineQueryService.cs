using MixDbg.Models;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Models.DapMessages.Threads;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless engine query and execution control service. Provides stack trace,
/// scope, variable, and thread inspection as well as execution control.
/// All state lives in <see cref="NativeDebuggerModel"/>.
/// </summary>
internal sealed class EngineQueryService(
    ILoggingService _log,
    LogStore _logStore,
    IManagedDebugger _managedDebugger,
    IManagedBreakpointService _managedBp,
    ICorDebugWrapper _corDebug,
    IPdbSourceMapper _pdbMapper,
    IDbgEngWrapper _wrapper) : IEngineQueryService
{
    public StackFrame[] GetStackTraceOnEngine(NativeDebuggerModel model, int maxFrames)
    {
        // Cache the stack trace result per stop. Repeated stackTrace requests from
        // nvim-dap (one per thread) all return the event thread's stack anyway,
        // but the redundant GetStackTrace + symbol lookups degrade the DAC,
        // breaking CreateRuntime for deferred breakpoint resolution.
        if (model.CachedStackTraceResult != null)
            return model.CachedStackTraceResult;

        NativeStackFrame[] nativeFrames = _wrapper.GetStackTrace(model.Wrapper, maxFrames);
        if (nativeFrames.Length == 0)
            return [];

        StackFrame[] result = new StackFrame[nativeFrames.Length];
        for (int i = 0; i < nativeFrames.Length; i++)
            result[i] = ResolveStackFrame(model, nativeFrames[i].InstructionOffset, i);

        if (model.ManagedInitialized)
            _managedDebugger.MergeManagedFrames(model, result);

        model.CachedStackTraceResult = result;
        return result;
    }

    /// <summary>
    /// Resolves a single native instruction pointer into a DAP <see cref="StackFrame"/>
    /// with function name, source location, and line number.
    /// </summary>
    private StackFrame ResolveStackFrame(NativeDebuggerModel model, ulong ip, int index)
    {
        string name = ResolveFunctionName(model.Wrapper, ip);
        (string? resolvedName, Source? source, int line) = ResolveSourceLocation(model, ip);

        // Profiler-resolved name overrides dbgeng name (more accurate for managed code).
        if (resolvedName != null)
            name = resolvedName;

        _log.LogInfo(_logStore, $"  Frame {index}: ip=0x{ip:X} name={name} line={line}");

        return new StackFrame
        {
            Id = index + 1, // 1-based
            Name = name,
            Source = source,
            Line = line,
            Column = 0,
        };
    }

    /// <summary>
    /// Resolves a function name from the instruction pointer via dbgeng symbols.
    /// Returns a hex address string if no symbol is available.
    /// </summary>
    private string ResolveFunctionName(DbgEngWrapperModel wrapper, ulong ip)
    {
        (string Name, ulong Displacement)? nameInfo = _wrapper.GetNameByOffset(wrapper, ip);
        return nameInfo == null
            ? $"0x{ip:X}"
            : nameInfo.Value.Displacement > 0
                ? $"{nameInfo.Value.Name}+0x{nameInfo.Value.Displacement:x}"
                : nameInfo.Value.Name;
    }

    /// <summary>
    /// Resolves source file, line number, and optionally a better function name for an instruction pointer.
    /// Tries dbgeng's PDB symbols first, then falls back to the profiler's JIT method map
    /// for managed code that dbgeng can't resolve. Returns <c>Name</c> only when the profiler
    /// provides a more accurate name than dbgeng (managed code).
    /// </summary>
    private (string? Name, Source? Source, int Line) ResolveSourceLocation(NativeDebuggerModel model, ulong ip)
    {
        // Try dbgeng symbol resolution (native PDBs and C++/CLI Windows PDBs).
        (uint Line, string File)? lineInfo = _wrapper.GetLineByOffset(model.Wrapper, ip);
        if (lineInfo != null)
        {
            return (null, new Source
            {
                Name = Path.GetFileName(lineInfo.Value.File),
                Path = lineInfo.Value.File,
            }, (int)lineInfo.Value.Line);
        }

        // Fallback: try the profiler's JIT method map for managed code.
        if (model.JitMethodMap.Count <= 0)
        {
            return (null, null, 0);
        }

        try
        {
            (string Name, Source? Source, int Line)? profilerFrame =
                _managedDebugger.ResolveFrameFromProfilerData(model, ip);

            if (profilerFrame != null)
                return (profilerFrame.Value.Name, profilerFrame.Value.Source, profilerFrame.Value.Line);
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"  Profiler resolve failed for 0x{ip:X}: {ex.Message}");
        }

        return (null, null, 0);
    }

    public Scope[] GetScopesOnEngine(NativeDebuggerModel model, int frameId)
    {
        int localsRef = _wrapper.SetScopeAndGetLocals(model.Wrapper, frameId);
        _log.LogInfo(_logStore, $"SetScopeAndGetLocals(frameId={frameId}) -> ref={localsRef}");

        // Fallback to managed locals if native returned 0 (managed frame).
        if (localsRef == 0 && model.ManagedInitialized)
        {
            int index = frameId - 1;
            if (index >= 0 && index < model.Wrapper.CachedStackFrames.Length)
            {
                ulong ip = model.Wrapper.CachedStackFrames[index].InstructionOffset;
                localsRef = _managedDebugger.TryGetManagedLocals(model, ip);
                _log.LogInfo(_logStore, $"TryGetManagedLocals(ip=0x{ip:X}) -> ref={localsRef}");
            }
        }

        return localsRef == 0
            ? []
            : [
            new Scope
            {
                Name = "Locals",
                VariablesReference = localsRef,
                Expensive = false,
            }
        ];
    }

    public Variable[] GetVariablesOnEngine(NativeDebuggerModel model, int variablesReference)
    {
        _log.LogInfo(_logStore, $"GetVariables: ref={variablesReference}");

        // Route by reference range: managed refs start at BaseOffset.
        VariableInfo[] vars = ManagedVariableStore.IsManaged(variablesReference) && model.CorWrapper != null
            ? _corDebug.GetManagedVariables(model.CorWrapper, variablesReference)
            : _wrapper.GetVariables(model.Wrapper, variablesReference);

        Variable[] result = new Variable[vars.Length];
        for (int i = 0; i < vars.Length; i++)
        {
            VariableInfo v = vars[i];
            _log.LogInfo(_logStore, $"  Var: name=\"{v.Name}\" type=\"{v.Type}\" value=\"{v.Value}\" childRef={v.VariablesReference}");
            result[i] = new Variable
            {
                Name = v.Name,
                Value = v.Value,
                Type = v.Type,
                VariablesReference = v.VariablesReference,
            };
        }
        return result;
    }

    public DapThread[] GetThreadsOnEngine(NativeDebuggerModel model)
    {
        (uint EngineId, uint SystemId)[] threads = _wrapper.GetThreads(model.Wrapper);
        if (threads.Length == 0)
            return [new DapThread { Id = 1, Name = "Main Thread" }];

        DapThread[] result = new DapThread[threads.Length];
        for (int i = 0; i < threads.Length; i++)
        {
            result[i] = new DapThread
            {
                Id = (int)threads[i].EngineId,
                Name = $"Thread {threads[i].SystemId} (dbg:{threads[i].EngineId})",
            };
        }
        return result;
    }

    public int GetStoppedThreadIdOnEngine(NativeDebuggerModel model) => (int)_wrapper.GetEventThreadId(model.Wrapper);

    public void ExecuteContinueOnEngine(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Continue executing: SetExecutionStatus(GO)");
        CancelActiveManagedStep(model);
        model.LastContinuedBpId = model.LastHitBpId;
        model.ContinueTimestampTicks = Environment.TickCount64;
        _managedBp.RemoveTransientManagedBreakpoints(model);
        _ = (model.ProfilerRehookEvent?.Set());
        model.ConfigDone = true;
        model.CachedStackTraceResult = null;
        _wrapper.ClearVariables(model.Wrapper);
        if (model.CorWrapper != null)
            _corDebug.ClearManagedVariables(model.CorWrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
    }

    public void ExecuteStepOnEngine(NativeDebuggerModel model, EngineExecutionStatus stepKind)
    {
        CancelActiveManagedStep(model);
        _managedBp.RemoveTransientManagedBreakpoints(model);
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
                    method = ManagedDebuggerService.FindContainingMethod(model.JitMethodMap, ip);
                }

                if (method != null)
                {
                    if (stepKind == EngineExecutionStatus.StepOver)
                    {
                        _ = (model.ProfilerRehookEvent?.Set());
                        if (TryManagedStepOver(model, method, ip, frames))
                            return;
                    }
                    else if (stepKind == EngineExecutionStatus.StepInto)
                    {
                        // Do NOT rehook — step-into needs raw call path without ENTER hooks.
                        if (TryManagedStepInto(model, method, ip))
                            return;
                    }
                }
            }
        }

        // Native step — rehook and use dbgeng directly.
        _ = (model.ProfilerRehookEvent?.Set());
        _wrapper.SetExecutionStatus(model.Wrapper, stepKind);
    }

    public void ExecuteStepOutOnEngine(NativeDebuggerModel model)
    {
        CancelActiveManagedStep(model);
        _managedBp.RemoveTransientManagedBreakpoints(model);
        _ = (model.ProfilerRehookEvent?.Set());
        _wrapper.ClearVariables(model.Wrapper);
        if (model.CorWrapper != null)
            _corDebug.ClearManagedVariables(model.CorWrapper);

        // Use temp BP at caller's return address for reliable cross-boundary step-out.
        // The dbgeng "gu" command doesn't stop reliably when returning from native to managed.
        NativeStackFrame[] frames = _wrapper.GetStackTrace(model.Wrapper, 2);
        if (frames.Length >= 2)
        {
            ulong returnAddress = frames[1].InstructionOffset;
            if (SetManagedStepBreakpoint(model, returnAddress))
            {
                _log.LogInfo(_logStore, $"Step-out: temp BP at return addr 0x{returnAddress:X}");
                _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
                return;
            }
        }

        // Fallback: native step-out via dbgeng "gu".
        _ = _wrapper.ExecuteCommand(model.Wrapper, "gu");
    }

    /// <summary>
    /// Attempts a managed step-over by setting a temp BP at the next source line's
    /// native address. Returns <c>true</c> if handled, <c>false</c> to fall back to native.
    /// </summary>
    private bool TryManagedStepOver(NativeDebuggerModel model, JitMethodInfo method,
        ulong ip, NativeStackFrame[] frames)
    {
        string? assemblyPath = _managedDebugger.FindAssemblyPath(model, method.AssemblyName);
        if (assemblyPath == null)
            return false;

        int currentIL = ManagedDebuggerService.ComputeILOffset(model, method, ip);
        (int ILOffset, string File, int Line)[] seqPoints = _pdbMapper.GetMethodSequencePoints(assemblyPath, method.MethodToken);
        if (seqPoints.Length == 0)
            return false;

        // Find the first sequence point with IL offset > current.
        (int ILOffset, string File, int Line)? nextPoint = null;
        foreach ((int ILOffset, string File, int Line) sp in seqPoints)
        {
            if (sp.ILOffset > currentIL)
            {
                nextPoint = sp;
                break;
            }
        }

        string bpKey = $"{method.AssemblyName}:{method.MethodToken:X8}";
        if (nextPoint != null && model.JitMethodMappings.TryGetValue(bpKey, out JitMethodMapping? mapping))
        {
            // Next line exists — set BP at its native address.
            ulong targetAddr = mapping.GetNativeAddress(nextPoint.Value.ILOffset);
            if (SetManagedStepBreakpoint(model, targetAddr))
            {
                _log.LogInfo(_logStore,
                    $"Managed step-over: temp BP at 0x{targetAddr:X} (IL 0x{nextPoint.Value.ILOffset:X} -> {nextPoint.Value.File}:{nextPoint.Value.Line})");
                _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
                return true;
            }
        }

        // No next line (end of method) — fall back to step-out behavior.
        if (frames.Length >= 2)
        {
            ulong returnAddress = frames[1].InstructionOffset;
            if (SetManagedStepBreakpoint(model, returnAddress))
            {
                _log.LogInfo(_logStore, $"Managed step-over (end of method): temp BP at return addr 0x{returnAddress:X}");
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
        string callerBpKey = $"{method.AssemblyName}:{method.MethodToken:X8}";
        bool haveBp = false;

        // 1. Parse IL to find the call target at this offset.
        (int TargetToken, string? TargetAssembly, string? TargetMethodName)? callTarget =
            _pdbMapper.GetCallTargetAtOffset(assemblyPath, method.MethodToken, currentIL);

        if (callTarget != null)
        {
            _log.LogInfo(_logStore,
                $"Managed step-into: IL call target token=0x{callTarget.Value.TargetToken:X} asm={callTarget.Value.TargetAssembly} name={callTarget.Value.TargetMethodName}");

            // If call target has no assembly (native call from C++/CLI), resolve via dbgeng
            // symbols and set a hardware BP at the native function entry.
            if (string.IsNullOrEmpty(callTarget.Value.TargetAssembly)
                && callTarget.Value.TargetMethodName != null)
            {
                haveBp = TrySetNativeStepIntoBp(model, callTarget.Value.TargetMethodName);
            }

            // 2a. Try JitMethodMap (for JIT'd managed methods).
            if (!haveBp)
                haveBp = TrySetStepIntoBpFromJitMap(model, callTarget.Value);

            // 2b. Try profiler WATCH (for C++/CLI ahead-of-time compiled methods).
            if (!haveBp)
            {
                _log.LogInfo(_logStore, "Managed step-into: JitMap miss, trying profiler WATCH");
                haveBp = TrySetStepIntoBpViaProfiler(model, callTarget.Value);
            }
        }

        // 3. Set fallback BP at next source line (step-over) in case call target not resolved.
        (int ILOffset, string File, int Line)[] seqPoints = _pdbMapper.GetMethodSequencePoints(assemblyPath, method.MethodToken);
        if (model.JitMethodMappings.TryGetValue(callerBpKey, out JitMethodMapping? callerMapping))
        {
            foreach ((int ILOffset, string File, int Line) sp in seqPoints)
            {
                if (sp.ILOffset > currentIL)
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
    /// profiler and adds a temporary deferred BP so <c>HandleEnterBreakpoint</c>
    /// sets a transient hardware BP when the ENTER hook fires.
    /// </summary>
    private bool TrySetStepIntoBpViaProfiler(NativeDebuggerModel model,
        (int TargetToken, string? TargetAssembly, string? TargetMethodName) callTarget)
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
            filePath = seqPoints[0].File;
            line = seqPoints[0].Line;
            ilOffset = seqPoints[0].ILOffset;
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

        // Add temporary deferred BP so HandleEnterBreakpoint matches the ENTER notification.
        model.DeferredManagedBreakpoints.Add(new DeferredManagedBreakpoint(
            filePath, line, targetToken.Value, ilOffset,
            BpId: -1, // Step-into — no DAP breakpoint ID.
            AssemblyName: callTarget.TargetAssembly,
            IsCliMethod: true));

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

        // Signal rehook so the profiler re-enables ENTER hooks.
        _ = (model.ProfilerRehookEvent?.Set());
        model.ProfilerHooksActive = true;

        _log.LogInfo(_logStore,
            $"Managed step-into: WATCH sent, deferred BP added for {callTarget.TargetAssembly}:0x{targetToken.Value:X8}");
        return true;
    }

    /// <summary>
    /// Finds the call target method in JitMethodMap and sets a temp BP at its first source line.
    /// Searches by matching the method name suffix across all JIT'd methods in the target assembly.
    /// </summary>
    private bool TrySetStepIntoBpFromJitMap(NativeDebuggerModel model,
        (int TargetToken, string? TargetAssembly, string? TargetMethodName) callTarget)
    {
        if (callTarget.TargetMethodName == null)
            return false;

        // Extract the short method name (e.g., "Add" from "CliWrapper.ManagedCalculator.Add").
        string targetName = callTarget.TargetMethodName;
        int lastDot = targetName.LastIndexOf('.');
        string shortName = lastDot >= 0 ? targetName[(lastDot + 1)..] : targetName;

        // Search JitMethodMap for a matching method.
        lock (model.JitMethodMap)
        {
            foreach (JitMethodInfo jitMethod in model.JitMethodMap.Values)
            {
                // Match by assembly name (if known) and method name.
                if (callTarget.TargetAssembly != null &&
                    !jitMethod.AssemblyName.Equals(callTarget.TargetAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Get the method's assembly path to check its name.
                string? targetAsmPath = _managedDebugger.FindAssemblyPath(model, jitMethod.AssemblyName);
                if (targetAsmPath == null)
                {
                    _log.LogInfo(_logStore,
                        $"Managed step-into: no assembly path for {jitMethod.AssemblyName}");
                    continue;
                }

                string? jitMethodName = _pdbMapper.GetMethodName(targetAsmPath, jitMethod.MethodToken);
                if (jitMethodName == null || !jitMethodName.EndsWith($".{shortName}", StringComparison.Ordinal))
                    continue;

                // Found the target method. Get its first sequence point.
                (int ILOffset, string File, int Line)[] targetSeqPoints =
                    _pdbMapper.GetMethodSequencePoints(targetAsmPath, jitMethod.MethodToken);
                if (targetSeqPoints.Length == 0)
                    continue;

                string targetBpKey = $"{jitMethod.AssemblyName}:{jitMethod.MethodToken:X8}";
                if (!model.JitMethodMappings.TryGetValue(targetBpKey, out JitMethodMapping? targetMapping))
                    continue;

                ulong targetAddr = targetMapping.GetNativeAddress(targetSeqPoints[0].ILOffset);
                if (SetManagedStepBreakpoint(model, targetAddr))
                {
                    _log.LogInfo(_logStore,
                        $"Managed step-into: target BP at 0x{targetAddr:X} ({targetSeqPoints[0].File}:{targetSeqPoints[0].Line}) in {jitMethod.AssemblyName}");
                    return true;
                }
            }
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
    /// Cancels any active managed step operation by removing temp breakpoints
    /// and clearing the state.
    /// </summary>
    internal void CancelActiveManagedStep(NativeDebuggerModel model)
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
    }
}
