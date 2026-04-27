using MixDbg.Models;
using MixDbg.Models.DapMessages.Breakpoints;
using MixDbg.Models.DapMessages.Protocol;
using MixDbg.Services.Interfaces;

namespace MixDbg.Services;

/// <summary>
/// Stateless managed breakpoint setting and removal service. Handles binding
/// breakpoints to loaded modules via PDB resolution, setting hardware breakpoints,
/// and cleaning up managed breakpoints. Delegates all ICorDebug COM operations to
/// <see cref="ICorDebugWrapper"/>. All methods execute on the engine thread.
/// </summary>
internal sealed class ManagedBreakpointService(
    ILoggingService _log,
    LogStore _logStore,
    ISourceFileService _sourceFiles,
    IDbgEngWrapper _dbgEng,
    ICorDebugWrapper _corDebug,
    IPdbSourceMapper _pdbMapper) : IManagedBreakpointService
{
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

    public bool TryBindBreakpoint(NativeDebuggerModel model, string filePath, int line, int bpId)
    {
        // Try three resolution strategies in order: loaded modules, disk PDBs, C++/CLI.
        (int MethodToken, int ILOffset, string? AssemblyName, bool IsCliMethod)? resolved =
            ResolveMethodFromLoadedModules(model, filePath, line)
            ?? ResolveMethodFromDiskPdb(filePath, line)
            ?? ResolveMethodFromCliFile(model, filePath, line);

        return resolved != null
            && BindResolvedMethod(model, filePath, line, bpId, resolved.Value);
    }

    /// <summary>
    /// Searches ICorDebug loaded modules for a method at the given source location
    /// using portable PDB resolution.
    /// </summary>
    private (int MethodToken, int ILOffset, string? AssemblyName, bool IsCliMethod)? ResolveMethodFromLoadedModules(
        NativeDebuggerModel model, string filePath, int line)
    {
        foreach (ManagedModuleInfo loaded in _corDebug.GetModules(model.CorWrapper))
        {
            if (loaded.PdbPath == null || loaded.Path == null)
                continue;
            (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? result =
                _pdbMapper.FindMethodAtLine(loaded.Path, filePath, line);
            if (result != null)
            {
                string assemblyName = Path.GetFileNameWithoutExtension(loaded.Path);
                _log.LogInfo(_logStore, $"  Resolved {filePath}:{line} -> token=0x{result.Value.MethodToken:X8} IL={result.Value.ILOffset} in {assemblyName}");
                return (result.Value.MethodToken, result.Value.ILOffset, assemblyName, false);
            }
        }
        return null;
    }

    /// <summary>
    /// Searches for PDB files on disk near the source file's project (bin/obj).
    /// Returns method info if found, but the module may not be in ICorDebug yet.
    /// </summary>
    private (int MethodToken, int ILOffset, string? AssemblyName, bool IsCliMethod)? ResolveMethodFromDiskPdb(
        string filePath, int line)
    {
        (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? diskResult =
            FindMethodFromDiskPdb(filePath, line);
        if (diskResult == null)
            return null;

        _log.LogInfo(_logStore, $"  Found method via disk PDB: {diskResult.Value.MethodName} token=0x{diskResult.Value.MethodToken:X8} in {diskResult.Value.AssemblyName} — but module not in ICorDebug yet");
        return (diskResult.Value.MethodToken, diskResult.Value.ILOffset, diskResult.Value.AssemblyName, false);
    }

    /// <summary>
    /// Resolves a C++/CLI method token via dbgeng's Windows PDB support.
    /// Uses GetOffsetByLine to get the IL section address, computes RVA, then
    /// looks up the method token from PE metadata. Tries dbgeng module info
    /// first (works for any project layout), falls back to vcxproj scanning.
    /// </summary>
    private (int MethodToken, int ILOffset, string? AssemblyName, bool IsCliMethod)? ResolveMethodFromCliFile(
        NativeDebuggerModel model, string filePath, int line)
    {
        // Try for any C++ file — don't require vcxproj detection (fragile in large projects).
        if (!ISourceFileService.IsCppExtension(filePath))
            return null;

        (ulong ilAddr, bool cliResolved) = _dbgEng.GetOffsetByLine(model.Wrapper, (uint)line, filePath);
        if (!cliResolved || ilAddr == 0)
        {
            _log.LogInfo(_logStore, $"  C++/CLI: GetOffsetByLine({line}, {filePath}) -> not resolved (module not loaded in dbgeng?)");
            return null;
        }

        _log.LogInfo(_logStore, $"  C++/CLI: GetOffsetByLine({line}) -> 0x{ilAddr:X}");

        ulong? moduleBase = _dbgEng.GetModuleByOffset(model.Wrapper, ilAddr);
        if (moduleBase == null || moduleBase.Value == 0)
        {
            _log.LogWarning(_logStore, $"  C++/CLI: GetModuleByOffset returned null");
            return null;
        }

        int rva = (int)(ilAddr - moduleBase.Value);

        // Primary path: get module image path directly from dbgeng.
        string? dllPath = _dbgEng.GetModuleImagePath(model.Wrapper, moduleBase.Value);
        string? assemblyName = dllPath != null
            ? Path.GetFileNameWithoutExtension(dllPath)
            : null;

        // Fallback: vcxproj scanning (works when dbgeng module info unavailable).
        if (dllPath == null)
        {
            assemblyName = ResolveCliAssemblyName(filePath);
            dllPath = FindCliAssemblyDll(filePath, assemblyName);
        }

        _log.LogInfo(_logStore, $"  C++/CLI: ilAddr=0x{ilAddr:X} base=0x{moduleBase.Value:X} rva=0x{rva:X} asm={assemblyName} dll={dllPath}");

        if (dllPath == null)
            return null;

        int? token = _pdbMapper.FindTokenByRva(dllPath, rva);
        if (token == null)
        {
            _log.LogWarning(_logStore, $"  C++/CLI: no method found at RVA 0x{rva:X} in {dllPath}");
            return null;
        }

        _log.LogInfo(_logStore, $"  C++/CLI: resolved token=0x{token.Value:X8} in {assemblyName}");
        return (token.Value, 0, assemblyName, true);
    }

    /// <summary>
    /// After method resolution succeeds, registers a plan entry for the method. If the
    /// method already has a live activation on the stack (
    /// <see cref="NativeDebuggerModel.ActiveMethodBreakpoints"/>), also installs the HW BP
    /// immediately so the current execution can hit it; otherwise the BP is installed
    /// on the next FunctionEnter.
    /// </summary>
    private bool BindResolvedMethod(
        NativeDebuggerModel model, string filePath, int line, int bpId,
        (int MethodToken, int ILOffset, string? AssemblyName, bool IsCliMethod) resolved)
    {
        if (resolved.AssemblyName == null)
            return false;

        (int Token, string Assembly) key = (resolved.MethodToken, resolved.AssemblyName);

        // Always create/update the method plan so ENTER hooks install HW BPs on every call.
        MethodBreakpointSite site = new()
        {
            BpId = bpId,
            ILOffset = resolved.ILOffset,
            FilePath = filePath,
            Line = line,
        };
        AddSiteToPlan(model, key, site);
        TrackFileBreakpoint(model, filePath, bpId);

        // Send WATCH command to profiler so it enables FunctionEnter hooks for this method.
        // For C++/CLI, assembly-level watches are set up at pre-launch time.
        if (!resolved.IsCliMethod)
            SendWatchToken(model, resolved.AssemblyName, resolved.MethodToken);

        // If the method is already running on the stack (ActiveMethodBreakpoints entry
        // exists), install the HW BP immediately so the in-progress execution can hit it.
        if (model.ActiveMethodBreakpoints.TryGetValue(key, out ActiveMethodBreakpoint? active))
        {
            JitMethodInfo? jitInfo = FindInJitMethodMap(model, resolved.MethodToken, resolved.AssemblyName);
            if (jitInfo == null)
                return true;

            ulong targetAddress = jitInfo.StartAddress;
            if (model.JitMethodMappings.TryGetValue(key, out JitMethodMapping? mapping))
                targetAddress = mapping.GetNativeAddress(resolved.ILOffset);

            uint? hwBpId = SetManagedCodeBreakpoint(model, targetAddress, filePath, line);
            if (hwBpId != null)
            {
                active.InstalledBpIds.Add(hwBpId.Value);
                _ = active.InstalledAddresses.Add(targetAddress);
                _log.LogInfo(_logStore,
                    $"  Piggybacked onto live activation: hw BP #{hwBpId} at 0x{targetAddress:X}");
            }
            else
            {
                _log.LogWarning(_logStore, $"  HW BP limit reached piggybacking on live activation for bp #{bpId}");
            }
        }
        else if (FindInJitMethodMap(model, resolved.MethodToken, resolved.AssemblyName) is { } jitHit)
        {
            // Method is already JIT'd but has no ENTER tracking — FunctionIDMapper wasn't
            // called with this token (mid-session BP added after JIT). Install HW BP now;
            // it stays until the user clears the breakpoint (ClearManagedBreakpointsForFile).
            ulong targetAddress = jitHit.StartAddress;
            if (model.JitMethodMappings.TryGetValue(key, out JitMethodMapping? mapping))
                targetAddress = mapping.GetNativeAddress(resolved.ILOffset);

            uint? hwBpId = SetManagedCodeBreakpoint(model, targetAddress, filePath, line);
            if (hwBpId != null)
            {
                _log.LogInfo(_logStore,
                    $"  Installed hw BP #{hwBpId} at 0x{targetAddress:X} (method JIT'd, no active hooks)");
            }
        }

        // If the method isn't JIT'd yet (no JitMethodMap entry), store a deferred BP so
        // the JIT notification can fold it into the plan when the method compiles.
        if (FindInJitMethodMap(model, resolved.MethodToken, resolved.AssemblyName) == null)
        {
            model.DeferredManagedBreakpoints.Add(
                new DeferredManagedBreakpoint(filePath, line, resolved.MethodToken, resolved.ILOffset,
                    bpId, resolved.AssemblyName, resolved.IsCliMethod));
            model.RebuildDeferredBreakpointIndex();
            _log.LogInfo(_logStore, $"  Deferred managed bp #{bpId}: method not JIT'd yet");
        }

        return true;
    }

    private static void AddSiteToPlan(
        NativeDebuggerModel model, (int Token, string Assembly) key, MethodBreakpointSite site)
    {
        if (!model.ManagedBpPlans.TryGetValue(key, out ManagedMethodBreakpointPlan? plan))
        {
            plan = new ManagedMethodBreakpointPlan
            {
                MethodToken = key.Token,
                AssemblyName = key.Assembly,
            };
            model.ManagedBpPlans[key] = plan;
        }
        if (!plan.Sites.Exists(s => s.ILOffset == site.ILOffset && s.BpId == site.BpId))
            plan.Sites.Add(site);
    }

    /// <summary>
    /// Adds a breakpoint ID to the per-file tracking dictionary.
    /// </summary>
    private static void TrackFileBreakpoint(NativeDebuggerModel model, string filePath, int bpId)
    {
        if (!model.ManagedFileBreakpointIds.ContainsKey(filePath))
            model.ManagedFileBreakpointIds[filePath] = [];
        model.ManagedFileBreakpointIds[filePath].Add(bpId);
    }

    public uint? SetManagedCodeBreakpoint(NativeDebuggerModel model, ulong address, string filePath, int line)
    {
        (uint bpId, bool success) = _dbgEng.AddHardwareBreakpoint(model.Wrapper, address, 1);
        if (!success)
        {
            _log.LogWarning(_logStore, $"  AddHardwareBreakpoint failed at 0x{address:X}");
            return null;
        }

        // dbgeng reuses breakpoint IDs after removal. The new BP may take the same ID
        // as a previously-continued BP, which would cause the re-fire suppression to
        // swallow the legitimate first hit. Invalidate the tracking as soon as any new
        // BP is installed — it's a fresh BP regardless of the ID it reuses.
        if (model.LastContinuedBpId == bpId)
            model.LastContinuedBpId = uint.MaxValue;

        string key = $"{filePath}:{line}";
        model.BreakpointIds[key] = bpId;
        _ = model.UserBreakpointIds.Add(bpId);
        _ = model.ManagedBreakpointIds.Add(bpId);
        _ = model.ManagedBreakpointAddresses.Add(address);
        model.ManagedBreakpointSources[address] = (filePath, line);

        _log.LogInfo(_logStore, $"  Hardware bp #{bpId} set at 0x{address:X} for {key}");
        return bpId;
    }

    /// <inheritdoc />
    public Breakpoint? TryResolveCliBreakpoint(NativeDebuggerModel model, string filePath, int line, int bpId)
    {
        (int MethodToken, int ILOffset, string? AssemblyName, bool IsCliMethod)? resolved =
            ResolveMethodFromCliFile(model, filePath, line);

        return resolved != null && BindResolvedMethod(model, filePath, line, bpId, resolved.Value)
            ? new Breakpoint
            {
                Id = bpId,
                Verified = true,
                Line = line,
                Source = new Source
                {
                    Name = Path.GetFileName(filePath),
                    Path = filePath,
                },
            }
            : null;
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
                else
                    _log.LogInfo(_logStore, $"  ResolveTokens: no PDB match for {filePath}:{line}");
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"  ResolveTokens: failed for {filePath}:{line}: {ex.Message}");
            }
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
        TrackFileBreakpoint(model, filePath, bpId);

        return new Breakpoint
        {
            Id = bpId,
            Verified = true, // Optimistic — will bind on module load.
            Line = req.Line,
            Source = new Source { Name = Path.GetFileName(filePath), Path = filePath },
            Message = "Pending — module not yet loaded",
        };
    }

    /// <inheritdoc />
    public void ClearManagedBreakpointsForFile(NativeDebuggerModel model, string filePath)
    {
        HashSet<int> clearedBpIds = [];
        if (model.ManagedFileBreakpointIds.TryGetValue(filePath, out List<int>? existingIds))
        {
            foreach (int id in existingIds)
            {
                _ = clearedBpIds.Add(id);

                // Remove hardware breakpoints set by the managed debugger.
                KeyValuePair<string, uint> key = model.BreakpointIds.FirstOrDefault(kv => kv.Value == (uint)id
                    || kv.Key.StartsWith(filePath + ":", StringComparison.OrdinalIgnoreCase));
                if (key.Key != null && model.BreakpointIds.TryGetValue(key.Key, out uint hwId))
                {
                    _ = _dbgEng.RemoveBreakpoint(model.Wrapper, hwId);
                    _ = model.UserBreakpointIds.Remove(hwId);
                    _ = model.ManagedBreakpointIds.Remove(hwId);
                    _ = model.BreakpointIds.Remove(key.Key);

                    // Also drop from any ActiveMethodBreakpoints entry that owned this HW BP.
                    foreach (ActiveMethodBreakpoint active in model.ActiveMethodBreakpoints.Values)
                        _ = active.InstalledBpIds.Remove(hwId);
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
        model.RebuildDeferredBreakpointIndex();

        // Drop sites from method plans that match the file or any cleared BP id.
        List<(int Token, string Assembly)> emptyPlanKeys = [];
        foreach (KeyValuePair<(int Token, string Assembly), ManagedMethodBreakpointPlan> kv in model.ManagedBpPlans)
        {
            _ = kv.Value.Sites.RemoveAll(s =>
                s.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)
                || clearedBpIds.Contains(s.BpId));
            if (kv.Value.Sites.Count == 0)
                emptyPlanKeys.Add(kv.Key);
        }
        foreach ((int Token, string Assembly) emptyKey in emptyPlanKeys)
            _ = model.ManagedBpPlans.Remove(emptyKey);
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
    /// Sends a WATCH command directly to the profiler's command pipe so it enables
    /// FunctionEnter hooks for the specified method. Used for mid-session breakpoints.
    /// If the pipe is not yet connected, the command is queued and flushed later
    /// by the command pipe connect thread.
    /// Writes directly to the model's pipe writer to avoid a DI circular dependency
    /// with <see cref="IProfilerPipeService"/>.
    /// </summary>
    private void SendWatchToken(NativeDebuggerModel model, string assembly, int token)
    {
        string line = $"WATCH:{assembly}:{token:X8}";
        StreamWriter? writer = model.ProfilerCmdPipeWriter;
        if (writer == null)
        {
            model.PendingWatchCommands.Enqueue(line);
            _log.LogInfo(_logStore, $"  ProfilerCmd: queued {line} (pipe not connected yet)");
            return;
        }

        try
        {
            writer.WriteLine(line);
            _log.LogInfo(_logStore, $"  ProfilerCmd: sent {line}");
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"  ProfilerCmd: send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches the profiler's JitMethodMap for a method with the given token and assembly name.
    /// Returns the JIT info if found (method was already JIT'd before this BP was set).
    /// Uses the secondary index for O(1) lookup instead of scanning all values.
    /// </summary>
    private static JitMethodInfo? FindInJitMethodMap(NativeDebuggerModel model, int methodToken, string assemblyName)
    {
        lock (model.JitMethodMap)
        {
            return model.JitMethodMapByToken.TryGetValue((methodToken, assemblyName), out JitMethodInfo? info)
                ? info
                : null;
        }
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
}
