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
            DbgEngDataTarget dataTarget = new(
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
            string runtimeDir = Path.GetDirectoryName(coreclrPath)!;
            string dacPath = Path.Combine(runtimeDir, "mscordaccore.dll");

            if (model.DacHandle == IntPtr.Zero)
                model.DacHandle = NativeLibrary.Load(dacPath);

            DbgEngClrDataTarget clrDataTarget = new(
                dbgEngModel.DataSpaces, dbgEngModel.Advanced, dbgEngModel.SysObjects);
            clrDataTarget.AddModuleBase(coreclrPath, baseAddress);

            Extensions.CLRDataCreateInstanceInterfaces interfaces = Extensions.CLRDataCreateInstance(model.DacHandle, clrDataTarget);
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
            CorDebugAppDomain[] appDomains = model.Process.AppDomains;
            foreach (CorDebugAppDomain? appDomain in appDomains)
            {
                foreach (CorDebugAssembly? assembly in appDomain.Assemblies)
                {
                    foreach (CorDebugModule? module in assembly.Modules)
                    {
                        long baseAddr = (long)module.BaseAddress;
                        string? name = module.Name;
                        string? pdbPath = name != null ? Path.ChangeExtension(name, ".pdb") : null;
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

    public ManagedModuleInfo[] GetModules(CorDebugWrapperModel model) => [.. model.Modules.Select(kv => new ManagedModuleInfo(kv.Value.Path, kv.Value.PdbPath, kv.Key))];

    public ManagedModuleInfo? FindModuleByName(CorDebugWrapperModel model, string assemblyName)
    {
        foreach ((long baseAddr, CorDebugWrapperModule? mod) in model.Modules)
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
            foreach (CorDebugThread? thread in model.Process.Threads)
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

            List<RawManagedFrame> frames = [];

            foreach (CorDebugChain? chain in clrThread.Chains)
            {
                if (!chain.IsManaged)
                    continue;

                foreach (CorDebugFrame? frame in chain.Frames)
                {
                    try
                    {
                        CorDebugFunction function = frame.Function;
                        CorDebugModule module = function.Module;
                        int token = (int)function.Token;
                        string modulePath = module.Name;

                        int ilOffset = 0;
                        try
                        {
                            if (frame is CorDebugILFrame ilFrame)
                            {
                                GetIPResult ip = ilFrame.IP;
                                ilOffset = (int)ip.pnOffset;
                            }
                        }
                        catch { }

                        string name = GetFrameName(function);
                        frames.Add(new RawManagedFrame(token, modulePath, ilOffset, name));
                    }
                    catch { }
                }
            }

            return [.. frames];
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
            nint enumHandle = model.XclrProcess.StartEnumModules();
            try
            {
                while (true)
                {
                    HRESULT moduleResult = model.XclrProcess.TryEnumModule(ref enumHandle, out XCLRDataModule? xModule);
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

                        XCLRDataMethodDefinition methodDef = xModule.GetMethodDefinitionByToken((mdMethodDef)methodToken);

                        HRESULT startResult = methodDef.TryStartEnumInstances(null!, out nint instHandle);
                        if (startResult != HRESULT.S_OK)
                            continue;

                        try
                        {
                            HRESULT instResult = methodDef.TryEnumInstance(ref instHandle, out XCLRDataMethodInstance? methodInst);
                            if (instResult == HRESULT.S_OK)
                            {
                                HRESULT entryResult = methodInst.TryGetRepresentativeEntryAddress(out CLRDATA_ADDRESS entryAddr);
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
        if (model.LegacyBreakpoints.TryGetValue(bpId, out CorDebugFunctionBreakpoint? corBp))
        {
            try { corBp.Activate(false); } catch { }
            _ = model.LegacyBreakpoints.Remove(bpId);
        }
    }

    // ── Private ──

    private static string GetFrameName(CorDebugFunction function)
    {
        try
        {
            CorDebugModule module = function.Module;
            MetaDataImport metaData = module.GetMetaDataInterface<MetaDataImport>();
            MetaDataImport_GetMethodPropsResult methodProps = metaData.GetMethodProps(function.Token);
            string typeName = metaData.GetTypeDefProps(methodProps.pClass).szTypeDef;
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
        string runtimeDir = Path.GetDirectoryName(coreClrPath)!;
        string mscordbiPath = Path.Combine(runtimeDir, "mscordbi.dll");
        string mscordacPath = Path.Combine(runtimeDir, "mscordaccore.dll");

        nint hMscordbi = NativeLibrary.Load(mscordbiPath);
        nint hDac = NativeLibrary.Load(mscordacPath);
        nint pFunc = NativeLibrary.GetExport(hMscordbi, "OpenVirtualProcessImpl");

        StrategyBasedComWrappers comWrappers = new();

        IntPtr pDataTarget = comWrappers.GetOrCreateComInterfaceForObject(
            dataTarget, CreateComInterfaceFlags.None);
        try
        {
            CLR_DEBUGGING_VERSION maxVersion = new()
            {
                wStructVersion = 0,
                wMajor = 255,
                wMinor = 255,
                wBuild = 255,
                wRevision = 255,
            };
            Guid riid = typeof(ICorDebugProcess).GUID;
            CLR_DEBUGGING_VERSION version = new();
            int flags = 0;
            IntPtr ppInstance = IntPtr.Zero;

            delegate* unmanaged[Stdcall]<
                ulong, IntPtr, IntPtr,
                CLR_DEBUGGING_VERSION*, Guid*,
                IntPtr*, CLR_DEBUGGING_VERSION*, int*, int> fn =
                (delegate* unmanaged[Stdcall]<
                ulong, IntPtr, IntPtr,
                CLR_DEBUGGING_VERSION*, Guid*,
                IntPtr*, CLR_DEBUGGING_VERSION*, int*, int>)pFunc;

            int hr = fn(
                coreClrBase, pDataTarget, hDac,
                &maxVersion, &riid,
                &ppInstance, &version, &flags);

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            ICorDebugProcess raw = (ICorDebugProcess)comWrappers.GetOrCreateObjectForComInstance(
                ppInstance, CreateObjectFlags.None);
            _ = Marshal.Release(ppInstance);
            clrVersion = version;
            return new CorDebugProcess(raw);
        }
        finally
        {
            _ = Marshal.Release(pDataTarget);
        }
    }
}