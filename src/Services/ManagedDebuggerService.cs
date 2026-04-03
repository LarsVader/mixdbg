using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using MixDbg.Dap;
using MixDbg.Engine.DbgEng;
using MixDbg.Engine.Sos;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless managed debugging service. Uses ClrMD for runtime inspection
/// (stack traces, method resolution) and hardware execution breakpoints
/// (<c>ba e1</c>) for managed breakpoints. All methods execute on the engine thread.
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

        try
        {
            // Create ClrMD DataTarget from existing dbgeng IDebugClient.
            var pClient = Marshal.GetIUnknownForObject(model.Client);
            try
            {
#pragma warning disable CS0618 // CreateFromDbgEng is obsolete but works; DbgEngDataReader requires a separate NuGet
                model.DataTarget = DataTarget.CreateFromDbgEng(pClient);
#pragma warning restore CS0618
            }
            finally
            {
                Marshal.Release(pClient);
            }

            var clrVersions = model.DataTarget.ClrVersions;
            if (clrVersions.Length == 0)
            {
                _log.LogWarning(_logStore, "No CLR versions found in target process");
                return false;
            }

            model.Runtime = clrVersions[0].CreateRuntime();
            _log.LogInfo(_logStore, $"ClrMD runtime created: {clrVersions[0].Version}");

            // Note: SOS is NOT loaded — SOS 9.0 is incompatible with .NET 10
            // (causes SOS_HOSTING failure and WaitForEvent crashes).
            // Managed breakpoints use ClrMD native code address polling instead.

            model.OriginalRuntime = model.Runtime;
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

        var results = new Breakpoint[requested.Length];

        for (int i = 0; i < requested.Length; i++)
        {
            var req = requested[i];
            results[i] = SetOneManagedBreakpoint(model, filePath, req);
        }

        return results;
    }

    public StackFrame[] GetManagedStackFrames(NativeDebuggerModel model)
    {
        var runtime = model.OriginalRuntime ?? model.Runtime;
        if (runtime == null)
            return [];

        try
        {
            runtime.FlushCachedData();

            // Find the CLR thread matching the current dbgeng event thread.
            model.SysObjects.GetEventThread(out var osThreadId);
            model.SysObjects.GetThreadIdsByIndex(0, 1, null!, new uint[1]);

            // GetEventThread returns the engine thread ID; we need the OS thread ID.
            // Save current thread, get OS ID.
            model.SysObjects.GetCurrentThreadSystemId(out var currentOsId);

            var clrThread = runtime.Threads
                .FirstOrDefault(t => t.OSThreadId == currentOsId);

            if (clrThread == null)
            {
                _log.LogInfo(_logStore, $"No CLR thread found for OS thread {currentOsId}");
                return [];
            }

            var frames = new List<StackFrame>();
            int frameId = 1;

            using var pdbMapper = new PdbSourceMapper();

            foreach (var frame in clrThread.EnumerateStackTrace(includeContext: false, maxFrames: 100))
            {
                if (frame.Kind != ClrStackFrameKind.ManagedMethod || frame.Method == null)
                    continue;

                var method = frame.Method;
                var name = method.Signature ?? $"{method.Type?.Name}.{method.Name}";

                Source? source = null;
                int line = 0;

                // Try to resolve source location.
                var srcLoc = ResolveSourceLocation(model, frame, pdbMapper);
                if (srcLoc != null)
                {
                    source = new Source
                    {
                        Name = Path.GetFileName(srcLoc.Value.File),
                        Path = srcLoc.Value.File,
                    };
                    line = srcLoc.Value.Line;
                    _log.LogInfo(_logStore, $"  Managed frame: {name} -> {srcLoc.Value.File}:{srcLoc.Value.Line}");
                }

                frames.Add(new StackFrame
                {
                    Id = frameId++,
                    Name = name,
                    Source = source,
                    Line = line,
                    Column = 0,
                });
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

    // ── Private ─────────────────────────────────────────

    public Breakpoint[] TryResolveDeferredBreakpoints(NativeDebuggerModel model)
    {
        if (model.DataTarget == null || model.DeferredManagedBreakpoints.Count == 0)
            return [];

        // Create a fresh runtime from the cached ResolutionDataTarget to see newly
        // JIT'd methods. The stack trace cache in NativeDebuggerService prevents
        // repeated GetStackTrace/symbol lookup calls from degrading the DAC.
        ClrRuntime resolutionRuntime;
        try
        {
            if (model.ResolutionDataTarget == null)
            {
                var pClient = Marshal.GetIUnknownForObject(model.Client);
                try
                {
#pragma warning disable CS0618
                    model.ResolutionDataTarget = DataTarget.CreateFromDbgEng(pClient);
#pragma warning restore CS0618
                }
                finally
                {
                    Marshal.Release(pClient);
                }
            }
            resolutionRuntime = model.ResolutionDataTarget.ClrVersions[0].CreateRuntime();
        }
        catch (Exception ex)
        {
            _log.LogWarning(_logStore, $"Resolution runtime creation failed: {ex.Message}");
            model.DeferredResolutionFailures++;
            return [];
        }

        var resolved = new List<Breakpoint>();
        var toRemove = new List<DeferredManagedBreakpoint>();

        foreach (var deferred in model.DeferredManagedBreakpoints)
        {
            var addr = FindNativeCodeAddress(resolutionRuntime, deferred.AssemblyName, deferred.MethodName, deferred.ILOffset);
            if (addr == 0)
                continue;

            _log.LogInfo(_logStore, $"  Deferred bp resolved: {deferred.MethodName} -> 0x{addr:X}");

            var bpId = SetHardwareBreakpoint(model, addr, deferred.FilePath, deferred.Line);
            if (bpId != null)
            {
                resolved.Add(new Breakpoint
                {
                    Id = (int)bpId.Value,
                    Verified = true,
                    Line = deferred.Line,
                    Source = new Source
                    {
                        Name = Path.GetFileName(deferred.FilePath),
                        Path = deferred.FilePath,
                    },
                });
                toRemove.Add(deferred);
            }
        }

        foreach (var r in toRemove)
            model.DeferredManagedBreakpoints.Remove(r);

        return resolved.ToArray();
    }

    private Breakpoint SetOneManagedBreakpoint(
        NativeDebuggerModel model, string filePath, SourceBreakpoint req)
    {
        string? assemblyName = null;
        string? methodName = null;
        int ilOffset = 0;

        // Try 1: Search loaded CLR modules (works when assemblies are already loaded).
        var found = FindMethodInModules(model, filePath, req.Line);
        if (found != null)
        {
            assemblyName = found.Value.AssemblyName;
            methodName = found.Value.MethodName;
            ilOffset = found.Value.ILOffset;
        }

        // Try 2: Search for PDB on disk.
        if (methodName == null)
        {
            var diskResult = FindMethodFromDiskPdb(model, filePath, req.Line);
            if (diskResult != null)
            {
                assemblyName = diskResult.Value.AssemblyName;
                methodName = diskResult.Value.MethodName;
                ilOffset = diskResult.Value.ILOffset;
            }
        }

        if (methodName == null || assemblyName == null)
        {
            _log.LogWarning(_logStore, $"  Could not resolve {filePath}:{req.Line} to a managed method");
            return new Breakpoint
            {
                Id = ++model.NextBpId,
                Verified = false,
                Line = req.Line,
                Message = "Could not resolve source line to a managed method",
            };
        }

        _log.LogInfo(_logStore, $"  Resolved managed method: {methodName} in {assemblyName} (IL offset={ilOffset})");

        // Try to find the JIT-compiled native code address via ClrMD.
        var nativeAddr = FindNativeCodeAddress(model, assemblyName, methodName, ilOffset);

        if (nativeAddr != 0)
        {
            // Method is JIT-compiled — set a hardware execution breakpoint.
            _log.LogInfo(_logStore, $"  NativeAddr=0x{nativeAddr:X} — setting hardware breakpoint");
            var bpId = SetHardwareBreakpoint(model, nativeAddr, filePath, req.Line);
            if (bpId != null)
            {
                return new Breakpoint
                {
                    Id = (int)bpId.Value,
                    Verified = true,
                    Line = req.Line,
                    Source = new Source
                    {
                        Name = Path.GetFileName(filePath),
                        Path = filePath,
                    },
                };
            }

            // Hardware breakpoint failed — likely all 4 debug registers in use.
            _log.LogWarning(_logStore, $"  Hardware breakpoint failed at 0x{nativeAddr:X}");
            return new Breakpoint
            {
                Id = ++model.NextBpId,
                Verified = false,
                Line = req.Line,
                Source = new Source
                {
                    Name = Path.GetFileName(filePath),
                    Path = filePath,
                },
                Message = "Hardware breakpoint limit reached (max 4 concurrent managed breakpoints)",
            };
        }

        // Method not JIT-compiled yet — defer until interrupt detects JIT.
        var deferredBpId = ++model.NextBpId;
        model.DeferredManagedBreakpoints.Add(new DeferredManagedBreakpoint(
            filePath, req.Line, assemblyName, methodName, ilOffset, deferredBpId));
        _log.LogInfo(_logStore, $"  NativeCode=0 — deferred bp #{deferredBpId} for {methodName} (IL={ilOffset})");

        return new Breakpoint
        {
            Id = deferredBpId,
            Verified = true,
            Line = req.Line,
            Source = new Source
            {
                Name = Path.GetFileName(filePath),
                Path = filePath,
            },
            Message = "Deferred — method not yet JIT-compiled",
        };
    }

    /// <summary>
    /// Searches the ClrMD runtime for the native address corresponding to a specific
    /// IL offset within a method. Uses <c>ILOffsetMap</c> for line-precise breakpoints.
    /// Returns 0 if the method hasn't been JIT-compiled yet.
    /// </summary>
    private ulong FindNativeCodeAddress(NativeDebuggerModel model, string assemblyName, string methodName, int targetILOffset = 0)
        => FindNativeCodeAddress(model.Runtime, assemblyName, methodName, targetILOffset);

    private ulong FindNativeCodeAddress(ClrRuntime? runtime, string assemblyName, string methodName, int targetILOffset = 0)
    {
        if (runtime == null)
            return 0;

        // Parse "Namespace.TypeName.MethodName" into type and method parts.
        var lastDot = methodName.LastIndexOf('.');
        if (lastDot < 0) return 0;

        var fullTypeName = methodName[..lastDot];
        var methName = methodName[(lastDot + 1)..];

        foreach (var module in runtime.EnumerateModules())
        {
            var modName = Path.GetFileNameWithoutExtension(module.Name ?? "");
            if (!modName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var type = module.GetTypeByName(fullTypeName);
                if (type == null) continue;

                foreach (var method in type.Methods)
                {
                    if (method.Name != methName)
                        continue;

                    var nativeCode = method.NativeCode;
                    // NativeCode is 0 or ulong.MaxValue when the method hasn't been JIT-compiled.
                    if (nativeCode == 0 || nativeCode == ulong.MaxValue)
                    {
                        _log.LogInfo(_logStore, $"  ClrMD: {fullTypeName}.{methName} found but NativeCode=0x{nativeCode:X} (not JIT'd)");
                        return 0;
                    }

                    // Use ILOffsetMap to find the native address for the exact source line.
                    var ilMap = method.ILOffsetMap;
                    if (ilMap.Length > 0 && targetILOffset > 0)
                    {
                        // Find the map entry matching the target IL offset.
                        // Entries have StartAddress (absolute native addr) and ILOffset.
                        ulong bestAddr = nativeCode;
                        int bestIL = -1;
                        foreach (var entry in ilMap)
                        {
                            if (entry.ILOffset >= 0 && entry.ILOffset <= targetILOffset
                                && entry.ILOffset > bestIL
                                && entry.StartAddress != 0)
                            {
                                bestIL = entry.ILOffset;
                                bestAddr = entry.StartAddress;
                            }
                        }
                        _log.LogInfo(_logStore, $"  ClrMD: {fullTypeName}.{methName} IL={targetILOffset} -> native=0x{bestAddr:X} (matched IL={bestIL}, map has {ilMap.Length} entries)");
                        return bestAddr;
                    }

                    _log.LogInfo(_logStore, $"  ClrMD: {fullTypeName}.{methName} -> NativeCode=0x{nativeCode:X} (entry point, no IL map)");
                    return nativeCode;
                }

                // Method not found in type.
                _log.LogInfo(_logStore, $"  ClrMD: {fullTypeName}.{methName} not found in type");
                return 0;
            }
            catch (Exception ex)
            {
                _log.LogInfo(_logStore, $"  ClrMD lookup error for {fullTypeName}: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Sets a hardware execution breakpoint (<c>ba e1</c>) at the given native address.
    /// Uses CPU debug registers — no code page modification, bypasses JIT page protections.
    /// Returns the dbgeng breakpoint ID, or <c>null</c> on failure.
    /// </summary>
    private uint? SetHardwareBreakpoint(
        NativeDebuggerModel model, ulong address, string filePath, int line)
    {
        // Create a data/processor breakpoint (DEBUG_BREAKPOINT_DATA).
        int hr = model.Control.AddBreakpoint(
            DebugBreakpointType.Data,
            0xFFFFFFFF, // DEBUG_ANY_ID
            out var bp);
        if (hr < 0)
        {
            _log.LogError(_logStore, $"  AddBreakpoint(Data) failed: hr=0x{hr:X8}");
            return null;
        }

        // Set the address to break at.
        hr = bp.SetOffset(address);
        if (hr < 0)
        {
            _log.LogError(_logStore, $"  SetOffset(0x{address:X}) failed: hr=0x{hr:X8}");
            model.Control.RemoveBreakpoint(bp);
            return null;
        }

        // Configure as execution breakpoint: size=1, access=Execute.
        // This is the API equivalent of "ba e1 <address>".
        hr = bp.SetDataParameters(1, DebugBreakAccess.Execute);
        if (hr < 0)
        {
            _log.LogError(_logStore, $"  SetDataParameters(1, Execute) failed: hr=0x{hr:X8}");
            model.Control.RemoveBreakpoint(bp);
            return null;
        }

        hr = bp.AddFlags(DebugBreakpointFlag.Enabled);
        if (hr < 0)
        {
            _log.LogError(_logStore, $"  AddFlags(Enabled) failed: hr=0x{hr:X8}");
            model.Control.RemoveBreakpoint(bp);
            return null;
        }

        bp.GetId(out var bpId);

        // Track as a managed breakpoint.
        var key = $"{filePath}:{line}";
        model.ManagedBreakpointIds.Add(bpId);
        model.BreakpointIds[key] = bpId;

        _log.LogInfo(_logStore, $"  Hardware breakpoint set: id={bpId} addr=0x{address:X} ({key})");
        return bpId;
    }

    private (string AssemblyPath, string AssemblyName, string MethodName, int ILOffset)? FindMethodInModules(
        NativeDebuggerModel model, string sourceFile, int line)
    {
        if (model.Runtime == null)
            return null;

        var modules = model.Runtime.EnumerateModules().ToList();
        _log.LogInfo(_logStore, $"  ClrMD reports {modules.Count} modules");

        foreach (var module in modules)
        {
            var assemblyPath = module.Name;
            if (string.IsNullOrEmpty(assemblyPath))
            {
                _log.LogInfo(_logStore, $"  Skipping module with empty name (addr=0x{module.Address:X})");
                continue;
            }

            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdbPath))
            {
                // Only log user assemblies, not framework ones
                if (!assemblyPath.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
                    && !assemblyPath.Contains("Microsoft.NETCore", StringComparison.OrdinalIgnoreCase))
                    _log.LogInfo(_logStore, $"  Module {assemblyPath} — no PDB at {pdbPath}");
                continue;
            }

            _log.LogInfo(_logStore, $"  Checking module: {assemblyPath} (PDB exists at {pdbPath})");

            using var mapper = new PdbSourceMapper();
            var result = mapper.FindMethodAtLine(assemblyPath, sourceFile, line);
            if (result != null)
            {
                _log.LogInfo(_logStore, $"  Found method: {result.Value.MethodName} in {result.Value.AssemblyName}");
                return (assemblyPath, result.Value.AssemblyName, result.Value.MethodName, result.Value.ILOffset);
            }

            if (mapper.LastError != null)
                _log.LogInfo(_logStore, $"  PDB error: {mapper.LastError}");
        }

        _log.LogInfo(_logStore, $"  No module found containing {sourceFile}:{line}");
        return null;
    }

    /// <summary>
    /// Finds the method by searching for PDB files on disk near the source file's
    /// project. Walks up from the source directory looking for a .csproj, then
    /// scans bin/ subdirectories for a matching PDB.
    /// </summary>
    private (string AssemblyName, string MethodName, int ILOffset)? FindMethodFromDiskPdb(
        NativeDebuggerModel model, string sourceFile, int line)
    {
        // Walk up from the source file to find a .csproj
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
        {
            _log.LogInfo(_logStore, $"  No .csproj found near {sourceFile}");
            return null;
        }

        _log.LogInfo(_logStore, $"  Found project: {projectName} at {projectDir}");

        // Search bin/ and obj/ for the PDB.
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

                _log.LogInfo(_logStore, $"  Trying disk PDB: {pdbFile}");

                using var mapper = new PdbSourceMapper();
                var result = mapper.FindMethodAtLine(assemblyPath, sourceFile, line);
                if (result != null)
                {
                    _log.LogInfo(_logStore, $"  Resolved via disk PDB: {result.Value.MethodName} in {result.Value.AssemblyName}");
                    return (result.Value.AssemblyName, result.Value.MethodName, result.Value.ILOffset);
                }

                if (mapper.LastError != null)
                    _log.LogInfo(_logStore, $"  PDB error: {mapper.LastError}");
            }
        }

        _log.LogInfo(_logStore, $"  No PDB found on disk for project {projectName}");
        return null;
    }

    private (string File, int Line)? ResolveSourceLocation(
        NativeDebuggerModel model, ClrStackFrame frame, PdbSourceMapper pdbMapper)
    {
        if (frame.Method == null)
            return null;

        // First try dbgeng's native symbol resolution (works for C++/CLI Windows PDBs).
        IntPtr fileBuf = Marshal.AllocHGlobal(512);
        try
        {
            int hr = model.Symbols.GetLineByOffset(
                frame.InstructionPointer, out var srcLine, fileBuf, 512, out _, out _);
            if (hr >= 0)
            {
                var path = Marshal.PtrToStringAnsi(fileBuf) ?? "";
                if (!string.IsNullOrEmpty(path))
                    return (path, (int)srcLine);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuf);
        }

        // Fall back to portable PDB reading for C# code.
        var module = frame.Method.Type?.Module;
        if (module?.Name == null)
            return null;

        var ilOffset = frame.Method.GetILOffset(frame.InstructionPointer);
        // IL offset 0 maps to the opening brace '{' in the PDB. Use offset 1 to
        // land on the first executable statement. Also handles -1 (JIT prolog).
        if (ilOffset < 1)
            ilOffset = 1;

        return pdbMapper.GetSourceLocation(module.Name, (int)frame.Method.MetadataToken, ilOffset);
    }
}
