using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Events;
using MixDbg.Models.DapMessages.Inspection;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless managed debugging service. Orchestrates managed breakpoint
/// lifecycle, profiler interaction, and frame merging. Delegates all ICorDebug
/// V4 COM operations to <see cref="ICorDebugWrapper"/>.
/// All methods execute on the engine thread.
/// </summary>
internal sealed class ManagedDebuggerService(
    ILoggingService log,
    LogStore logStore,
    ISourceFileService sourceFiles,
    IDapServer server,
    DapServerModel transport,
    IDbgEngWrapper dbgEngWrapper,
    ICorDebugWrapper corDebugWrapper,
    IPdbSourceMapper pdbSourceMapper) : IManagedDebugger
{
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly ISourceFileService _sourceFiles = sourceFiles;
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly IDbgEngWrapper _dbgEng = dbgEngWrapper;
    private readonly ICorDebugWrapper _corDebug = corDebugWrapper;
    private readonly IPdbSourceMapper _pdbMapper = pdbSourceMapper;

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

    public Breakpoint[] SetManagedBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested)
    {
        _log.LogInfo(_logStore, $"SetManagedBreakpoints: file={filePath} count={requested.Length}");

        // Clear existing managed breakpoints for this file.
        ClearManagedBreakpointsForFile(model, filePath);

        Breakpoint[] results = new Breakpoint[requested.Length];
        for (int i = 0; i < requested.Length; i++)
        {
            results[i] = SetOneManagedBreakpoint(model, filePath, requested[i]);
        }
        return results;
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

    // ── Private ─────────────────────────────────────────

    private Breakpoint SetOneManagedBreakpoint(
        NativeDebuggerModel model, string filePath, SourceBreakpoint req)
    {
        int bpId = ++model.NextBpId;

        // Try to bind the breakpoint to a loaded module via PDB.
        if (TryBindBreakpoint(model, filePath, req.Line, bpId))
        {
            return new Breakpoint
            {
                Id = bpId,
                Verified = true,
                Line = req.Line,
                Source = new Source { Name = Path.GetFileName(filePath), Path = filePath },
            };
        }

        // Module not loaded yet — store as pending, will bind on module load.
        model.PendingILBreakpoints.Add(new PendingManagedBreakpoint(filePath, req.Line, bpId));
        _log.LogInfo(_logStore, $"  Breakpoint #{bpId} pending — module not loaded yet");

        if (!model.ManagedFileBreakpointIds.ContainsKey(filePath))
            model.ManagedFileBreakpointIds[filePath] = [];
        model.ManagedFileBreakpointIds[filePath].Add(bpId);

        return new Breakpoint
        {
            Id = bpId,
            Verified = true, // Optimistic — will bind on module load.
            Line = req.Line,
            Source = new Source { Name = Path.GetFileName(filePath), Path = filePath },
            Message = "Pending — module not yet loaded",
        };
    }

    /// <summary>
    /// Tries to bind a breakpoint to a loaded module. First attempts direct resolution
    /// via <c>GetOffsetByLine</c> (works if method is JIT'd). If not JIT'd, stores as
    /// a <see cref="DeferredManagedBreakpoint"/> for periodic polling via
    /// <c>GetOffsetByLine</c> + hardware breakpoint.
    /// </summary>
    private bool TryBindBreakpoint(NativeDebuggerModel model, string filePath, int line, int bpId)
    {
        // Check if PDB resolution finds the method in any loaded module.
        bool foundInPdb = false;
        bool isCliMethod = false; // C++/CLI: skip direct GetOffsetByLine, use deferred + JIT notification
        int methodToken = 0;
        int ilOffset = 0;
        string? assemblyName = null;
        foreach (ManagedModuleInfo loaded in _corDebug.GetModules(model.CorWrapper))
        {
            if (loaded.PdbPath == null || loaded.Path == null)
                continue;
            (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result = _pdbMapper.FindMethodAtLine(loaded.Path, filePath, line);
            if (result != null)
            {
                (_, _, methodToken, ilOffset) = result.Value;
                assemblyName = Path.GetFileNameWithoutExtension(loaded.Path);
                _log.LogInfo(_logStore, $"  Resolved {filePath}:{line} -> token=0x{methodToken:X8} IL={ilOffset} in {assemblyName}");
                foundInPdb = true;
                break;
            }
        }

        if (!foundInPdb)
        {
            (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? diskResult = FindMethodFromDiskPdb(filePath, line);
            if (diskResult != null)
            {
                methodToken = diskResult.Value.MethodToken;
                ilOffset = diskResult.Value.ILOffset;
                assemblyName = diskResult.Value.AssemblyName;
                _log.LogInfo(_logStore, $"  Found method via disk PDB: {diskResult.Value.MethodName} token=0x{methodToken:X8} in {assemblyName} — but module not in ICorDebug yet");
                foundInPdb = true;
            }
        }

        if (!foundInPdb)
        {
            // C++/CLI files: dbgeng reads Windows PDBs natively. Use GetOffsetByLine
            // to get the IL section address, then compute RVA → find method token from
            // PE metadata. The token is used to match against profiler JIT notifications,
            // which provide the actual JIT'd native address for the hw breakpoint.
            if (_sourceFiles.IsCliFile(filePath))
            {
                _log.LogInfo(_logStore, $"  C++/CLI file detected: {filePath}");
                (ulong ilAddr, bool cliResolved) = _dbgEng.GetOffsetByLine(model.Wrapper, (uint)line, filePath);
                if (cliResolved && ilAddr != 0)
                {
                    ulong? moduleBase = _dbgEng.GetModuleByOffset(model.Wrapper, ilAddr);
                    if (moduleBase != null && moduleBase.Value != 0)
                    {
                        int rva = (int)(ilAddr - moduleBase.Value);
                        assemblyName = ResolveCliAssemblyName(filePath);
                        string? dllPath = FindCliAssemblyDll(filePath, assemblyName);
                        _log.LogInfo(_logStore, $"  C++/CLI: ilAddr=0x{ilAddr:X} base=0x{moduleBase.Value:X} rva=0x{rva:X} asm={assemblyName} dll={dllPath}");
                        if (dllPath != null)
                        {
                            int? token = _pdbMapper.FindTokenByRva(dllPath, rva);
                            if (token != null)
                            {
                                methodToken = token.Value;
                                _log.LogInfo(_logStore,
                                    $"  C++/CLI: resolved token=0x{methodToken:X8} in {assemblyName}");
                                foundInPdb = true;
                                isCliMethod = true;
                            }
                            else
                            {
                                _log.LogWarning(_logStore, $"  C++/CLI: no method found at RVA 0x{rva:X} in {dllPath}");
                            }
                        }
                    }
                    else
                    {
                        _log.LogWarning(_logStore, $"  C++/CLI: GetModuleByOffset returned null");
                    }
                }
                else
                {
                    _log.LogInfo(_logStore, $"  C++/CLI: GetOffsetByLine({line}) failed");
                }
                if (!foundInPdb)
                    return false; // Module not loaded yet — stays as PendingManagedBreakpoint.
            }
            else
            {
                return false;
            }
        }

        // Try direct resolution via GetOffsetByLine (works if method is already JIT'd).
        // Skip for C++/CLI: GetOffsetByLine returns the IL section address (not JIT'd code).
        // C++/CLI methods must go through the deferred + JIT notification path.
        ulong offset = 0;
        bool offsetResolved = false;
        if (!isCliMethod)
            (offset, offsetResolved) = _dbgEng.GetOffsetByLine(model.Wrapper, (uint)line, filePath);
        if (offsetResolved && offset != 0)
        {
            _log.LogInfo(_logStore, $"  GetOffsetByLine({line}) -> 0x{offset:X} — setting hardware breakpoint");
            uint? hwBpId = SetManagedCodeBreakpoint(model, offset, filePath, line);
            if (hwBpId != null)
            {
                if (!model.ManagedFileBreakpointIds.ContainsKey(filePath))
                    model.ManagedFileBreakpointIds[filePath] = [];
                model.ManagedFileBreakpointIds[filePath].Add(bpId);
                return true;
            }
            _log.LogWarning(_logStore, $"  Hardware breakpoint limit reached for managed bp #{bpId}");
            return false;
        }

        // Method not JIT'd yet — store as deferred for periodic polling.
        model.DeferredManagedBreakpoints.Add(
            new DeferredManagedBreakpoint(filePath, line, methodToken, ilOffset, bpId, assemblyName, isCliMethod));
        if (!model.ManagedFileBreakpointIds.ContainsKey(filePath))
            model.ManagedFileBreakpointIds[filePath] = [];
        model.ManagedFileBreakpointIds[filePath].Add(bpId);
        _log.LogInfo(_logStore, $"  Deferred managed bp #{bpId}: method not JIT'd yet");
        return true;
    }



    /// <summary>
    /// Sets a hardware execution breakpoint (<c>ba e1</c>) at the given native address.
    /// Uses CPU debug registers — no code patching, so safe for managed code.
    /// Returns the dbgeng breakpoint ID, or <c>null</c> on failure.
    /// </summary>
    private uint? SetManagedCodeBreakpoint(NativeDebuggerModel model, ulong address, string filePath, int line)
    {
        (uint bpId, bool success) = _dbgEng.AddHardwareBreakpoint(model.Wrapper, address, 1);
        if (!success)
        {
            _log.LogWarning(_logStore, $"  AddHardwareBreakpoint failed at 0x{address:X}");
            return null;
        }

        string key = $"{filePath}:{line}";
        model.BreakpointIds[key] = bpId;
        _ = model.UserBreakpointIds.Add(bpId);
        _ = model.ManagedBreakpointIds.Add(bpId);
        _ = model.ManagedBreakpointAddresses.Add(address);
        model.ManagedBreakpointSources[address] = (filePath, line);

        _log.LogInfo(_logStore, $"  Hardware bp #{bpId} set at 0x{address:X} for {key}");
        return bpId;
    }

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

                uint? hwBpId = SetManagedCodeBreakpoint(model, nativeAddress, deferred.FilePath, deferred.Line);
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
        {
            // Match against deferred breakpoints by token + assembly name.
            foreach (DeferredManagedBreakpoint deferred in model.DeferredManagedBreakpoints)
            {
                if (deferred.MethodToken == jit.MethodToken &&
                    deferred.AssemblyName != null &&
                    deferred.AssemblyName.Equals(jit.AssemblyName, StringComparison.OrdinalIgnoreCase) &&
                    !bound.Contains(deferred))
                {
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
                        // With hooks: don't set hardware BP here — the ENTER path will
                        // set a transient BP using this resolved address. Don't signal ACK
                        // either — the ENTER handler does that.
                        _log.LogInfo(_logStore, $"  Hooks active: stored address, ENTER will set BP");
                        continue;
                    }

                    // Without hooks: set hardware BP now and signal ACK.
                    uint? hwBpId = SetManagedCodeBreakpoint(model, jit.NativeAddress, deferred.FilePath, deferred.Line);

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
        }

        foreach (DeferredManagedBreakpoint r in bound)
            _ = model.DeferredManagedBreakpoints.Remove(r);

        // Signal the ACK event to unblock the profiler's JITCompilationFinished callback.
        // The hardware BP is now set, so when the profiler unblocks and the CLR dispatches
        // to the freshly JIT'd code, the BP will fire immediately.
        if (resolved.Count > 0)
            _ = (model.ProfilerAckEvent?.Set());

        return [.. resolved];
    }

    public void SetTransientBreakpoint(NativeDebuggerModel model, ulong address, string filePath, int line) => SetManagedCodeBreakpoint(model, address, filePath, line);

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

        // Find the assembly path from loaded modules (match by assembly name).
        string? assemblyPath = null;
        if (model.CorWrapper != null)
        {
            ManagedModuleInfo? moduleInfo = _corDebug.FindModuleByName(model.CorWrapper, method.AssemblyName);
            assemblyPath = moduleInfo?.Path;

            // Fallback: search near known module paths.
            if (assemblyPath == null)
            {
                foreach (ManagedModuleInfo mod in _corDebug.GetModules(model.CorWrapper))
                {
                    if (mod.Path == null) continue;
                    string? dir = Path.GetDirectoryName(mod.Path);
                    if (dir == null) continue;
                    string candidate = Path.Combine(dir, method.AssemblyName + ".dll");
                    if (File.Exists(candidate))
                    {
                        assemblyPath = candidate;
                        break;
                    }
                }
            }
        }

        // Resolve method name and source location via PDB.
        string methodName = $"[{method.AssemblyName}] 0x{method.MethodToken:X8}";
        Source? source = null;
        int line = 0;

        if (assemblyPath != null)
        {
            try
            {

                // Compute IL offset from native IP using the IL-to-native mapping.
                int ilOffset = 0;
                string bpKey = $"{method.AssemblyName}:{method.MethodToken:X8}";
                if (model.JitMethodMappings.TryGetValue(bpKey, out JitMethodMapping? methodMapping))
                {
                    uint nativeOffset = (uint)(instructionPointer - methodMapping.CodeStart);
                    // Find the largest IL offset whose native start ≤ our offset.
                    foreach ((int il, int nat) in methodMapping.ILToNativeMap)
                    {
                        if ((uint)nat <= nativeOffset)
                            ilOffset = il;
                    }
                }

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
                if (source == null && (model.ManagedBreakpointSources.TryGetValue(
                    method.StartAddress, out (string File, int Line) bpSource) ||
                    model.ManagedBreakpointSources.TryGetValue(
                    instructionPointer, out bpSource)))
                {
                    source = new Source
                    {
                        Name = Path.GetFileName(bpSource.File),
                        Path = bpSource.File,
                    };
                    line = bpSource.Line;
                }
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"  Profiler frame resolution failed for token 0x{method.MethodToken:X8}: {ex.Message}");
            }
        }

        return (methodName, source, line);
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

    private Breakpoint[] TryBindPendingBreakpoints(NativeDebuggerModel model)
    {
        List<Breakpoint> resolved = [];
        List<PendingManagedBreakpoint> bound = [];

        foreach (PendingManagedBreakpoint pending in model.PendingILBreakpoints)
        {
            if (TryBindBreakpoint(model, pending.FilePath, pending.Line, pending.BpId))
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

    private void ClearManagedBreakpointsForFile(NativeDebuggerModel model, string filePath)
    {
        if (model.ManagedFileBreakpointIds.TryGetValue(filePath, out List<int>? existingIds))
        {
            foreach (int id in existingIds)
            {
                // Remove hardware breakpoints set by the managed debugger.
                KeyValuePair<string, uint> key = model.BreakpointIds.FirstOrDefault(kv => kv.Value == (uint)id
                    || kv.Key.StartsWith(filePath + ":", StringComparison.OrdinalIgnoreCase));
                if (key.Key != null && model.BreakpointIds.TryGetValue(key.Key, out uint hwId))
                {
                    _ = _dbgEng.RemoveBreakpoint(model.Wrapper, hwId);
                    _ = model.UserBreakpointIds.Remove(hwId);
                    _ = model.ManagedBreakpointIds.Remove(hwId);
                    _ = model.BreakpointIds.Remove(key.Key);
                }

                // Also deactivate any ICorDebug breakpoints (legacy path).
                if (model.CorWrapper != null)
                    _corDebug.DeactivateLegacyBreakpoint(model.CorWrapper, id);
            }
            existingIds.Clear();
        }

        _ = model.PendingILBreakpoints.RemoveAll(p =>
            p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        _ = model.DeferredManagedBreakpoints.RemoveAll(d =>
            d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
    }



    /// <summary>
    /// Finds the method by searching for PDB files on disk near the source file's
    /// project. Walks up from the source directory looking for a .csproj, then
    /// scans bin/ subdirectories for a matching PDB.
    /// </summary>
    private (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? FindMethodFromDiskPdb(
        string sourceFile, int line)
    {
        string? dir = Path.GetDirectoryName(sourceFile);
        string? projectDir = null;
        string? projectName = null;

        for (int up = 0; up < 5 && dir != null; up++)
        {
            string[] csprojs = Directory.GetFiles(dir, "*.csproj");
            if (csprojs.Length > 0)
            {
                projectDir = dir;
                projectName = Path.GetFileNameWithoutExtension(csprojs[0]);
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }

        if (projectDir == null || projectName == null)
            return null;

        string[] searchDirs = ["bin", "obj"];
        foreach (string? subDir in searchDirs)
        {
            string binDir = Path.Combine(projectDir, subDir);
            if (!Directory.Exists(binDir))
                continue;

            foreach (string pdbFile in Directory.GetFiles(binDir, $"{projectName}.pdb", SearchOption.AllDirectories))
            {
                string assemblyPath = Path.ChangeExtension(pdbFile, ".dll");
                if (!File.Exists(assemblyPath))
                    continue;

                (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result = _pdbMapper.FindMethodAtLine(assemblyPath, sourceFile, line);
                if (result != null)
                    return (result.Value.AssemblyName, result.Value.MethodName, result.Value.MethodToken, result.Value.ILOffset);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the assembly name for a C++/CLI source file by walking up directories
    /// looking for a .vcxproj with CLRSupport.
    /// </summary>
    private static string? ResolveCliAssemblyName(string sourceFile)
    {
        string? dir = Path.GetDirectoryName(sourceFile);
        for (int up = 0; up < 5 && dir != null; up++)
        {
            try
            {
                foreach (string vcx in Directory.GetFiles(dir, "*.vcxproj"))
                {
                    if (File.ReadAllText(vcx).Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                        return Path.GetFileNameWithoutExtension(vcx);
                }
            }
            catch { }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Finds the built DLL for a C++/CLI project by searching bin/ directories.
    /// </summary>
    private static string? FindCliAssemblyDll(string sourceFile, string? assemblyName)
    {
        if (assemblyName == null) return null;
        string? dir = Path.GetDirectoryName(sourceFile);
        for (int up = 0; up < 5 && dir != null; up++)
        {
            try
            {
                foreach (string vcx in Directory.GetFiles(dir, "*.vcxproj"))
                {
                    if (Path.GetFileNameWithoutExtension(vcx).Equals(assemblyName, StringComparison.OrdinalIgnoreCase)
                        && File.ReadAllText(vcx).Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                    {
                        // Search bin/ for the DLL.
                        string binDir = Path.Combine(dir, "bin");
                        if (Directory.Exists(binDir))
                        {
                            foreach (string dll in Directory.GetFiles(binDir, $"{assemblyName}.dll", SearchOption.AllDirectories))
                                return dll;
                        }
                        // Also check output in the WpfApp bin (typical for C++/CLI wrapper).
                        string? parent = Path.GetDirectoryName(dir);
                        if (parent != null)
                        {
                            foreach (string subDir in Directory.GetDirectories(parent))
                            {
                                string subBin = Path.Combine(subDir, "bin");
                                if (Directory.Exists(subBin))
                                {
                                    foreach (string dll in Directory.GetFiles(subBin, $"{assemblyName}.dll", SearchOption.AllDirectories))
                                        return dll;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public List<(string Assembly, int Token)> ResolveTokensFromBreakpoints(
        IEnumerable<(string FilePath, int Line)> breakpoints)
    {
        List<(string Assembly, int Token)> tokens = [];
        foreach ((string? filePath, int line) in breakpoints)
        {
            try
            {
                (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result = FindMethodFromDiskPdb(filePath, line);
                if (result != null)
                    tokens.Add((result.Value.AssemblyName, result.Value.MethodToken));
            }
            catch { }
        }
        return tokens;
    }

    public List<string> ResolveWatchAssemblies(
        IEnumerable<(string FilePath, int Line)> breakpoints)
    {
        HashSet<string> assemblies = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? filePath, _) in breakpoints)
        {
            if (_sourceFiles.IsCliFile(filePath))
            {
                string? asmName = ResolveCliAssemblyName(filePath);
                if (asmName != null)
                    _ = assemblies.Add(asmName);
            }
        }
        return [.. assemblies];
    }

    // ── Methods moved from NativeDebuggerService ────────────────────

    public void TryInitializeManaged(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Initializing managed debugging (CLR detected)...");
        if (InitializeRuntime(model))
        {
            // Apply any managed breakpoints that were pending before CLR loaded.
            foreach (SetBreakpointsArguments pending in model.PendingManagedBreakpoints)
            {
                Breakpoint[] bps = SetManagedBreakpoints(
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
                StartDeferredBreakpointPoller(model);
        }
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

    public void RemoveTransientManagedBreakpoints(NativeDebuggerModel model)
    {
        // Only remove BPs when using enter/leave hooks (BPs are transient per-call).
        // With JIT-blocking fallback, BPs are permanent and must persist.
        if (!model.ProfilerHooksActive || model.ManagedBreakpointIds.Count == 0)
            return;

        List<uint> idsToRemove = [.. model.ManagedBreakpointIds];
        foreach (uint hwBpId in idsToRemove)
        {
            if (_dbgEng.RemoveBreakpoint(model.Wrapper, hwBpId))
                _log.LogInfo(_logStore, $"Removed transient managed hw bp #{hwBpId}");
            _ = model.UserBreakpointIds.Remove(hwBpId);
        }
        model.ManagedBreakpointIds.Clear();
        model.ManagedBreakpointAddresses.Clear();

        // Clear the key→id mappings for managed breakpoints.
        List<string> keysToRemove = [.. model.BreakpointIds
            .Where(kv => idsToRemove.Contains(kv.Value))
            .Select(kv => kv.Key)];
        foreach (string key in keysToRemove)
            _ = model.BreakpointIds.Remove(key);
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

    public void ProcessPendingManagedBreakpoints(NativeDebuggerModel model)
    {
        // Process JIT notifications from the CLR profiler pipe.
        if (model.DeferredManagedBreakpoints.Count > 0 && !model.JitNotifications.IsEmpty)
        {
            try
            {
                Breakpoint[] jitResolved = HandleJitNotifications(model);
                foreach (Breakpoint bp in jitResolved)
                {
                    _log.LogInfo(_logStore, $"Profiler JIT bp resolved: id={bp.Id} verified={bp.Verified}");
                    _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                    {
                        Reason = "changed",
                        Breakpoint = bp,
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"HandleJitNotifications failed: {ex.Message}");
            }
        }

        // Fallback: try to resolve deferred managed breakpoints via DAC/XCLRData.
        // Skip when hooks are active — deferred BPs are consumed by ENTER notifications.
        if (!model.ProfilerHooksActive &&
            model.ManagedInitialized && model.DeferredManagedBreakpoints.Count > 0)
        {
            try
            {
                Breakpoint[] resolved = TryResolveDeferredBreakpoints(model);
                foreach (Breakpoint bp in resolved)
                {
                    _log.LogInfo(_logStore, $"Deferred managed bp resolved: id={bp.Id} verified={bp.Verified}");
                    _server.SendEvent(_transport, "breakpoint", new BreakpointEventBody
                    {
                        Reason = "changed",
                        Breakpoint = bp,
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"TryResolveDeferredBreakpoints failed: {ex.Message}");
            }
        }
    }

    public bool HandleEnterBreakpoint(NativeDebuggerModel model)
    {
        // Initialize managed debugging when CLR is first detected.
        if (model.ClrLoaded && !model.ManagedInitialized)
            TryInitializeManaged(model);

        // Process JIT notifications and resolve deferred managed breakpoints.
        ProcessPendingManagedBreakpoints(model);

        if (!model.ProfilerHooksActive || !model.PendingEnterBreakpoint)
            return false;

        model.PendingEnterBreakpoint = false;
        // Find the matching deferred BP and compute exact native address
        // from the IL-to-native mapping (resolves breakpoint line → native offset).
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
                SetTransientBreakpoint(model, addr, deferred.FilePath, deferred.Line);
                _log.LogInfo(_logStore, $"  ENTER: transient hw BP at 0x{addr:X} for {deferred.FilePath}:{deferred.Line}");
                matched = true;
                break;
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
}
