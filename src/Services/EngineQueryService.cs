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
        VariableInfo[] vars = _wrapper.GetVariables(model.Wrapper, variablesReference);

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
        _managedBp.RemoveTransientManagedBreakpoints(model);

        // Only REHOOK if no transient managed BPs remain. When multiple BPs target the
        // same method (e.g. lines before and after a native call), the unfired ones must
        // stay active — REHOOK will happen after the last one fires.
        if (model.ManagedBreakpointIds.Count == 0)
            _ = (model.ProfilerRehookEvent?.Set());

        model.ConfigDone = true;
        model.CachedStackTraceResult = null;
        _wrapper.ClearVariables(model.Wrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
    }

    public void ExecuteStepOnEngine(NativeDebuggerModel model, EngineExecutionStatus stepKind)
    {
        _managedBp.RemoveTransientManagedBreakpoints(model);
        if (model.ManagedBreakpointIds.Count == 0)
            _ = (model.ProfilerRehookEvent?.Set());
        _wrapper.ClearVariables(model.Wrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, stepKind);
    }

    public void ExecuteStepOutOnEngine(NativeDebuggerModel model)
    {
        _wrapper.ClearVariables(model.Wrapper);
        _ = _wrapper.ExecuteCommand(model.Wrapper, "gu");
    }
}
