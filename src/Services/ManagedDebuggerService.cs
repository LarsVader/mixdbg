using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless managed debugging service. Handles ICorDebug V4 runtime lifecycle,
/// managed stack frame resolution, and CLR initialization orchestration. Delegates
/// breakpoint operations to <see cref="IManagedBreakpointService"/> and deferred
/// resolution to <see cref="IManagedBreakpointResolver"/>.
/// All methods execute on the engine thread.
/// </summary>
internal sealed class ManagedDebuggerService(
    ILoggingService _log,
    LogStore _logStore,
    IDapServer _server,
    DapServerModel _transport,
    IDbgEngWrapper _dbgEng,
    ICorDebugWrapper _corDebug,
    IPdbSourceMapper _pdbMapper,
    IManagedBreakpointService _bpService,
    IManagedBreakpointResolver breakpointResolver) : IManagedDebugger
{
    private readonly IManagedBreakpointResolver _bpResolver = breakpointResolver;

    public bool IsInitialized(NativeDebuggerModel model) => model.ManagedInitialized;

    public bool InitializeRuntime(NativeDebuggerModel model)
    {
        if (model.ManagedInitialized)
            return true;

        if (string.IsNullOrEmpty(model.CoreClrPath) || model.CoreClrBaseAddress == 0)
        {
            _log.LogWarning(_logStore, "Cannot initialize managed debugging: coreclr path or base address not set");
            return false;
        }

        model.CorWrapper = _corDebug.CreateModel();

        _log.LogInfo(_logStore, $"Initializing ICorDebug V4 (coreclr={model.CoreClrPath}, base=0x{model.CoreClrBaseAddress:X})");
        if (!_corDebug.InitializeProcess(
                model.CorWrapper, model.Wrapper, model.CoreClrPath, model.CoreClrBaseAddress))
        {
            _log.LogError(_logStore, "Failed to initialize ICorDebug V4");
            return false;
        }

        _corDebug.FlushProcessState(model.CorWrapper);
        _corDebug.RefreshModules(model.CorWrapper);

        try { _ = _corDebug.InitializeDac(model.CorWrapper, model.Wrapper, model.CoreClrPath, model.CoreClrBaseAddress); }
        catch (Exception ex) { _log.LogWarning(_logStore, $"DAC init failed: {ex.Message}"); }

        model.ManagedInitialized = true;
        _log.LogInfo(_logStore, "ICorDebug V4 initialized");
        return true;
    }

    public void TryInitializeManaged(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Initializing managed debugging (CLR detected)...");
        if (InitializeRuntime(model))
        {
            // Apply any managed breakpoints that were pending before CLR loaded.
            foreach (SetBreakpointsArguments pending in model.PendingManagedBreakpoints)
            {
                Breakpoint[] bps = _bpService.SetManagedBreakpoints(
                    model, pending.Source.Path!, pending.Breakpoints);
                foreach (Breakpoint bp in bps)
                {
                    _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                    {
                        Reason = "changed",
                        Breakpoint = bp,
                    });
                }
            }
            model.PendingManagedBreakpoints.Clear();
            _log.LogInfo(_logStore, "Managed debugging initialized (ICorDebug V4)");

            // Start polling for deferred managed breakpoint resolution.
            // Skip when profiler is connected — profiler JIT notifications are real-time
            // and don't starve the WPF UI thread like the 2s SetInterrupt polling does.
            if (model.DeferredManagedBreakpoints.Count > 0 && model.ProfilerPipe == null)
                _bpResolver.StartDeferredBreakpointPoller(model);
        }
    }

    public StackFrame[] GetManagedStackFrames(NativeDebuggerModel model)
    {
        if (!model.ManagedInitialized)
            return [];

        uint currentOsId = _dbgEng.GetCurrentThreadSystemId(model.Wrapper);
        RawManagedFrame[] rawFrames = _corDebug.GetRawManagedFrames(model.CorWrapper, currentOsId);
        if (rawFrames.Length == 0)
            return [];

        int frameId = 1;
        return [.. rawFrames.Select(f =>
        {
            string? sourceFile = null;
            int line = 0;
            if (f.ModulePath != null)
            {
                (string File, int Line)? srcLoc = _pdbMapper.GetSourceLocation(f.ModulePath, f.MethodToken,
                    f.ILOffset > 0 ? f.ILOffset : 1);
                if (srcLoc != null)
                {
                    sourceFile = srcLoc.Value.File;
                    line = srcLoc.Value.Line;
                }
            }
            return new StackFrame
            {
                Id = frameId++,
                Name = f.Name,
                Source = sourceFile != null ? new Source
                {
                    Name = Path.GetFileName(sourceFile),
                    Path = sourceFile,
                } : null,
                Line = line,
                Column = 0,
            };
        })];
    }

    public (string Name, Source? Source, int Line)? ResolveFrameFromProfilerData(
        NativeDebuggerModel model, ulong instructionPointer)
    {
        // Binary search the sorted JIT method map for the largest start address ≤ ip.
        JitMethodInfo? method;
        lock (model.JitMethodMap)
        {
            method = FindContainingMethod(model.JitMethodMap, instructionPointer);
        }

        if (method == null)
            return null;

        string? assemblyPath = FindAssemblyPath(model, method.AssemblyName);
        return ResolveMethodFromPdb(model, method, assemblyPath, instructionPointer);
    }

    /// <summary>
    /// Finds the assembly DLL path by matching the assembly name against loaded ICorDebug
    /// modules. Falls back to searching near known module paths on disk.
    /// </summary>
    private string? FindAssemblyPath(NativeDebuggerModel model, string assemblyName)
    {
        if (model.CorWrapper == null)
            return null;

        ManagedModuleInfo? moduleInfo = _corDebug.FindModuleByName(model.CorWrapper, assemblyName);
        if (moduleInfo?.Path != null)
            return moduleInfo.Path;

        // Fallback: search near known module paths.
        foreach (ManagedModuleInfo mod in _corDebug.GetModules(model.CorWrapper))
        {
            if (mod.Path == null) continue;
            string? dir = Path.GetDirectoryName(mod.Path);
            if (dir == null) continue;
            string candidate = Path.Combine(dir, assemblyName + ".dll");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Resolves method name, source file, and line number from PDB metadata and
    /// IL-to-native mapping data. Falls back to managed breakpoint sources for
    /// C++/CLI methods whose Windows PDBs can't be read by PdbSourceMapper.
    /// </summary>
    private (string Name, Source? Source, int Line) ResolveMethodFromPdb(
        NativeDebuggerModel model, JitMethodInfo method, string? assemblyPath, ulong instructionPointer)
    {
        string methodName = $"[{method.AssemblyName}] 0x{method.MethodToken:X8}";
        Source? source = null;
        int line = 0;

        if (assemblyPath == null)
            return (methodName, null, 0);

        try
        {
            // Compute IL offset from native IP using the IL-to-native mapping.
            int ilOffset = ComputeILOffset(model, method, instructionPointer);

            (string File, int Line)? srcLoc = _pdbMapper.GetSourceLocation(assemblyPath, method.MethodToken, ilOffset);
            if (srcLoc != null)
            {
                source = new Source
                {
                    Name = Path.GetFileName(srcLoc.Value.File),
                    Path = srcLoc.Value.File,
                };
                line = srcLoc.Value.Line;
            }

            // Try to get a better method name from PDB metadata.
            string? methodInfo = _pdbMapper.GetMethodName(assemblyPath, method.MethodToken);
            if (methodInfo != null)
                methodName = methodInfo;

            // C++/CLI fallback: PdbSourceMapper can't read Windows PDBs.
            // Look up the source from the managed BP we set at this method's address.
            // Check both method start and exact IP (transient BPs from ENTER hooks
            // are set at an IL-mapped offset, not the method start).
            if (source == null)
                (source, line) = FallbackToBreakpointSource(model, method.StartAddress, instructionPointer);
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"  Profiler frame resolution failed for token 0x{method.MethodToken:X8}: {ex.Message}");
        }

        return (methodName, source, line);
    }

    /// <summary>
    /// Computes the IL offset for a native instruction pointer using the profiler's
    /// IL-to-native mapping. Returns 0 if no mapping is available.
    /// </summary>
    private static int ComputeILOffset(NativeDebuggerModel model, JitMethodInfo method, ulong instructionPointer)
    {
        string bpKey = $"{method.AssemblyName}:{method.MethodToken:X8}";
        if (!model.JitMethodMappings.TryGetValue(bpKey, out JitMethodMapping? methodMapping))
            return 0;

        uint nativeOffset = (uint)(instructionPointer - methodMapping.CodeStart);
        int ilOffset = 0;
        // Find the largest IL offset whose native start ≤ our offset.
        foreach ((int il, int nat) in methodMapping.ILToNativeMap)
        {
            if ((uint)nat <= nativeOffset)
                ilOffset = il;
        }
        return ilOffset;
    }

    /// <summary>
    /// Falls back to looking up source info from managed breakpoint sources (used for
    /// C++/CLI methods whose Windows PDBs can't be read by PdbSourceMapper).
    /// </summary>
    private static (Source? Source, int Line) FallbackToBreakpointSource(
        NativeDebuggerModel model, ulong methodStart, ulong instructionPointer)
    {
        if (model.ManagedBreakpointSources.TryGetValue(methodStart, out (string File, int Line) bpSource) ||
            model.ManagedBreakpointSources.TryGetValue(instructionPointer, out bpSource))
        {
            return (new Source
            {
                Name = Path.GetFileName(bpSource.File),
                Path = bpSource.File,
            }, bpSource.Line);
        }
        return (null, 0);
    }

    public void MergeManagedFrames(NativeDebuggerModel model, StackFrame[] nativeFrames)
    {
        StackFrame[] managedFrames = GetManagedStackFrames(model);
        if (managedFrames.Length == 0)
            return;

        // Best-effort merge: for each native frame without source info, if the
        // next managed frame has a name, overlay it.
        int managedIdx = 0;
        for (int i = 0; i < nativeFrames.Length && managedIdx < managedFrames.Length; i++)
        {
            StackFrame nf = nativeFrames[i];

            // Skip frames that already resolved to native source.
            if (nf.Source != null)
                continue;

            // Check if this looks like a JIT-compiled or CLR infrastructure frame.
            string name = nf.Name ?? "";
            bool looksManaged = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || name.Contains("coreclr!", StringComparison.OrdinalIgnoreCase)
                || name.Contains("clrjit!", StringComparison.OrdinalIgnoreCase)
                || name.Contains("clr!", StringComparison.OrdinalIgnoreCase);

            if (!looksManaged)
                continue;

            // Overlay with the next managed frame.
            StackFrame mf = managedFrames[managedIdx++];
            nativeFrames[i] = new StackFrame
            {
                Id = nf.Id,
                Name = mf.Name,
                Source = mf.Source,
                Line = mf.Line,
                Column = 0,
            };
            _log.LogInfo(_logStore, $"  Merged managed frame into slot {i}: {mf.Name}");
        }
    }

    /// <summary>
    /// Binary searches the sorted JIT method map for the method containing the given IP.
    /// Returns <c>null</c> if no method contains the address.
    /// </summary>
    private static JitMethodInfo? FindContainingMethod(SortedList<ulong, JitMethodInfo> map, ulong ip)
    {
        if (map.Count == 0)
            return null;

        IList<ulong> keys = map.Keys;
        int lo = 0, hi = keys.Count - 1;

        // Find the largest key ≤ ip.
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keys[mid] <= ip)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        // hi is now the index of the largest key ≤ ip (or -1 if none).
        if (hi < 0)
            return null;

        JitMethodInfo entry = map.Values[hi];
        return ip < entry.StartAddress + entry.CodeSize ? entry : null;
    }
}
