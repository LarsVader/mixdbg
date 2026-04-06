using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;
using MixDbg.Models.Dap;
using MixDbg.Engine.CorDebug;
using MixDbg.Engine.Sos;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless managed debugging service. Uses ICorDebug V4
/// (<c>ICLRDebugging::OpenVirtualProcess</c>) piggybacked on the existing dbgeng
/// session for IL-level breakpoints and managed stack traces.
/// All methods execute on the engine thread.
/// </summary>
internal sealed class ManagedDebuggerService(
    ILoggingService log,
    LogStore logStore,
    ISourceFileService sourceFiles) : IManagedDebugger
{
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;
    private readonly ISourceFileService _sourceFiles = sourceFiles;
    private IntPtr _dacHandle;

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

        try
        {
            _log.LogInfo(_logStore, $"Initializing ICorDebug V4 via OpenVirtualProcessImpl (coreclr={model.CoreClrPath}, base=0x{model.CoreClrBaseAddress:X})");

            // Create the data target bridge that lets ICorDebug read/write through dbgeng.
            var dataTarget = new DbgEngDataTarget(model.DataSpaces, model.Advanced, model.SysObjects);

            // Call mscordbi!OpenVirtualProcessImpl directly — the .NET 10 mscordbi.dll
            // doesn't export DllGetClassObject, and mscoree.dll's CLRCreateInstance
            // doesn't understand .NET 10 (returns CORDBG_E_UNSUPPORTED_FORWARD_COMPAT).
            model.CorProcess = OpenVirtualProcessImpl(
                model.CoreClrPath, model.CoreClrBaseAddress, dataTarget, _log, _logStore,
                out var clrVersion);
            _log.LogInfo(_logStore, $"ICorDebug V4 initialized: CLR {clrVersion.wMajor}.{clrVersion.wMinor}.{clrVersion.wBuild}");

            // Enumerate currently loaded modules.
            EnumerateModules(model);

            // Initialize the DAC (SOSDacInterface) for querying JIT native code addresses.
            try { TryInitializeDac(model); }
            catch (Exception ex) { _log.LogWarning(_logStore, $"DAC init outer catch: {ex.Message}"); }

            model.ManagedInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(_logStore, $"Failed to initialize managed debugging: {ex.Message}");
            return false;
        }
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
        if (model.CorProcess == null)
            return [];

        try
        {
            // Find the CLR thread matching the current dbgeng event thread.
            model.SysObjects.GetCurrentThreadSystemId(out var currentOsId);

            CorDebugThread? clrThread = null;
            foreach (var thread in model.CorProcess.Threads)
            {
                try
                {
                    // ICorDebugThread::GetID returns the OS thread ID in .NET Core.
                    if ((uint)thread.Id == currentOsId)
                    {
                        clrThread = thread;
                        break;
                    }
                }
                catch { }
            }

            if (clrThread == null)
            {
                _log.LogInfo(_logStore, $"No CLR thread found for OS thread {currentOsId}");
                return [];
            }

            var frames = new List<StackFrame>();
            int frameId = 1;

            using var pdbMapper = new PdbSourceMapper();

            foreach (var chain in clrThread.Chains)
            {
                if (!chain.IsManaged)
                    continue;

                foreach (var frame in chain.Frames)
                {
                    try
                    {
                        var function = frame.Function;
                        var module = function.Module;
                        var token = (int)function.Token;
                        var modulePath = module.Name;

                        // Get IL offset for source resolution.
                        int ilOffset = 0;
                        try
                        {
                            if (frame is CorDebugILFrame ilFrame)
                            {
                                var ip = ilFrame.IP;
                                ilOffset = (int)ip.pnOffset;
                            }
                        }
                        catch { }

                        // Resolve source location via PDB.
                        Source? source = null;
                        int line = 0;

                        if (modulePath != null)
                        {
                            var srcLoc = pdbMapper.GetSourceLocation(modulePath, token, ilOffset > 0 ? ilOffset : 1);
                            if (srcLoc != null)
                            {
                                source = new Source
                                {
                                    Name = Path.GetFileName(srcLoc.Value.File),
                                    Path = srcLoc.Value.File,
                                };
                                line = srcLoc.Value.Line;
                            }
                        }

                        // Build frame name from metadata.
                        var name = GetFrameName(function);

                        frames.Add(new StackFrame
                        {
                            Id = frameId++,
                            Name = name,
                            Source = source,
                            Line = line,
                            Column = 0,
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.LogInfo(_logStore, $"  Frame enumeration error: {ex.Message}");
                    }
                }
            }

            _log.LogInfo(_logStore, $"GetManagedStackFrames: {frames.Count} managed frames");
            return frames.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogError(_logStore, $"GetManagedStackFrames failed: {ex.Message}");
            return [];
        }
    }

    public Breakpoint[] OnModuleLoad(NativeDebuggerModel model)
    {
        if (!model.ManagedInitialized || model.CorProcess == null)
            return [];

        // Re-enumerate ICorDebug modules to pick up newly loaded assemblies.
        EnumerateModules(model);

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
            foreach (var loaded in model.CorModules.Values)
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
                int cliHr = model.Symbols.GetOffsetByLine((uint)line, filePath, out var ilAddr);
                if (cliHr >= 0 && ilAddr != 0)
                {
                    int modHr = model.Symbols.GetModuleByOffset(ilAddr, 0, out _, out var moduleBase);
                    if (modHr >= 0 && moduleBase != 0)
                    {
                        int rva = (int)(ilAddr - moduleBase);
                        assemblyName = ResolveCliAssemblyName(filePath);
                        var dllPath = FindCliAssemblyDll(filePath, assemblyName);
                        _log.LogInfo(_logStore, $"  C++/CLI: ilAddr=0x{ilAddr:X} base=0x{moduleBase:X} rva=0x{rva:X} asm={assemblyName} dll={dllPath}");
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
                        _log.LogWarning(_logStore, $"  C++/CLI: GetModuleByOffset failed: hr=0x{modHr:X8}");
                    }
                }
                else
                {
                    _log.LogInfo(_logStore, $"  C++/CLI: GetOffsetByLine({line}) failed: hr=0x{cliHr:X8}");
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
        int hr = isCliMethod ? -1 : model.Symbols.GetOffsetByLine((uint)line, filePath, out offset);
        if (hr >= 0 && offset != 0)
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
    /// Gets the native code address corresponding to an IL offset using the
    /// IL-to-native mapping from JIT-compiled code.
    /// </summary>
    private ulong GetNativeAddressForILOffset(CorDebugCode nativeCode, int ilOffset)
    {
        var mapping = nativeCode.ILToNativeMapping;
        if (mapping != null && mapping.Length > 0)
        {
            foreach (var entry in mapping)
            {
                if (entry.ilOffset == ilOffset)
                    return nativeCode.Address + (ulong)entry.nativeStartOffset;
            }
        }
        // Fallback: use the code base address.
        return nativeCode.Address;
    }

    /// <summary>
    /// Sets a hardware execution breakpoint (<c>ba e1</c>) at the given native address.
    /// Uses CPU debug registers — no code patching, so safe for managed code.
    /// Returns the dbgeng breakpoint ID, or <c>null</c> on failure.
    /// </summary>
    private uint? SetManagedCodeBreakpoint(NativeDebuggerModel model, ulong address, string filePath, int line)
    {
        int hr = model.Control.AddBreakpoint(
            Engine.DbgEng.DebugBreakpointType.Data,
            0xFFFFFFFF, // DEBUG_ANY_ID
            out var bp);
        if (hr < 0)
        {
            _log.LogWarning(_logStore, $"  AddBreakpoint(Data) failed: hr=0x{hr:X8}");
            return null;
        }

        hr = bp.SetDataParameters(1, Engine.DbgEng.DebugBreakAccess.Execute);
        if (hr < 0)
        {
            _log.LogWarning(_logStore, $"  SetDataParameters failed: hr=0x{hr:X8}");
            model.Control.RemoveBreakpoint(bp);
            return null;
        }

        bp.SetOffset(address);
        bp.AddFlags(Engine.DbgEng.DebugBreakpointFlag.Enabled);
        bp.GetId(out var bpId);

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
        try { TryInitializeDac(model); }
        catch { }

        var resolved = new List<Breakpoint>();
        var bound = new List<DeferredManagedBreakpoint>();

        foreach (var deferred in model.DeferredManagedBreakpoints)
        {
            try
            {
                // Use the DAC (XCLRDataProcess) to find the real JIT native entry point.
                var nativeAddress = ResolveViaXclrData(model, deferred.MethodToken, deferred.AssemblyName);
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

        // Find the assembly path from CorModules (match by assembly name).
        string? assemblyPath = null;
        foreach (var mod in model.CorModules.Values)
        {
            if (mod.Path != null &&
                Path.GetFileNameWithoutExtension(mod.Path)
                    .Equals(method.AssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                assemblyPath = mod.Path;
                break;
            }
        }

        // Fallback: search near known module paths if CorModules didn't have it.
        if (assemblyPath == null)
        {
            foreach (var mod in model.CorModules.Values)
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
                    int hr = model.Control.GetBreakpointById(hwId, out var hwBp);
                    if (hr >= 0)
                        model.Control.RemoveBreakpoint(hwBp);
                    model.UserBreakpointIds.Remove(hwId);
                    model.ManagedBreakpointIds.Remove(hwId);
                    model.BreakpointIds.Remove(key.Key);
                }

                // Also deactivate any ICorDebug breakpoints (legacy path).
                if (model.CorManagedBreakpoints.TryGetValue(id, out var corBp))
                {
                    try { corBp.Activate(false); } catch { }
                    model.CorManagedBreakpoints.Remove(id);
                }
            }
            existingIds.Clear();
        }

        model.PendingILBreakpoints.RemoveAll(p =>
            p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        model.DeferredManagedBreakpoints.RemoveAll(d =>
            d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
    }

    private void EnumerateModules(NativeDebuggerModel model)
    {
        if (model.CorProcess == null)
            return;

        try
        {
            // Notify ICorDebug that the process is stopped so the DAC refreshes
            // its view of the runtime's in-memory data structures.
            try
            {
                model.CorProcess.ProcessStateChanged(CorDebugStateChange.FLUSH_ALL);
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"  ProcessStateChanged failed: {ex.Message}");
            }

            var appDomains = model.CorProcess.AppDomains;
            _log.LogInfo(_logStore, $"  EnumerateModules: {appDomains.Length} app domains");

            foreach (var appDomain in appDomains)
            {
                _log.LogInfo(_logStore, $"  AppDomain: {appDomain.Name}");
                var assemblies = appDomain.Assemblies;
                _log.LogInfo(_logStore, $"    {assemblies.Length} assemblies");

                foreach (var assembly in assemblies)
                {
                    foreach (var module in assembly.Modules)
                    {
                        var baseAddr = (long)module.BaseAddress;
                        var isNew = !model.CorModules.ContainsKey(baseAddr);

                        // Always update: after FLUSH_ALL old CorDebugModule objects
                        // are neutered, so we must store the fresh reference.
                        var name = module.Name;
                        var pdbPath = name != null ? Path.ChangeExtension(name, ".pdb") : null;
                        model.CorModules[baseAddr] = new ManagedModule
                        {
                            Module = module,
                            Path = name,
                            PdbPath = pdbPath != null && File.Exists(pdbPath) ? pdbPath : null,
                        };
                        if (isNew)
                            _log.LogInfo(_logStore, $"  ICorDebug module: {name} (pdb={pdbPath != null && File.Exists(pdbPath)})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"  Module enumeration error: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes the DAC (<c>SOSDacInterface</c>) by calling <c>CLRDataCreateInstance</c>
    /// from mscordaccore.dll. The DAC provides <c>GetMethodDescPtrFromIP</c> and
    /// <c>GetMethodDescData</c> for finding real JIT native code addresses.
    /// </summary>
    private void TryInitializeDac(NativeDebuggerModel model)
    {
        try
        {
            var runtimeDir = Path.GetDirectoryName(model.CoreClrPath)!;
            var dacPath = Path.Combine(runtimeDir, "mscordaccore.dll");

            // Load mscordaccore.dll once, reuse the handle.
            if (_dacHandle == IntPtr.Zero)
            {
                _log.LogInfo(_logStore, "DAC: Loading mscordaccore.dll...");
                _dacHandle = System.Runtime.InteropServices.NativeLibrary.Load(dacPath);
            }

            var clrDataTarget = new DbgEngClrDataTarget(
                model.DataSpaces, model.Advanced, model.SysObjects);
            clrDataTarget.AddModuleBase(model.CoreClrPath!, model.CoreClrBaseAddress);

            var interfaces = ClrDebug.Extensions.CLRDataCreateInstance(_dacHandle, clrDataTarget);
            model.SosDac = interfaces.SOSDacInterface;
            model.XclrProcess = interfaces.XCLRDataProcess;
            _log.LogInfo(_logStore, "DAC: XCLRDataProcess refreshed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(_logStore, $"DAC initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Uses the DAC's XCLRData interfaces to find the real JIT native code entry point
    /// for a method token. Enumerates modules to find the matching one, then gets the
    /// method definition by token and checks if it has a JIT'd instance.
    /// Returns 0 if the method is not yet JIT'd or the lookup fails.
    /// </summary>
    private ulong ResolveViaXclrData(NativeDebuggerModel model, int methodToken, string? assemblyName)
    {
        _log.LogInfo(_logStore, $"  ResolveViaXclrData: token=0x{methodToken:X8} assembly={assemblyName} xclrProcess={model.XclrProcess != null}");

        if (model.XclrProcess == null)
            return 0;

        try
        {
            // Enumerate XCLR modules to find the one containing our method.
            _log.LogInfo(_logStore, "  XCLRData: StartEnumModules...");
            var enumHandle = model.XclrProcess.StartEnumModules();
            int moduleCount = 0;
            try
            {
                while (true)
                {
                    var moduleResult = model.XclrProcess.TryEnumModule(ref enumHandle, out var xModule);
                    if (moduleResult != ClrDebug.HRESULT.S_OK)
                        break;
                    moduleCount++;

                    try
                    {
                        // Filter by assembly name to avoid token collisions.
                        if (assemblyName != null)
                        {
                            string moduleName = "";
                            try { moduleName = xModule.Name; } catch { }
                            if (!moduleName.Contains(assemblyName, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        var methodDef = xModule.GetMethodDefinitionByToken((ClrDebug.mdMethodDef)methodToken);

                        // Check if this method has a JIT'd instance.
                        var startResult = methodDef.TryStartEnumInstances(null!, out var instHandle);
                        if (startResult != ClrDebug.HRESULT.S_OK)
                        {
                            _log.LogInfo(_logStore, $"  XCLRData: StartEnumInstances failed: {startResult}");
                            continue;
                        }
                        try
                        {
                            var instResult = methodDef.TryEnumInstance(ref instHandle, out var methodInst);
                            if (instResult == ClrDebug.HRESULT.S_OK)
                            {
                                var entryResult = methodInst.TryGetRepresentativeEntryAddress(out var entryAddr);
                                _log.LogInfo(_logStore, $"  XCLRData: EntryAddress result={entryResult} addr=0x{(ulong)entryAddr:X}");
                                if (entryResult == ClrDebug.HRESULT.S_OK && (ulong)entryAddr != 0)
                                {
                                    return (ulong)entryAddr;
                                }
                            }
                        }
                        finally
                        {
                            methodDef.EndEnumInstances(instHandle);
                        }
                    }
                    catch
                    {
                        // Token not found in this module — try next.
                    }
                }
                _log.LogInfo(_logStore, $"  XCLRData: enumerated {moduleCount} modules, method not found/not JIT'd");
            }
            finally
            {
                model.XclrProcess.EndEnumModules(enumHandle);
            }
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"  XCLRData lookup failed for token 0x{methodToken:X8}: {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Given an address within a managed method (from <c>GetOffsetByLine</c>), uses the
    /// DAC to find the real native code entry point via <c>GetMethodDescPtrFromIP</c> +
    /// <c>GetMethodDescData</c>. Falls back to the original address if the DAC is not
    /// available or the lookup fails.
    /// </summary>
    private ulong ResolveNativeEntryPoint(NativeDebuggerModel model, ulong symbolAddress)
    {
        if (model.SosDac == null)
            return symbolAddress;

        try
        {
            var mdResult = model.SosDac.TryGetMethodDescPtrFromIP((ClrDebug.CLRDATA_ADDRESS)symbolAddress, out var methodDesc);
            if (mdResult != ClrDebug.HRESULT.S_OK)
            {
                _log.LogInfo(_logStore, $"  GetMethodDescPtrFromIP(0x{symbolAddress:X}) failed: {mdResult}");
                return symbolAddress;
            }

            var dataResult = model.SosDac.TryGetMethodDescData(methodDesc, 0, out var data);
            if (dataResult != ClrDebug.HRESULT.S_OK)
            {
                _log.LogInfo(_logStore, $"  GetMethodDescData(0x{(ulong)methodDesc:X}) failed: {dataResult}");
                return symbolAddress;
            }

            var entryPoint = (ulong)data.data.NativeCodeAddr;
            if (entryPoint != 0 && data.data.bHasNativeCode)
            {
                _log.LogInfo(_logStore, $"  DAC: MethodDesc=0x{(ulong)methodDesc:X} NativeCodeAddr=0x{entryPoint:X} (symbol was 0x{symbolAddress:X})");
                return entryPoint;
            }

            return symbolAddress;
        }
        catch (Exception ex)
        {
            _log.LogInfo(_logStore, $"  DAC lookup failed: {ex.Message}");
            return symbolAddress;
        }
    }

    private static string GetFrameName(CorDebugFunction function)
    {
        try
        {
            var module = function.Module;
            var metaData = module.GetMetaDataInterface<MetaDataImport>();
            var methodProps = metaData.GetMethodProps(function.Token);
            var typeName = metaData.GetTypeDefProps(methodProps.pClass).szTypeDef;
            return $"{typeName}.{methodProps.szMethod}";
        }
        catch
        {
            return $"<frame token=0x{function.Token:X8}>";
        }
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

    /// <summary>
    /// Calls <c>mscordbi!OpenVirtualProcessImpl</c> directly to create a piggybacked
    /// <c>ICorDebugProcess</c>. This bypasses <c>ICLRDebugging</c> entirely because
    /// .NET 10's <c>mscordbi.dll</c> doesn't export <c>DllGetClassObject</c>, and
    /// <c>mscoree.dll</c>'s <c>CLRCreateInstance</c> returns
    /// <c>CORDBG_E_UNSUPPORTED_FORWARD_COMPAT</c> for modern runtimes.
    /// </summary>
    private static unsafe CorDebugProcess OpenVirtualProcessImpl(
        string coreClrPath, ulong coreClrBase, DbgEngDataTarget dataTarget,
        ILoggingService log, LogStore logStore, out CLR_DEBUGGING_VERSION clrVersion)
    {
        clrVersion = default;
        var runtimeDir = Path.GetDirectoryName(coreClrPath)!;
        var mscordbiPath = Path.Combine(runtimeDir, "mscordbi.dll");
        var mscordacPath = Path.Combine(runtimeDir, "mscordaccore.dll");

        var hMscordbi = NativeLibrary.Load(mscordbiPath);
        var hDac = NativeLibrary.Load(mscordacPath);
        var pFunc = NativeLibrary.GetExport(hMscordbi, "OpenVirtualProcessImpl");

        // StrategyBasedComWrappers is required for both creating CCWs from our
        // [GeneratedComClass] objects AND wrapping native COM pointers for
        // ClrDebug's [GeneratedComInterface] types.
        var comWrappers = new StrategyBasedComWrappers();

        IntPtr pDataTarget = comWrappers.GetOrCreateComInterfaceForObject(
            dataTarget, CreateComInterfaceFlags.None);
        try
        {
            var maxVersion = new CLR_DEBUGGING_VERSION
            {
                wStructVersion = 0,
                wMajor = 255, wMinor = 255, wBuild = 255, wRevision = 255,
            };
            var riid = typeof(ICorDebugProcess).GUID;
            var version = new CLR_DEBUGGING_VERSION();
            int flags = 0;
            IntPtr ppInstance = IntPtr.Zero;

            // OpenVirtualProcessImpl(clrInstanceId, pDataTarget, hDacModule,
            //   pMaxVersion, riidProcess, ppInstance, pVersion, pdwFlags)
            var fn = (delegate* unmanaged[Stdcall]<
                ulong, IntPtr, IntPtr,
                CLR_DEBUGGING_VERSION*, Guid*,
                IntPtr*, CLR_DEBUGGING_VERSION*, int*, int>)pFunc;

            int hr = fn(
                coreClrBase, pDataTarget, hDac,
                &maxVersion, &riid,
                &ppInstance, &version, &flags);

            log.LogInfo(logStore, $"OpenVirtualProcessImpl: hr=0x{hr:X8} flags=0x{flags:X} version={version.wMajor}.{version.wMinor}.{version.wBuild}");

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            // Use StrategyBasedComWrappers to create a proper wrapper compatible
            // with ClrDebug's [GeneratedComInterface] types. Marshal.GetObjectForIUnknown
            // creates a legacy RCW that can't QI for source-generated COM interfaces.
            var raw = (ICorDebugProcess)comWrappers.GetOrCreateObjectForComInstance(
                ppInstance, CreateObjectFlags.None);
            Marshal.Release(ppInstance);
            clrVersion = version;
            return new CorDebugProcess(raw);
        }
        finally
        {
            Marshal.Release(pDataTarget);
        }
    }
}
