using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;
using MixDbg.Dap;
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
    LogStore logStore) : IManagedDebugger
{
    private readonly ILoggingService _log = log;
    private readonly LogStore _logStore = logStore;

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
        int methodToken = 0;
        int ilOffset = 0;
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
                    _log.LogInfo(_logStore, $"  Resolved {filePath}:{line} -> token=0x{methodToken:X8} IL={ilOffset} in {loaded.Path}");
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
                _log.LogInfo(_logStore, $"  Found method via disk PDB: {diskResult.Value.MethodName} in {diskResult.Value.AssemblyName} — but module not in ICorDebug yet");
                foundInPdb = true;
            }
        }

        if (!foundInPdb)
            return false;

        // Try direct resolution via GetOffsetByLine (works if method is already JIT'd).
        int hr = model.Symbols.GetOffsetByLine((uint)line, filePath, out var offset);
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
            new DeferredManagedBreakpoint(filePath, line, methodToken, ilOffset, bpId));
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

        _log.LogInfo(_logStore, $"  Hardware bp #{bpId} set at 0x{address:X} for {key}");
        return bpId;
    }

    public Breakpoint[] TryResolveDeferredBreakpoints(NativeDebuggerModel model)
    {
        if (model.DeferredManagedBreakpoints.Count == 0)
            return [];

        _log.LogInfo(_logStore, $"TryResolveDeferredBreakpoints: {model.DeferredManagedBreakpoints.Count} deferred");

        var resolved = new List<Breakpoint>();
        var bound = new List<DeferredManagedBreakpoint>();

        foreach (var deferred in model.DeferredManagedBreakpoints)
        {
            try
            {
                // Use dbgeng's GetOffsetByLine — works after JIT publishes the symbol.
                int hr = model.Symbols.GetOffsetByLine(
                    (uint)deferred.Line, deferred.FilePath, out var nativeAddress);

                if (hr < 0 || nativeAddress == 0)
                    continue;

                _log.LogInfo(_logStore, $"  Deferred bp #{deferred.BpId}: resolved to 0x{nativeAddress:X}");

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
    private (string AssemblyName, string MethodName, int ILOffset)? FindMethodFromDiskPdb(
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
                    return (result.Value.AssemblyName, result.Value.MethodName, result.Value.ILOffset);
            }
        }

        return null;
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
