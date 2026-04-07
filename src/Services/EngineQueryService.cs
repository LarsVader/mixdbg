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

        DbgEngWrapperModel w = model.Wrapper;
        NativeStackFrame[] nativeFrames = _wrapper.GetStackTrace(w, maxFrames);
        if (nativeFrames.Length == 0)
            return [];

        StackFrame[] result = new StackFrame[nativeFrames.Length];
        for (int i = 0; i < nativeFrames.Length; i++)
        {
            ulong ip = nativeFrames[i].InstructionOffset;
            string name = $"0x{ip:X}";
            Source? source = null;
            int line = 0;

            // Try to resolve function name
            (string Name, ulong Displacement)? nameInfo = _wrapper.GetNameByOffset(w, ip);
            if (nameInfo != null)
            {
                name = nameInfo.Value.Displacement > 0
                    ? $"{nameInfo.Value.Name}+0x{nameInfo.Value.Displacement:x}"
                    : nameInfo.Value.Name;
            }

            // Try to resolve source location
            (uint Line, string File)? lineInfo = _wrapper.GetLineByOffset(w, ip);
            if (lineInfo != null)
            {
                line = (int)lineInfo.Value.Line;
                source = new Source
                {
                    Name = Path.GetFileName(lineInfo.Value.File),
                    Path = lineInfo.Value.File,
                };
            }

            // Fallback: if dbgeng can't resolve, try the profiler's JIT method map.
            if (source == null && model.JitMethodMap.Count > 0)
            {
                try
                {
                    (string Name, Source? Source, int Line)? profilerFrame = _managedDebugger.ResolveFrameFromProfilerData(model, ip);
                    if (profilerFrame != null)
                    {
                        name = profilerFrame.Value.Name;
                        source = profilerFrame.Value.Source;
                        line = profilerFrame.Value.Line;
                    }
                }
                catch { }
            }

            _log.LogInfo(_logStore, $"  Frame {i}: ip=0x{ip:X} name={name} line={line}");

            result[i] = new StackFrame
            {
                Id = i + 1, // 1-based
                Name = name,
                Source = source,
                Line = line,
                Column = 0,
            };
        }

        // Merge managed frame info from ClrMD.
        if (model.ManagedInitialized)
            _managedDebugger.MergeManagedFrames(model, result);

        model.CachedStackTraceResult = result;
        return result;
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
        _managedDebugger.RemoveTransientManagedBreakpoints(model);
        _ = (model.ProfilerRehookEvent?.Set());
        model.ConfigDone = true;
        model.CachedStackTraceResult = null;
        _wrapper.ClearVariables(model.Wrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, EngineExecutionStatus.Go);
    }

    public void ExecuteStepOnEngine(NativeDebuggerModel model, EngineExecutionStatus stepKind)
    {
        _managedDebugger.RemoveTransientManagedBreakpoints(model);
        _wrapper.ClearVariables(model.Wrapper);
        _wrapper.SetExecutionStatus(model.Wrapper, stepKind);
    }

    public void ExecuteStepOutOnEngine(NativeDebuggerModel model)
    {
        _wrapper.ClearVariables(model.Wrapper);
        _ = _wrapper.ExecuteCommand(model.Wrapper, "gu");
    }
}
