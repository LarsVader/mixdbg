using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;
using MixDbg.Engine.CorDebug;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless wrapper around ICorDebug V4 (piggybacked on the dbgeng session).
/// All mutable state lives in <see cref="CorDebugWrapperModel"/>. Encapsulates
/// all ClrDebug COM interop so the rest of the codebase never references
/// ClrDebug types directly. All methods must be called on the engine thread.
/// </summary>
internal sealed class CorDebugWrapperService(
    IPdbSourceMapper pdbMapper) : ICorDebugWrapper
{
    private readonly IPdbSourceMapper _pdbMapper = pdbMapper;

    // ── Lifecycle ──

    public CorDebugWrapperModel CreateModel() => new();

    public bool InitializeRuntime(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress)
    {
        if (model.Initialized)
            return true;

        try
        {
            // _log.LogInfo(_logStore, $"Initializing ICorDebug V4 via OpenVirtualProcessImpl (coreclr={coreclrPath}, base=0x{baseAddress:X})");

            // Create the data target bridge that lets ICorDebug read/write through dbgeng.
            var dataTarget = new DbgEngDataTarget(
                dbgEngModel.DataSpaces, dbgEngModel.Advanced, dbgEngModel.SysObjects);

            model.Process = OpenVirtualProcessImpl(
                coreclrPath, baseAddress, dataTarget,
                out var clrVersion);
            // _log.LogInfo(_logStore, $"ICorDebug V4 initialized: CLR {clrVersion.wMajor}.{clrVersion.wMinor}.{clrVersion.wBuild}");

            // Enumerate currently loaded modules.
            RefreshModules(model);

            // Initialize the DAC for querying JIT native code addresses.
            try { InitializeDacInternal(model, dbgEngModel, coreclrPath, baseAddress); }
            catch { }

            model.Initialized = true;
            return true;
        }
        catch
        {
            // _log.LogError(_logStore, $"Failed to initialize managed debugging: {ex.Message}");
            return false;
        }
    }

    public bool InitializeDac(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress)
    {
        try
        {
            InitializeDacInternal(model, dbgEngModel, coreclrPath, baseAddress);
            return true;
        }
        catch
        {
            // _log.LogWarning(_logStore, $"DAC initialization failed: {ex.Message}");
            return false;
        }
    }

    public bool IsInitialized(CorDebugWrapperModel model) => model.Initialized;

    // ── Module Enumeration ──

    public void RefreshModules(CorDebugWrapperModel model)
    {
        if (model.Process == null)
            return;

        try
        {
            // Notify ICorDebug that the process is stopped so the DAC refreshes.
            try { model.Process.ProcessStateChanged(CorDebugStateChange.FLUSH_ALL); }
            catch { }

            var appDomains = model.Process.AppDomains;
            // _log.LogInfo(_logStore, $"  EnumerateModules: {appDomains.Length} app domains");

            foreach (var appDomain in appDomains)
            {
                // _log.LogInfo(_logStore, $"  AppDomain: {appDomain.Name}");
                var assemblies = appDomain.Assemblies;
                // _log.LogInfo(_logStore, $"    {assemblies.Length} assemblies");

                foreach (var assembly in assemblies)
                {
                    foreach (var module in assembly.Modules)
                    {
                        var baseAddr = (long)module.BaseAddress;
                        var isNew = !model.Modules.ContainsKey(baseAddr);

                        var name = module.Name;
                        var pdbPath = name != null ? Path.ChangeExtension(name, ".pdb") : null;
                        model.Modules[baseAddr] = new CorDebugWrapperModule
                        {
                            Module = module,
                            Path = name,
                            PdbPath = pdbPath != null && File.Exists(pdbPath) ? pdbPath : null,
                        };
                        // if (isNew)
                        //     _log.LogInfo(_logStore, $"  ICorDebug module: {name} (pdb={pdbPath != null && File.Exists(pdbPath)})");
                    }
                }
            }
        }
        catch
        {
            // _log.LogInfo(_logStore, $"  Module enumeration error: {ex.Message}");
        }
    }

    public ManagedModuleInfo[] GetModules(CorDebugWrapperModel model)
    {
        return model.Modules.Values
            .Select(m => new ManagedModuleInfo(m.Path, m.PdbPath,
                model.Modules.FirstOrDefault(kv => kv.Value == m).Key))
            .ToArray();
    }

    public ManagedModuleInfo? FindModuleByName(CorDebugWrapperModel model, string assemblyName)
    {
        foreach (var (baseAddr, mod) in model.Modules)
        {
            if (mod.Path != null &&
                Path.GetFileNameWithoutExtension(mod.Path)
                    .Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return new ManagedModuleInfo(mod.Path, mod.PdbPath, baseAddr);
            }
        }
        return null;
    }

    // ── Stack Traces ──

    public ManagedFrameInfo[] GetManagedStackFrames(CorDebugWrapperModel model, uint osThreadId)
    {
        if (model.Process == null)
            return [];

        try
        {
            CorDebugThread? clrThread = null;
            foreach (var thread in model.Process.Threads)
            {
                try
                {
                    if ((uint)thread.Id == osThreadId)
                    {
                        clrThread = thread;
                        break;
                    }
                }
                catch { }
            }

            if (clrThread == null)
            {
                // _log.LogInfo(_logStore, $"No CLR thread found for OS thread {osThreadId}");
                return [];
            }

            var frames = new List<ManagedFrameInfo>();

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

                        string? sourceFile = null;
                        int line = 0;

                        if (modulePath != null)
                        {
                            var srcLoc = _pdbMapper.GetSourceLocation(modulePath, token, ilOffset > 0 ? ilOffset : 1);
                            if (srcLoc != null)
                            {
                                sourceFile = srcLoc.Value.File;
                                line = srcLoc.Value.Line;
                            }
                        }

                        var name = GetFrameName(function);

                        frames.Add(new ManagedFrameInfo(name, sourceFile, line, ilOffset));
                    }
                    catch
                    {
                        // _log.LogInfo(_logStore, $"  Frame enumeration error: {ex.Message}");
                    }
                }
            }

            // _log.LogInfo(_logStore, $"GetManagedStackFrames: {frames.Count} managed frames");
            return frames.ToArray();
        }
        catch
        {
            // _log.LogError(_logStore, $"GetManagedStackFrames failed: {ex.Message}");
            return [];
        }
    }

    // ── DAC Operations ──

    public ulong ResolveNativeEntryViaXclrData(CorDebugWrapperModel model, int methodToken, string? assemblyName)
    {
        // _log.LogInfo(_logStore, $"  ResolveViaXclrData: token=0x{methodToken:X8} assembly={assemblyName} xclrProcess={model.XclrProcess != null}");

        if (model.XclrProcess == null)
            return 0;

        try
        {
            // _log.LogInfo(_logStore, "  XCLRData: StartEnumModules...");
            var enumHandle = model.XclrProcess.StartEnumModules();
            int moduleCount = 0;
            try
            {
                while (true)
                {
                    var moduleResult = model.XclrProcess.TryEnumModule(ref enumHandle, out var xModule);
                    if (moduleResult != HRESULT.S_OK)
                        break;
                    moduleCount++;

                    try
                    {
                        if (assemblyName != null)
                        {
                            string moduleName = "";
                            try { moduleName = xModule.Name; } catch { }
                            if (!moduleName.Contains(assemblyName, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        var methodDef = xModule.GetMethodDefinitionByToken((mdMethodDef)methodToken);

                        var startResult = methodDef.TryStartEnumInstances(null!, out var instHandle);
                        if (startResult != HRESULT.S_OK)
                        {
                            // _log.LogInfo(_logStore, $"  XCLRData: StartEnumInstances failed: {startResult}");
                            continue;
                        }
                        try
                        {
                            var instResult = methodDef.TryEnumInstance(ref instHandle, out var methodInst);
                            if (instResult == HRESULT.S_OK)
                            {
                                var entryResult = methodInst.TryGetRepresentativeEntryAddress(out var entryAddr);
                                // _log.LogInfo(_logStore, $"  XCLRData: EntryAddress result={entryResult} addr=0x{(ulong)entryAddr:X}");
                                if (entryResult == HRESULT.S_OK && (ulong)entryAddr != 0)
                                    return (ulong)entryAddr;
                            }
                        }
                        finally
                        {
                            methodDef.EndEnumInstances(instHandle);
                        }
                    }
                    catch { }
                }
                // _log.LogInfo(_logStore, $"  XCLRData: enumerated {moduleCount} modules, method not found/not JIT'd");
            }
            finally
            {
                model.XclrProcess.EndEnumModules(enumHandle);
            }
        }
        catch
        {
            // _log.LogInfo(_logStore, $"  XCLRData lookup failed for token 0x{methodToken:X8}: {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    public ulong ResolveNativeEntryPoint(CorDebugWrapperModel model, ulong symbolAddress)
    {
        if (model.SosDac == null)
            return symbolAddress;

        try
        {
            var mdResult = model.SosDac.TryGetMethodDescPtrFromIP((CLRDATA_ADDRESS)symbolAddress, out var methodDesc);
            if (mdResult != HRESULT.S_OK)
            {
                // _log.LogInfo(_logStore, $"  GetMethodDescPtrFromIP(0x{symbolAddress:X}) failed: {mdResult}");
                return symbolAddress;
            }

            var dataResult = model.SosDac.TryGetMethodDescData(methodDesc, 0, out var data);
            if (dataResult != HRESULT.S_OK)
            {
                // _log.LogInfo(_logStore, $"  GetMethodDescData(0x{(ulong)methodDesc:X}) failed: {dataResult}");
                return symbolAddress;
            }

            var entryPoint = (ulong)data.data.NativeCodeAddr;
            if (entryPoint != 0 && data.data.bHasNativeCode)
            {
                // _log.LogInfo(_logStore, $"  DAC: MethodDesc=0x{(ulong)methodDesc:X} NativeCodeAddr=0x{entryPoint:X} (symbol was 0x{symbolAddress:X})");
                return entryPoint;
            }

            return symbolAddress;
        }
        catch
        {
            // _log.LogInfo(_logStore, $"  DAC lookup failed: {ex.Message}");
            return symbolAddress;
        }
    }

    // ── Breakpoint Support ──

    public void DeactivateLegacyBreakpoint(CorDebugWrapperModel model, int bpId)
    {
        if (model.LegacyBreakpoints.TryGetValue(bpId, out var corBp))
        {
            try { corBp.Activate(false); } catch { }
            model.LegacyBreakpoints.Remove(bpId);
        }
    }

    // ── Private ──

    private void InitializeDacInternal(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress)
    {
        try
        {
            var runtimeDir = Path.GetDirectoryName(coreclrPath)!;
            var dacPath = Path.Combine(runtimeDir, "mscordaccore.dll");

            if (model.DacHandle == IntPtr.Zero)
            {
                // _log.LogInfo(_logStore, "DAC: Loading mscordaccore.dll...");
                model.DacHandle = NativeLibrary.Load(dacPath);
            }

            var clrDataTarget = new DbgEngClrDataTarget(
                dbgEngModel.DataSpaces, dbgEngModel.Advanced, dbgEngModel.SysObjects);
            clrDataTarget.AddModuleBase(coreclrPath, baseAddress);

            var interfaces = Extensions.CLRDataCreateInstance(model.DacHandle, clrDataTarget);
            model.SosDac = interfaces.SOSDacInterface;
            model.XclrProcess = interfaces.XCLRDataProcess;
            model.DacLoaded = true;
            // _log.LogInfo(_logStore, "DAC: XCLRDataProcess refreshed");
        }
        catch
        {
            // _log.LogWarning(_logStore, $"DAC initialization failed: {ex.Message}");
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
    /// Calls <c>mscordbi!OpenVirtualProcessImpl</c> directly to create a piggybacked
    /// <c>ICorDebugProcess</c>.
    /// </summary>
    private static unsafe CorDebugProcess OpenVirtualProcessImpl(
        string coreClrPath, ulong coreClrBase, ICorDebugMutableDataTarget dataTarget,
        out CLR_DEBUGGING_VERSION clrVersion)
    {
        clrVersion = default;
        var runtimeDir = Path.GetDirectoryName(coreClrPath)!;
        var mscordbiPath = Path.Combine(runtimeDir, "mscordbi.dll");
        var mscordacPath = Path.Combine(runtimeDir, "mscordaccore.dll");

        var hMscordbi = NativeLibrary.Load(mscordbiPath);
        var hDac = NativeLibrary.Load(mscordacPath);
        var pFunc = NativeLibrary.GetExport(hMscordbi, "OpenVirtualProcessImpl");

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

            var fn = (delegate* unmanaged[Stdcall]<
                ulong, IntPtr, IntPtr,
                CLR_DEBUGGING_VERSION*, Guid*,
                IntPtr*, CLR_DEBUGGING_VERSION*, int*, int>)pFunc;

            int hr = fn(
                coreClrBase, pDataTarget, hDac,
                &maxVersion, &riid,
                &ppInstance, &version, &flags);

            // log.LogInfo(logStore, $"OpenVirtualProcessImpl: hr=0x{hr:X8} flags=0x{flags:X} version={version.wMajor}.{version.wMinor}.{version.wBuild}");

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

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
