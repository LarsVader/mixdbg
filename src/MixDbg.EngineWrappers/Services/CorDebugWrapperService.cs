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
internal sealed class CorDebugWrapperService(
    MixDbg.Services.Interfaces.IPdbSourceMapper _pdbMapper) : ICorDebugWrapper
{
    /// <summary>Last diagnostic message from a failed operation, for caller logging.</summary>
    public string? LastDiagnostic { get; private set; }
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

    // ── Managed Variable Inspection ──

    public int InitializeManagedLocals(CorDebugWrapperModel model, uint osThreadId,
        ulong ip, string? assemblyPath, int methodToken, int ilOffset)
    {
        if (model.Process == null)
        {
            LastDiagnostic = "Process is null";
            return 0;
        }

        try
        {
            CorDebugILFrame? ilFrame = FindILFrame(model, osThreadId, methodToken);
            if (ilFrame == null)
                return 0; // LastDiagnostic set by FindILFrame

            LastDiagnostic = null;
            List<(string Name, CorDebugValue Value)> entries = [];

            // Enumerate parameters.
            string[] paramNames = assemblyPath != null
                ? _pdbMapper.GetParameterNames(assemblyPath, methodToken)
                : [];
            try
            {
                CorDebugValueEnum? argEnum = null;
                try { argEnum = ilFrame.EnumerateArguments(); } catch { }
                if (argEnum != null)
                {
                    int argIdx = 0;
                    foreach (CorDebugValue argVal in argEnum)
                    {
                        string name = argIdx < paramNames.Length ? paramNames[argIdx] : $"arg{argIdx}";
                        entries.Add((name, argVal));
                        argIdx++;
                    }
                }
                else
                {
                    // Fallback: try GetArgument(i) with PDB-derived count.
                    for (int i = 0; i < paramNames.Length; i++)
                    {
                        try
                        {
                            CorDebugValue argVal = ilFrame.GetArgument(i);
                            entries.Add((paramNames[i], argVal));
                        }
                        catch { break; }
                    }
                }
            }
            catch { }

            // Enumerate local variables.
            (string Name, int Index)[] localNames = assemblyPath != null
                ? _pdbMapper.GetLocalVariableNames(assemblyPath, methodToken, ilOffset)
                : [];
            try
            {
                CorDebugValueEnum? localEnum = null;
                try { localEnum = ilFrame.EnumerateLocalVariables(); } catch { }
                if (localEnum != null)
                {
                    Dictionary<int, string> nameMap = [];
                    foreach ((string name, int idx) in localNames)
                        nameMap[idx] = name;

                    int localIdx = 0;
                    foreach (CorDebugValue localVal in localEnum)
                    {
                        string name = nameMap.TryGetValue(localIdx, out string? pdbName)
                            ? pdbName
                            : $"local{localIdx}";
                        entries.Add((name, localVal));
                        localIdx++;
                    }
                }
                else
                {
                    // Fallback: try GetLocalVariable(i) with PDB-derived count.
                    foreach ((string name, int idx) in localNames)
                    {
                        try
                        {
                            CorDebugValue localVal = ilFrame.GetLocalVariable(idx);
                            entries.Add((name, localVal));
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (entries.Count == 0)
                return 0;

            ManagedVariableEntry entry = new() { Locals = [.. entries] };
            return model.ManagedVariables.Allocate(entry);
        }
        catch
        {
            return 0;
        }
    }

    public VariableInfo[] GetManagedVariables(CorDebugWrapperModel model, int variablesReference)
    {
        ManagedVariableEntry? entry = model.ManagedVariables.Get(variablesReference);
        return entry?.SimpleLocals
            ?? (entry?.Locals != null ? FormatLocals(model, entry.Locals) : null)
            ?? (entry?.ObjectValue != null ? FormatObjectFields(model, entry.ObjectValue) : null)
            ?? (entry?.ArrayValue != null ? FormatArrayElements(model, entry.ArrayValue, entry.ArrayCount) : null)
            ?? [];
    }

    public int StoreSimpleLocals(CorDebugWrapperModel model, VariableInfo[] locals)
    {
        if (locals.Length == 0)
            return 0;
        ManagedVariableEntry entry = new() { SimpleLocals = locals };
        return model.ManagedVariables.Allocate(entry);
    }

    public void ClearManagedVariables(CorDebugWrapperModel model) => model.ManagedVariables.Clear();

    // ── Managed Variable Formatting ──

    private VariableInfo[] FormatLocals(CorDebugWrapperModel model,
        (string Name, CorDebugValue Value)[] locals)
    {
        List<VariableInfo> result = [];
        foreach ((string name, CorDebugValue value) in locals)
        {
            try
            {
                result.Add(FormatValue(model, name, value));
            }
            catch
            {
                result.Add(new VariableInfo(name, "<error>", null, 0));
            }
        }
        return [.. result];
    }

    private VariableInfo FormatValue(CorDebugWrapperModel model, string name, CorDebugValue value)
    {
        // Dereference reference values first.
        CorDebugValue unwrapped = value;
        while (unwrapped is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull)
                return new VariableInfo(name, "null", GetTypeName(value), 0);
            try { unwrapped = refVal.Dereference(); }
            catch { return new VariableInfo(name, "<cannot dereference>", GetTypeName(value), 0); }
        }

        // Handle boxed values.
        if (unwrapped is CorDebugBoxValue boxVal)
        {
            try { unwrapped = boxVal.Object; }
            catch { return new VariableInfo(name, "<boxed>", GetTypeName(value), 0); }
        }

        // Primitive / generic values.
        if (unwrapped is CorDebugGenericValue genVal)
            return FormatGenericValue(name, genVal, value);

        // String values.
        if (unwrapped is CorDebugStringValue strVal)
        {
            try
            {
                string s = strVal.GetString(strVal.Length);
                return new VariableInfo(name, $"\"{s}\"", "string", 0);
            }
            catch { return new VariableInfo(name, "<string error>", "string", 0); }
        }

        // Array values.
        if (unwrapped is CorDebugArrayValue arrVal)
        {
            try
            {
                int count = (int)arrVal.Count;
                string typeName = GetTypeName(value) ?? "array";
                int childRef = model.ManagedVariables.Allocate(
                    new ManagedVariableEntry { ArrayValue = arrVal, ArrayCount = count });
                return new VariableInfo(name, $"{typeName}[{count}]", typeName, childRef);
            }
            catch { return new VariableInfo(name, "<array error>", null, 0); }
        }

        // Object values (class / valuetype).
        if (unwrapped is CorDebugObjectValue objVal)
        {
            try
            {
                string typeName = GetObjectTypeName(objVal);
                int childRef = model.ManagedVariables.Allocate(
                    new ManagedVariableEntry { ObjectValue = objVal });
                return new VariableInfo(name, $"{{{typeName}}}", typeName, childRef);
            }
            catch { return new VariableInfo(name, "<object>", null, 0); }
        }

        return new VariableInfo(name, unwrapped.ToString() ?? "<unknown>", GetTypeName(value), 0);
    }

    private static VariableInfo FormatGenericValue(string name, CorDebugGenericValue genVal, CorDebugValue originalValue)
    {
        try
        {
            CorElementType elemType = originalValue.Type;
            int size = originalValue.Size;
            byte[] rawData = new byte[size];
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                genVal.GetValue(buf);
                Marshal.Copy(buf, rawData, 0, size);
            }
            finally { Marshal.FreeHGlobal(buf); }

            string formatted = elemType switch
            {
                CorElementType.Boolean => BitConverter.ToBoolean(rawData, 0).ToString(),
                CorElementType.Char => $"'{BitConverter.ToChar(rawData, 0)}'",
                CorElementType.I1 => ((sbyte)rawData[0]).ToString(),
                CorElementType.U1 => rawData[0].ToString(),
                CorElementType.I2 => BitConverter.ToInt16(rawData, 0).ToString(),
                CorElementType.U2 => BitConverter.ToUInt16(rawData, 0).ToString(),
                CorElementType.I4 => BitConverter.ToInt32(rawData, 0).ToString(),
                CorElementType.U4 => BitConverter.ToUInt32(rawData, 0).ToString(),
                CorElementType.I8 => BitConverter.ToInt64(rawData, 0).ToString(),
                CorElementType.U8 => BitConverter.ToUInt64(rawData, 0).ToString(),
                CorElementType.R4 => BitConverter.ToSingle(rawData, 0).ToString(),
                CorElementType.R8 => BitConverter.ToDouble(rawData, 0).ToString(),
                CorElementType.I => ((nint)BitConverter.ToInt64(rawData, 0)).ToString(),
                CorElementType.U => ((nuint)BitConverter.ToUInt64(rawData, 0)).ToString(),
                _ => $"0x{Convert.ToHexString(rawData)}",
            };
            string typeName = elemType switch
            {
                CorElementType.Boolean => "bool",
                CorElementType.Char => "char",
                CorElementType.I1 => "sbyte",
                CorElementType.U1 => "byte",
                CorElementType.I2 => "short",
                CorElementType.U2 => "ushort",
                CorElementType.I4 => "int",
                CorElementType.U4 => "uint",
                CorElementType.I8 => "long",
                CorElementType.U8 => "ulong",
                CorElementType.R4 => "float",
                CorElementType.R8 => "double",
                CorElementType.I => "nint",
                CorElementType.U => "nuint",
                _ => elemType.ToString(),
            };
            return new VariableInfo(name, formatted, typeName, 0);
        }
        catch
        {
            return new VariableInfo(name, "<value error>", null, 0);
        }
    }

    private VariableInfo[] FormatObjectFields(CorDebugWrapperModel model, CorDebugObjectValue objVal)
    {
        try
        {
            CorDebugClass cls = objVal.Class;
            CorDebugModule module = cls.Module;
            MetaDataImport metaData = module.GetMetaDataInterface<MetaDataImport>();

            List<VariableInfo> fields = [];
            EnumFieldsRecursive(model, objVal, cls, metaData, fields);
            return [.. fields];
        }
        catch
        {
            return [];
        }
    }

    private void EnumFieldsRecursive(CorDebugWrapperModel model, CorDebugObjectValue objVal,
        CorDebugClass cls, MetaDataImport metaData, List<VariableInfo> fields)
    {
        try
        {
            mdTypeDef classToken = cls.Token;

            // Enumerate fields of this class.
            mdFieldDef[] fieldTokens = metaData.EnumFields(classToken);
            foreach (mdFieldDef fieldToken in fieldTokens)
            {
                try
                {
                    GetFieldPropsResult props = metaData.GetFieldProps(fieldToken);

                    // Skip static fields (CorFieldAttr.fdStatic = 0x10).
                    if (((int)props.pdwAttr & 0x10) != 0)
                        continue;

                    CorDebugValue fieldVal = objVal.GetFieldValue(cls.Raw, fieldToken);
                    fields.Add(FormatValue(model, props.szField, fieldVal));
                }
                catch
                {
                    // Field access failed — skip.
                }
            }

            // Walk base class chain.
            try
            {
                GetTypeDefPropsResult typeProps = metaData.GetTypeDefProps(classToken);
                if (!typeProps.ptkExtends.IsNil && typeProps.ptkExtends != default)
                {
                    // Only follow TypeDef parents (not TypeRef to System.Object).
                    if (((int)typeProps.ptkExtends & 0x02000000) != 0)
                    {
                        CorDebugClass baseClass = cls.Module.GetClassFromToken((mdTypeDef)typeProps.ptkExtends);
                        EnumFieldsRecursive(model, objVal, baseClass, metaData, fields);
                    }
                }
            }
            catch { }
        }
        catch { }
    }

    private VariableInfo[] FormatArrayElements(CorDebugWrapperModel model, CorDebugArrayValue arrVal, int count)
    {
        int limit = Math.Min(count, 100);
        List<VariableInfo> elements = [];
        for (int i = 0; i < limit; i++)
        {
            try
            {
                CorDebugValue elem = arrVal.GetElementAtPosition(i);
                elements.Add(FormatValue(model, $"[{i}]", elem));
            }
            catch
            {
                elements.Add(new VariableInfo($"[{i}]", "<error>", null, 0));
            }
        }
        if (count > 100)
            elements.Add(new VariableInfo("...", $"({count - 100} more)", null, 0));
        return [.. elements];
    }

    /// <summary>
    /// Finds the ICorDebugILFrame matching the given method token on the specified thread.
    /// </summary>
    private CorDebugILFrame? FindILFrame(CorDebugWrapperModel model, uint osThreadId, int methodToken)
    {
        if (model.Process == null)
            return null;

        CorDebugThread? clrThread = null;
        int threadCount = 0;
        try
        {
            foreach (CorDebugThread? thread in model.Process.Threads)
            {
                threadCount++;
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
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"FindILFrame: Process.Threads threw after {threadCount}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }

        if (clrThread == null)
        {
            LastDiagnostic = $"FindILFrame: thread {osThreadId} not found ({threadCount} enumerated)";
            return null;
        }

        int chainCount = 0, frameCount = 0;
        try
        {
            foreach (CorDebugChain? chain in clrThread.Chains)
            {
                chainCount++;
                if (!chain.IsManaged)
                    continue;

                foreach (CorDebugFrame? frame in chain.Frames)
                {
                    frameCount++;
                    try
                    {
                        if ((int)frame.Function.Token == methodToken && frame is CorDebugILFrame ilFrame)
                            return ilFrame;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"FindILFrame: chain/frame threw: {ex.GetType().Name}: {ex.Message}";
            return null;
        }

        LastDiagnostic = $"FindILFrame: token 0x{methodToken:X8} not in {chainCount} chains, {frameCount} frames";
        return null;
    }

    private static string? GetTypeName(CorDebugValue value)
    {
        try
        {
            CorElementType elemType = value.Type;
            return elemType switch
            {
                CorElementType.Boolean => "bool",
                CorElementType.Char => "char",
                CorElementType.I1 => "sbyte",
                CorElementType.U1 => "byte",
                CorElementType.I2 => "short",
                CorElementType.U2 => "ushort",
                CorElementType.I4 => "int",
                CorElementType.U4 => "uint",
                CorElementType.I8 => "long",
                CorElementType.U8 => "ulong",
                CorElementType.R4 => "float",
                CorElementType.R8 => "double",
                CorElementType.String => "string",
                _ => null,
            };
        }
        catch { return null; }
    }

    private static string GetObjectTypeName(CorDebugObjectValue objVal)
    {
        try
        {
            CorDebugClass cls = objVal.Class;
            CorDebugModule module = cls.Module;
            MetaDataImport metaData = module.GetMetaDataInterface<MetaDataImport>();
            GetTypeDefPropsResult props = metaData.GetTypeDefProps(cls.Token);
            return props.szTypeDef;
        }
        catch
        {
            return "<object>";
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