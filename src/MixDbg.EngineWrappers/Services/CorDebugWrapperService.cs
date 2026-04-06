using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;
using MixDbg.Engine.CorDebug;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Thin wrapper around ICorDebug V4 (piggybacked on the dbgeng session).
/// All mutable state lives in <see cref="CorDebugWrapperModel"/>. Encapsulates
/// all ClrDebug COM interop. Contains no business logic — only COM calls,
/// marshaling, and HRESULT checking.
/// </summary>
internal sealed class CorDebugWrapperService : ICorDebugWrapper
{
    // ── Lifecycle ──

    public CorDebugWrapperModel CreateModel() => new();

    public bool InitializeProcess(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress)
    {
        try
        {
            var dataTarget = new DbgEngDataTarget(
                dbgEngModel.DataSpaces, dbgEngModel.Advanced, dbgEngModel.SysObjects);

            model.Process = OpenVirtualProcessImpl(
                coreclrPath, baseAddress, dataTarget, out _);
            return model.Process != null;
        }
        catch
        {
            return false;
        }
    }

    public bool InitializeDac(CorDebugWrapperModel model, DbgEngWrapperModel dbgEngModel,
        string coreclrPath, ulong baseAddress)
    {
        try
        {
            var runtimeDir = Path.GetDirectoryName(coreclrPath)!;
            var dacPath = Path.Combine(runtimeDir, "mscordaccore.dll");

            if (model.DacHandle == IntPtr.Zero)
                model.DacHandle = NativeLibrary.Load(dacPath);

            var clrDataTarget = new DbgEngClrDataTarget(
                dbgEngModel.DataSpaces, dbgEngModel.Advanced, dbgEngModel.SysObjects);
            clrDataTarget.AddModuleBase(coreclrPath, baseAddress);

            var interfaces = Extensions.CLRDataCreateInstance(model.DacHandle, clrDataTarget);
            model.SosDac = interfaces.SOSDacInterface;
            model.XclrProcess = interfaces.XCLRDataProcess;
            model.DacLoaded = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsInitialized(CorDebugWrapperModel model) => model.Initialized;

    // ── Process State ──

    public void FlushProcessState(CorDebugWrapperModel model)
    {
        if (model.Process == null) return;
        try { model.Process.ProcessStateChanged(CorDebugStateChange.FLUSH_ALL); }
        catch { }
    }

    // ── Module Enumeration ──

    public void RefreshModules(CorDebugWrapperModel model)
    {
        if (model.Process == null)
            return;

        try
        {
            var appDomains = model.Process.AppDomains;
            foreach (var appDomain in appDomains)
            {
                foreach (var assembly in appDomain.Assemblies)
                {
                    foreach (var module in assembly.Modules)
                    {
                        var baseAddr = (long)module.BaseAddress;
                        var name = module.Name;
                        var pdbPath = name != null ? Path.ChangeExtension(name, ".pdb") : null;
                        model.Modules[baseAddr] = new CorDebugWrapperModule
                        {
                            Module = module,
                            Path = name,
                            PdbPath = pdbPath != null && File.Exists(pdbPath) ? pdbPath : null,
                        };
                    }
                }
            }
        }
        catch { }
    }

    public ManagedModuleInfo[] GetModules(CorDebugWrapperModel model)
    {
        return model.Modules
            .Select(kv => new ManagedModuleInfo(kv.Value.Path, kv.Value.PdbPath, kv.Key))
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

    public RawManagedFrame[] GetRawManagedFrames(CorDebugWrapperModel model, uint osThreadId)
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
                return [];

            var frames = new List<RawManagedFrame>();

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

                        var name = GetFrameName(function);
                        frames.Add(new RawManagedFrame(token, modulePath, ilOffset, name));
                    }
                    catch { }
                }
            }

            return frames.ToArray();
        }
        catch
        {
            return [];
        }
    }

    // ── DAC Operations ──

    public ulong ResolveNativeEntryViaXclrData(CorDebugWrapperModel model, int methodToken, string? assemblyName)
    {
        if (model.XclrProcess == null)
            return 0;

        try
        {
            var enumHandle = model.XclrProcess.StartEnumModules();
            try
            {
                while (true)
                {
                    var moduleResult = model.XclrProcess.TryEnumModule(ref enumHandle, out var xModule);
                    if (moduleResult != HRESULT.S_OK)
                        break;

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
                            continue;

                        try
                        {
                            var instResult = methodDef.TryEnumInstance(ref instHandle, out var methodInst);
                            if (instResult == HRESULT.S_OK)
                            {
                                var entryResult = methodInst.TryGetRepresentativeEntryAddress(out var entryAddr);
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
            }
            finally
            {
                model.XclrProcess.EndEnumModules(enumHandle);
            }
        }
        catch { }

        return 0;
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
