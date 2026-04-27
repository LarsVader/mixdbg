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

            // Re-evaluate .cpp deferred (bu) breakpoints now that CLR is initialized.
            // These were set as native breakpoints before CLR loaded because HasClrSupport
            // may have failed. The CLI fallback in SetBreakpointsOnEngine can now resolve them.
            ReEvaluateDeferredCppBreakpoints(model);

            _log.LogInfo(_logStore, "Managed debugging initialized (ICorDebug V4)");

            // Start polling for deferred managed breakpoint resolution.
            // Skip when profiler is connected — profiler JIT notifications are real-time
            // and don't starve the WPF UI thread like the 2s SetInterrupt polling does.
            if (model.DeferredManagedBreakpoints.Count > 0 && model.ProfilerPipe == null)
                _bpResolver.StartDeferredBreakpointPoller(model);
        }
    }

    /// <summary>
    /// Re-evaluates .cpp/.h breakpoints that were set as native deferred (bu) breakpoints
    /// before CLR was initialized. Tries C++/CLI resolution via dbgeng's Windows PDB.
    /// Only replaces the native bu BP if CLI resolution succeeds — native files are left
    /// untouched.
    /// </summary>
    private void ReEvaluateDeferredCppBreakpoints(NativeDebuggerModel model)
    {
        // Collect C++ entries from BreakpointIds. Snapshot keys to avoid modification during iteration.
        List<(string FilePath, int Line, string Key, uint BpId)> candidates = [];
        foreach (string key in model.BreakpointIds.Keys.ToList())
        {
            int lastColon = key.LastIndexOf(':');
            if (lastColon <= 0)
                continue;
            string filePath = key[..lastColon];
            if (!ISourceFileService.IsCppExtension(filePath))
                continue;
            if (!int.TryParse(key[(lastColon + 1)..], out int line))
                continue;
            candidates.Add((filePath, line, key, model.BreakpointIds[key]));
        }

        if (candidates.Count == 0)
            return;

        _log.LogInfo(_logStore, $"Re-evaluating {candidates.Count} C++ BP(s) after CLR init");
        foreach ((string filePath, int line, string key, uint oldBpId) in candidates)
        {
            Breakpoint? cliBp = _bpService.TryResolveCliBreakpoint(model, filePath, line, (int)oldBpId);
            if (cliBp == null)
            {
                _log.LogInfo(_logStore, $"  {filePath}:{line} — not C++/CLI, keeping native bu");
                continue;
            }

            // CLI resolution succeeded — remove the old native bu BP.
            _ = _dbgEng.RemoveBreakpoint(model.Wrapper, oldBpId);
            _ = model.UserBreakpointIds.Remove(oldBpId);
            _ = model.BreakpointIds.Remove(key);
            _log.LogInfo(_logStore, $"  {filePath}:{line} — resolved as C++/CLI, replaced bu #{oldBpId}");

            _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
            {
                Reason = "changed",
                Breakpoint = cliBp,
            });
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
            method = FindContainingMethod(model, instructionPointer);
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
    public string? FindAssemblyPath(NativeDebuggerModel model, string assemblyName)
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
            // Check both method start and exact IP (method-lifetime HW BPs are set
            // at an IL-mapped offset, not the method start).
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
    internal static int ComputeILOffset(NativeDebuggerModel model, JitMethodInfo method, ulong instructionPointer)
    {
        if (!model.JitMethodMappings.TryGetValue((method.MethodToken, method.AssemblyName), out JitMethodMapping? methodMapping))
            return 0;

        uint nativeOffset = (uint)(instructionPointer - methodMapping.CodeStart);
        return methodMapping.GetILOffset(nativeOffset);
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

    public int TryGetManagedLocals(NativeDebuggerModel model, ulong instructionPointer)
    {
        if (!model.ManagedInitialized || model.CorWrapper == null)
            return 0;

        JitMethodInfo? method;
        lock (model.JitMethodMap)
        {
            method = FindContainingMethod(model, instructionPointer);
        }
        if (method == null)
            return 0;

        string? assemblyPath = FindAssemblyPath(model, method.AssemblyName);
        int ilOffset = ComputeILOffset(model, method, instructionPointer);

        uint osThreadId = _dbgEng.GetCurrentThreadSystemId(model.Wrapper);

        _corDebug.FlushProcessState(model.CorWrapper);

        int result = _corDebug.InitializeManagedLocals(
            model.CorWrapper, osThreadId, instructionPointer,
            assemblyPath, method.MethodToken, ilOffset);

        // Fallback: ICorDebug thread enumeration fails on piggybacked V4 process.
        // Use SOS !clrstack via dbgeng to read locals from the DAC instead.
        if (result == 0)
        {
            if (_corDebug.LastDiagnostic != null)
                _log.LogInfo(_logStore, $"ICorDebug locals failed: {_corDebug.LastDiagnostic}, trying SOS");
            result = TryGetLocalsViaSos(model, assemblyPath, method.MethodToken, ilOffset);
        }

        _log.LogInfo(_logStore, $"TryGetManagedLocals: ip=0x{instructionPointer:X} token=0x{method.MethodToken:X8} ilOffset={ilOffset} -> ref={result}");
        return result;
    }

    /// <summary>
    /// Fallback: reads managed locals via SOS <c>!clrstack -a</c> command through dbgeng.
    /// Loads the SOS extension on first use, captures command output, and parses
    /// PARAMETERS/LOCALS sections for the top frame.
    /// </summary>
    private int TryGetLocalsViaSos(NativeDebuggerModel model, string? assemblyPath, int methodToken, int ilOffset)
    {
        try
        {
            // Load SOS extension on first use.
            if (!model.SosLoaded)
            {
                string? sosPath = FindSosPath(model);
                if (sosPath == null)
                {
                    _log.LogWarning(_logStore, "SOS: sos.dll not found");
                    return 0;
                }
                string loadOutput = _dbgEng.ExecuteCommandWithCapture(model.Wrapper, $".load {sosPath}");
                _log.LogInfo(_logStore, $"SOS: .load {sosPath} -> {loadOutput.Trim()}");
                model.SosLoaded = true;
            }

            // Run !clrstack -a to get parameters and locals for all managed frames.
            string output = _dbgEng.ExecuteCommandWithCapture(model.Wrapper, "!clrstack -a");
            _log.LogInfo(_logStore, $"SOS: !clrstack -a output ({output.Length} chars)");

            // Parse PARAMETERS and LOCALS from the top frame.
            VariableInfo[] vars = ParseClrStackLocals(output);

            // Enrich with PDB names and PE types where possible.
            if (assemblyPath != null)
            {
                (string Name, int Index)[] pdbLocals = _pdbMapper.GetLocalVariableNames(assemblyPath, methodToken, ilOffset);
                string[] paramNames = _pdbMapper.GetParameterNames(assemblyPath, methodToken);
                string[] paramTypes = _pdbMapper.GetParameterTypes(assemblyPath, methodToken);
                string[] localTypes = _pdbMapper.GetLocalVariableTypes(assemblyPath, methodToken);
                vars = EnrichWithPdbNames(vars, pdbLocals, paramNames, paramTypes, localTypes);
            }

            if (vars.Length == 0)
            {
                _log.LogInfo(_logStore, "SOS: no locals parsed from !clrstack output");
                return 0;
            }

            return _corDebug.StoreSimpleLocals(model.CorWrapper, vars);
        }
        catch (Exception ex)
        {
            _log.LogWarning(_logStore, $"SOS locals failed: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Finds the SOS extension DLL. Checks dotnet-sos install location first,
    /// then the runtime directory next to coreclr.dll.
    /// </summary>
    private static string? FindSosPath(NativeDebuggerModel model)
    {
        // dotnet-sos global tool install location.
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dotnetSos = Path.Combine(userProfile, ".dotnet", "sos", "sos.dll");
        if (File.Exists(dotnetSos))
            return dotnetSos;

        // Next to coreclr.dll in the runtime directory.
        if (model.CoreClrPath != null)
        {
            string runtimeDir = Path.GetDirectoryName(model.CoreClrPath)!;
            string rtSos = Path.Combine(runtimeDir, "sos.dll");
            if (File.Exists(rtSos))
                return rtSos;
        }

        return null;
    }

    /// <summary>
    /// Parses the output of <c>!clrstack -a</c> to extract PARAMETERS and LOCALS
    /// from the top (first) managed frame.
    /// </summary>
    internal static VariableInfo[] ParseClrStackLocals(string output)
    {
        // !clrstack -a output format:
        //   OS Thread Id: 0x1234 (0)
        //           Child SP               IP Call Site
        //   000000AB 00007FF7B6D3CC66 Namespace.Type.Method(args) [...\file.cs @ 65]
        //       PARAMETERS:
        //           this (0x...) = 0x...
        //           sender (0x...) = 0x...
        //       LOCALS:
        //           0x... = 0x...
        //
        //   000000AB 00007FF7XXXXXXXX Next.Frame(...)
        //   ...

        List<VariableInfo> vars = [];
        string[] lines = output.Split('\n');
        bool inFirstFrame = false;
        bool inParams = false;
        bool inLocals = false;
        bool pastFirstFrame = false;
        int localIdx = 0;

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();

            // Skip header lines.
            if (trimmed.StartsWith("OS Thread Id:", StringComparison.Ordinal) ||
                trimmed.StartsWith("Child SP", StringComparison.Ordinal) ||
                trimmed.Length == 0)
            {
                continue;
            }

            // Detect frame lines (start with hex address).
            if (!trimmed.StartsWith("PARAMETERS", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("LOCALS", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("this ", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > 16 && IsHexPrefix(trimmed))
            {
                if (inFirstFrame)
                {
                    pastFirstFrame = true;
                    break; // Stop at the second frame.
                }
                inFirstFrame = true;
                inParams = false;
                inLocals = false;
                continue;
            }

            if (pastFirstFrame) break;
            if (!inFirstFrame) continue;

            if (trimmed.StartsWith("PARAMETERS:", StringComparison.OrdinalIgnoreCase))
            {
                inParams = true;
                inLocals = false;
                continue;
            }
            if (trimmed.StartsWith("LOCALS:", StringComparison.OrdinalIgnoreCase))
            {
                inLocals = true;
                inParams = false;
                localIdx = 0;
                continue;
            }

            // Parse variable line: "name (0x...) = 0x..." or "0x... = 0x..."
            if (inParams || inLocals)
            {
                (string? name, string? value) = ParseSosVariableLine(trimmed);
                if (value != null)
                {
                    string varName = name ?? (inParams ? $"arg{vars.Count}" : $"local{localIdx}");
                    string section = inParams ? "param" : "local";
                    vars.Add(new VariableInfo(varName, FormatSosValue(value), section, 0));
                    if (inLocals) localIdx++;
                }
            }
        }

        return [.. vars];
    }

    /// <summary>
    /// Parses a single SOS variable line like <c>this (0x...) = 0x...</c> or <c>0x... = 0x...</c>.
    /// Returns (name, value) where name may be null for unnamed locals.
    /// </summary>
    private static (string? Name, string? Value) ParseSosVariableLine(string line)
    {
        // Named: "varname (0x...) = 0x..." or "varname = 0x..."
        // Unnamed: "0x... = 0x..."
        int eqIdx = line.IndexOf('=');
        if (eqIdx < 0)
            return (null, null);

        string lhs = line[..eqIdx].Trim();
        string value = line[(eqIdx + 1)..].Trim();

        // Strip address hint: "sender (0x000000AB12CD)" -> "sender"
        string? name = null;
        int parenIdx = lhs.IndexOf('(');
        if (parenIdx > 0)
            name = lhs[..parenIdx].Trim();
        else if (!lhs.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            name = lhs;

        return (name, value);
    }

    /// <summary>
    /// Formats a raw SOS hex value for display. Small values (upper 32 bits zero)
    /// are shown as decimal (likely primitives). Large values are kept as hex
    /// (likely heap addresses / object references).
    /// </summary>
    internal static string FormatSosValue(string hexValue)
    {
        if (!hexValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return hexValue;

        if (!ulong.TryParse(hexValue.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong val))
            return hexValue;

        // Zero is always "0".
        if (val == 0)
            return "0";

        // If upper 32 bits are zero, this is likely a primitive value — show decimal.
        return val <= uint.MaxValue
            ? val.ToString()
            : hexValue;
    }

    /// <summary>Checks if a string starts with hex digits (frame line detection).</summary>
    private static bool IsHexPrefix(string s)
    {
        if (s.Length < 8) return false;
        for (int i = 0; i < 8; i++)
        {
            char c = s[i];
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Enriches SOS-parsed variables with PDB names. Parameters get PDB names by order;
    /// locals get PDB names by slot index.
    /// </summary>
    private static VariableInfo[] EnrichWithPdbNames(VariableInfo[] vars,
        (string Name, int Index)[] pdbLocals, string[] paramNames,
        string[] paramTypes, string[] localTypes)
    {
        int paramIdx = 0;
        int localIdx = 0;
        Dictionary<int, string> localNameMap = [];
        foreach ((string name, int idx) in pdbLocals)
            localNameMap[idx] = name;

        VariableInfo[] result = new VariableInfo[vars.Length];
        for (int i = 0; i < vars.Length; i++)
        {
            VariableInfo v = vars[i];
            if (v.Type == "param")
            {
                string name = v.Name;
                string? type = null;
                if (name != "this")
                {
                    if (paramIdx < paramNames.Length)
                        name = paramNames[paramIdx];
                    if (paramIdx < paramTypes.Length)
                        type = paramTypes[paramIdx];
                    paramIdx++;
                }
                else
                {
                    type = "object";
                }
                result[i] = new VariableInfo(name, v.Value, type, 0);
            }
            else
            {
                string name = localNameMap.TryGetValue(localIdx, out string? pdbName)
                    ? pdbName : v.Name;
                string? type = localIdx < localTypes.Length ? localTypes[localIdx] : null;
                result[i] = new VariableInfo(name, v.Value, type, 0);
                localIdx++;
            }
        }
        return result;
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
    /// Binary searches the JIT method map for the method containing the given IP.
    /// Builds a sorted snapshot lazily (cached until new entries are added).
    /// Caller must hold <c>lock (model.JitMethodMap)</c>.
    /// </summary>
    internal static JitMethodInfo? FindContainingMethod(NativeDebuggerModel model, ulong ip)
    {
        if (model.JitMethodMap.Count == 0)
            return null;

        // Build sorted snapshot if invalidated.
        (ulong Key, JitMethodInfo Value)[] snapshot = model.JitMethodMapSnapshot
            ?? RebuildSnapshot(model);

        int lo = 0, hi = snapshot.Length - 1;

        // Find the largest key ≤ ip.
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (snapshot[mid].Key <= ip)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        // hi is now the index of the largest key ≤ ip (or -1 if none).
        if (hi < 0)
            return null;

        JitMethodInfo entry = snapshot[hi].Value;
        return ip < entry.StartAddress + entry.CodeSize ? entry : null;
    }

    private static (ulong Key, JitMethodInfo Value)[] RebuildSnapshot(NativeDebuggerModel model)
    {
        (ulong Key, JitMethodInfo Value)[] snapshot =
            [.. model.JitMethodMap.Select(kv => (kv.Key, kv.Value)).OrderBy(e => e.Key)];
        model.JitMethodMapSnapshot = snapshot;
        return snapshot;
    }
}
