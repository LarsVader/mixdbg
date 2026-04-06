using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using MixDbg.Models.Dap;
using MixDbg.Engine.Sos;
using MixDbg.Models;

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
    ICorDebugWrapper corDebugWrapper) : IManagedDebugger
{
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly ISourceFileService _sourceFiles = sourceFiles;
    private readonly IDapServer _server = server;
    private readonly DapServerModel _transport = transport;
    private readonly IDbgEngWrapper _dbgEng = dbgEngWrapper;
    private readonly ICorDebugWrapper _corDebug = corDebugWrapper;

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
        var result = _corDebug.InitializeRuntime(
            model.CorWrapper, model.Wrapper, model.CoreClrPath, model.CoreClrBaseAddress);
        if (result)
            model.ManagedInitialized = true;
        return result;
    }

    public Breakpoint[] SetManagedBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested)
    {
        _log.LogInfo(_logStore, $"SetManagedBreakpoints: file={filePath} count={requested.Length}");

        // Clear existing managed breakpoints for this file.
        ClearManagedBreakpointsForFile(model, filePath);

        var results = new Breakpoint[requested.Length];
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

        var currentOsId = _dbgEng.GetCurrentThreadSystemId(model.Wrapper);
        var managedFrames = _corDebug.GetManagedStackFrames(model.CorWrapper, currentOsId);
        if (managedFrames.Length == 0)
            return [];

        int frameId = 1;
        return managedFrames.Select(f => new StackFrame
        {
            Id = frameId++,
            Name = f.Name,
            Source = f.SourceFile != null ? new Source
            {
                Name = Path.GetFileName(f.SourceFile),
                Path = f.SourceFile,
            } : null,
            Line = f.Line,
            Column = 0,
        }).ToArray();
    }

    public Breakpoint[] OnModuleLoad(NativeDebuggerModel model)
    {
        if (!model.ManagedInitialized)
            return [];

        // Re-enumerate ICorDebug modules to pick up newly loaded assemblies.
        _corDebug.RefreshModules(model.CorWrapper);

        // Try to bind pending managed breakpoints against the new modules.
        return TryBindPendingBreakpoints(model);
    }

    // ── Private ─────────────────────────────────────────

    private Breakpoint SetOneManagedBreakpoint(
        NativeDebuggerModel model, string filePath, SourceBreakpoint req)
    {
        var bpId = ++model.NextBpId;

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
            model.ManagedFileBreakpointIds[filePath] = new List<int>();
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
        using (var mapper = new PdbSourceMapper())
        {
            foreach (var loaded in _corDebug.GetModules(model.CorWrapper))
            {
                if (loaded.PdbPath == null || loaded.Path == null)
                    continue;
                var result = mapper.FindMethodAtLine(loaded.Path, filePath, line);
                if (result != null)
                {
                    (_, _, methodToken, ilOffset) = result.Value;
                    assemblyName = Path.GetFileNameWithoutExtension(loaded.Path);
                    _log.LogInfo(_logStore, $"  Resolved {filePath}:{line} -> token=0x{methodToken:X8} IL={ilOffset} in {assemblyName}");
                    foundInPdb = true;
                    break;
                }
            }
        }

        if (!foundInPdb)
        {
            var diskResult = FindMethodFromDiskPdb(filePath, line);
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
                var (ilAddr, cliResolved) = _dbgEng.GetOffsetByLine(model.Wrapper, (uint)line, filePath);
                if (cliResolved && ilAddr != 0)
                {
                    var moduleBase = _dbgEng.GetModuleByOffset(model.Wrapper, ilAddr);
                    if (moduleBase != null && moduleBase.Value != 0)
                    {
                        int rva = (int)(ilAddr - moduleBase.Value);
                        assemblyName = ResolveCliAssemblyName(filePath);
                        var dllPath = FindCliAssemblyDll(filePath, assemblyName);
                        _log.LogInfo(_logStore, $"  C++/CLI: ilAddr=0x{ilAddr:X} base=0x{moduleBase.Value:X} rva=0x{rva:X} asm={assemblyName} dll={dllPath}");
                        if (dllPath != null)
                        {
                            using var mapper = new PdbSourceMapper();
                            var token = mapper.FindTokenByRva(dllPath, rva);
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
            var hwBpId = SetManagedCodeBreakpoint(model, offset, filePath, line);
            if (hwBpId != null)
            {
                if (!model.ManagedFileBreakpointIds.ContainsKey(filePath))
                    model.ManagedFileBreakpointIds[filePath] = new List<int>();
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
            model.ManagedFileBreakpointIds[filePath] = new List<int>();
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
        var (bpId, success) = _dbgEng.AddHardwareBreakpoint(model.Wrapper, address, 1);
        if (!success)
        {
            _log.LogWarning(_logStore, $"  AddHardwareBreakpoint failed at 0x{address:X}");
            return null;
        }

        var key = $"{filePath}:{line}";
        model.BreakpointIds[key] = bpId;
        model.UserBreakpointIds.Add(bpId);
        model.ManagedBreakpointIds.Add(bpId);
        model.ManagedBreakpointAddresses.Add(address);
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
        try { _corDebug.InitializeDac(model.CorWrapper, model.Wrapper, model.CoreClrPath!, model.CoreClrBaseAddress); }
        catch { }

        var resolved = new List<Breakpoint>();
        var bound = new List<DeferredManagedBreakpoint>();

        foreach (var deferred in model.DeferredManagedBreakpoints)
        {
            try
            {
                // Use the DAC (XCLRDataProcess) to find the real JIT native entry point.
                var nativeAddress = _corDebug.ResolveNativeEntryViaXclrData(model.CorWrapper, deferred.MethodToken, deferred.AssemblyName);
                if (nativeAddress == 0)
                    continue;

                _log.LogInfo(_logStore, $"  Deferred bp #{deferred.BpId}: XCLRData entry=0x{nativeAddress:X}");

                var hwBpId = SetManagedCodeBreakpoint(model, nativeAddress, deferred.FilePath, deferred.Line);
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

        foreach (var r in bound)
            model.DeferredManagedBreakpoints.Remove(r);

        return resolved.ToArray();
    }

    public Breakpoint[] HandleJitNotifications(NativeDebuggerModel model)
    {
        if (model.DeferredManagedBreakpoints.Count == 0 || model.JitNotifications.IsEmpty)
            return [];

        var resolved = new List<Breakpoint>();
        var bound = new List<DeferredManagedBreakpoint>();

        // Drain all pending JIT notifications from the profiler pipe.
        while (model.JitNotifications.TryDequeue(out var jit))
        {
            // Match against deferred breakpoints by token + assembly name.
            foreach (var deferred in model.DeferredManagedBreakpoints)
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
                    var bpKey = $"{jit.AssemblyName}:{jit.MethodToken:X8}";
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
                    var hwBpId = SetManagedCodeBreakpoint(model, jit.NativeAddress, deferred.FilePath, deferred.Line);

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

        foreach (var r in bound)
            model.DeferredManagedBreakpoints.Remove(r);

        // Signal the ACK event to unblock the profiler's JITCompilationFinished callback.
        // The hardware BP is now set, so when the profiler unblocks and the CLR dispatches
        // to the freshly JIT'd code, the BP will fire immediately.
        if (resolved.Count > 0)
            model.ProfilerAckEvent?.Set();

        return resolved.ToArray();
    }

    public void SetTransientBreakpoint(NativeDebuggerModel model, ulong address, string filePath, int line)
    {
        SetManagedCodeBreakpoint(model, address, filePath, line);
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

        // Find the assembly path from loaded modules (match by assembly name).
        string? assemblyPath = null;
        if (model.CorWrapper != null)
        {
            var moduleInfo = _corDebug.FindModuleByName(model.CorWrapper, method.AssemblyName);
            assemblyPath = moduleInfo?.Path;

            // Fallback: search near known module paths.
            if (assemblyPath == null)
            {
                foreach (var mod in _corDebug.GetModules(model.CorWrapper))
                {
                    if (mod.Path == null) continue;
                    var dir = Path.GetDirectoryName(mod.Path);
                    if (dir == null) continue;
                    var candidate = Path.Combine(dir, method.AssemblyName + ".dll");
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
                using var mapper = new PdbSourceMapper();

                // Compute IL offset from native IP using the IL-to-native mapping.
                int ilOffset = 0;
                var bpKey = $"{method.AssemblyName}:{method.MethodToken:X8}";
                if (model.JitMethodMappings.TryGetValue(bpKey, out var methodMapping))
                {
                    uint nativeOffset = (uint)(instructionPointer - methodMapping.CodeStart);
                    // Find the largest IL offset whose native start ≤ our offset.
                    foreach (var (il, nat) in methodMapping.ILToNativeMap)
                    {
                        if ((uint)nat <= nativeOffset)
                            ilOffset = il;
                    }
                }

                var srcLoc = mapper.GetSourceLocation(assemblyPath, method.MethodToken, ilOffset);
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
                var methodInfo = mapper.GetMethodName(assemblyPath, method.MethodToken);
                if (methodInfo != null)
                    methodName = methodInfo;

                // C++/CLI fallback: PdbSourceMapper can't read Windows PDBs.
                // Look up the source from the managed BP we set at this method's address.
                // Check both method start and exact IP (transient BPs from ENTER hooks
                // are set at an IL-mapped offset, not the method start).
                if (source == null && (model.ManagedBreakpointSources.TryGetValue(
                    method.StartAddress, out var bpSource) ||
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

        var keys = map.Keys;
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

        var entry = map.Values[hi];
        if (ip < entry.StartAddress + entry.CodeSize)
            return entry;

        return null;
    }

    private Breakpoint[] TryBindPendingBreakpoints(NativeDebuggerModel model)
    {
        var resolved = new List<Breakpoint>();
        var bound = new List<PendingManagedBreakpoint>();

        foreach (var pending in model.PendingILBreakpoints)
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

        foreach (var r in bound)
            model.PendingILBreakpoints.Remove(r);

        return resolved.ToArray();
    }

    private void ClearManagedBreakpointsForFile(NativeDebuggerModel model, string filePath)
    {
        if (model.ManagedFileBreakpointIds.TryGetValue(filePath, out var existingIds))
        {
            foreach (var id in existingIds)
            {
                // Remove hardware breakpoints set by the managed debugger.
                var key = model.BreakpointIds.FirstOrDefault(kv => kv.Value == (uint)id
                    || kv.Key.StartsWith(filePath + ":", StringComparison.OrdinalIgnoreCase));
                if (key.Key != null && model.BreakpointIds.TryGetValue(key.Key, out var hwId))
                {
                    _dbgEng.RemoveBreakpoint(model.Wrapper, hwId);
                    model.UserBreakpointIds.Remove(hwId);
                    model.ManagedBreakpointIds.Remove(hwId);
                    model.BreakpointIds.Remove(key.Key);
                }

                // Also deactivate any ICorDebug breakpoints (legacy path).
                if (model.CorWrapper != null)
                    _corDebug.DeactivateLegacyBreakpoint(model.CorWrapper, id);
            }
            existingIds.Clear();
        }

        model.PendingILBreakpoints.RemoveAll(p =>
            p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        model.DeferredManagedBreakpoints.RemoveAll(d =>
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
        var dir = Path.GetDirectoryName(sourceFile);
        string? projectDir = null;
        string? projectName = null;

        for (int up = 0; up < 5 && dir != null; up++)
        {
            var csprojs = Directory.GetFiles(dir, "*.csproj");
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

        var searchDirs = new[] { "bin", "obj" };
        foreach (var subDir in searchDirs)
        {
            var binDir = Path.Combine(projectDir, subDir);
            if (!Directory.Exists(binDir))
                continue;

            foreach (var pdbFile in Directory.GetFiles(binDir, $"{projectName}.pdb", SearchOption.AllDirectories))
            {
                var assemblyPath = Path.ChangeExtension(pdbFile, ".dll");
                if (!File.Exists(assemblyPath))
                    continue;

                using var mapper = new PdbSourceMapper();
                var result = mapper.FindMethodAtLine(assemblyPath, sourceFile, line);
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
        var dir = Path.GetDirectoryName(sourceFile);
        for (int up = 0; up < 5 && dir != null; up++)
        {
            try
            {
                foreach (var vcx in Directory.GetFiles(dir, "*.vcxproj"))
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
        var dir = Path.GetDirectoryName(sourceFile);
        for (int up = 0; up < 5 && dir != null; up++)
        {
            try
            {
                foreach (var vcx in Directory.GetFiles(dir, "*.vcxproj"))
                {
                    if (Path.GetFileNameWithoutExtension(vcx).Equals(assemblyName, StringComparison.OrdinalIgnoreCase)
                        && File.ReadAllText(vcx).Contains("<CLRSupport>", StringComparison.OrdinalIgnoreCase))
                    {
                        // Search bin/ for the DLL.
                        var binDir = Path.Combine(dir, "bin");
                        if (Directory.Exists(binDir))
                        {
                            foreach (var dll in Directory.GetFiles(binDir, $"{assemblyName}.dll", SearchOption.AllDirectories))
                                return dll;
                        }
                        // Also check output in the WpfApp bin (typical for C++/CLI wrapper).
                        var parent = Path.GetDirectoryName(dir);
                        if (parent != null)
                        {
                            foreach (var subDir in Directory.GetDirectories(parent))
                            {
                                var subBin = Path.Combine(subDir, "bin");
                                if (Directory.Exists(subBin))
                                {
                                    foreach (var dll in Directory.GetFiles(subBin, $"{assemblyName}.dll", SearchOption.AllDirectories))
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
        var tokens = new List<(string Assembly, int Token)>();
        foreach (var (filePath, line) in breakpoints)
        {
            try
            {
                var result = FindMethodFromDiskPdb(filePath, line);
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
        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, _) in breakpoints)
        {
            if (_sourceFiles.IsCliFile(filePath))
            {
                var asmName = ResolveCliAssemblyName(filePath);
                if (asmName != null)
                    assemblies.Add(asmName);
            }
        }
        return assemblies.ToList();
    }

    // ── Methods moved from NativeDebuggerService ────────────────────

    public void TryInitializeManaged(NativeDebuggerModel model)
    {
        _log.LogInfo(_logStore, "Initializing managed debugging (CLR detected)...");
        if (InitializeRuntime(model))
        {
            // Apply any managed breakpoints that were pending before CLR loaded.
            foreach (var pending in model.PendingManagedBreakpoints)
            {
                var bps = SetManagedBreakpoints(
                    model, pending.Source.Path!, pending.Breakpoints);
                foreach (var bp in bps)
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
            var resolved = OnModuleLoad(model);
            foreach (var bp in resolved)
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

        var idsToRemove = new List<uint>(model.ManagedBreakpointIds);
        foreach (var hwBpId in idsToRemove)
        {
            if (_dbgEng.RemoveBreakpoint(model.Wrapper, hwBpId))
                _log.LogInfo(_logStore, $"Removed transient managed hw bp #{hwBpId}");
            model.UserBreakpointIds.Remove(hwBpId);
        }
        model.ManagedBreakpointIds.Clear();
        model.ManagedBreakpointAddresses.Clear();

        // Clear the key→id mappings for managed breakpoints.
        var keysToRemove = model.BreakpointIds
            .Where(kv => idsToRemove.Contains(kv.Value))
            .Select(kv => kv.Key).ToList();
        foreach (var key in keysToRemove)
            model.BreakpointIds.Remove(key);
    }

    public void MergeManagedFrames(NativeDebuggerModel model, StackFrame[] nativeFrames)
    {
        var managedFrames = GetManagedStackFrames(model);
        if (managedFrames.Length == 0)
            return;

        // Best-effort merge: for each native frame without source info, if the
        // next managed frame has a name, overlay it.
        int managedIdx = 0;
        for (int i = 0; i < nativeFrames.Length && managedIdx < managedFrames.Length; i++)
        {
            var nf = nativeFrames[i];

            // Skip frames that already resolved to native source.
            if (nf.Source != null)
                continue;

            // Check if this looks like a JIT-compiled or CLR infrastructure frame.
            var name = nf.Name ?? "";
            bool looksManaged = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || name.Contains("coreclr!", StringComparison.OrdinalIgnoreCase)
                || name.Contains("clrjit!", StringComparison.OrdinalIgnoreCase)
                || name.Contains("clr!", StringComparison.OrdinalIgnoreCase);

            if (!looksManaged)
                continue;

            // Overlay with the next managed frame.
            var mf = managedFrames[managedIdx++];
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
        var timer = new System.Threading.Timer(_ =>
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
            model.EngineThread?.Join(3000);
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
                var jitResolved = HandleJitNotifications(model);
                foreach (var bp in jitResolved)
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
                var resolved = TryResolveDeferredBreakpoints(model);
                foreach (var bp in resolved)
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
        if (!model.ProfilerHooksActive || !model.PendingEnterBreakpoint)
            return false;

        model.PendingEnterBreakpoint = false;
        // Find the matching deferred BP and compute exact native address
        // from the IL-to-native mapping (resolves breakpoint line → native offset).
        var bpKey = $"{model.EnterBreakpointAssembly}:{model.EnterBreakpointToken:X8}";
        bool matched = false;
        foreach (var deferred in model.DeferredManagedBreakpoints)
        {
            if (deferred.MethodToken == model.EnterBreakpointToken &&
                deferred.AssemblyName != null &&
                deferred.AssemblyName.Equals(model.EnterBreakpointAssembly, StringComparison.OrdinalIgnoreCase))
            {
                // Use IL-to-native mapping to get the exact address for the BP line.
                ulong addr = model.EnterBreakpointAddress; // fallback: body entry
                if (model.JitMethodMappings.TryGetValue(bpKey, out var mapping))
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
        model.ProfilerAckEvent?.Set();
        if (!matched)
        {
            // Non-BP method from assembly-level watch — re-enable hooks
            // immediately so the next method call also fires ENTER.
            _log.LogInfo(_logStore, $"  ENTER: no match for token=0x{model.EnterBreakpointToken:X8} — rehooking");
            model.ProfilerRehookEvent?.Set();
        }
        return true;
    }
}
