using System.Runtime.InteropServices;

using MixDbg.Engine.DbgEng;
using MixDbg.Models;

namespace MixDbg.Services;

/// <summary>
/// Stateless wrapper around dbgeng COM interfaces. All mutable state lives in
/// <see cref="DbgEngWrapperModel"/>. Encapsulates all COM interop (including
/// buffer allocation, marshaling, and HRESULT checking) so the rest of the
/// codebase never references dbgeng types directly.
/// </summary>
internal sealed class DbgEngWrapperService : IDbgEngWrapper
{
    // ── Lifecycle ──

    public DbgEngWrapperModel CreateModel() => new();

    public void CreateEngine(DbgEngWrapperModel model)
    {
        Guid iid = typeof(IDebugClient).GUID;
        Check(DbgEngNative.DebugCreate(ref iid, out object? obj));
        model.Client = (IDebugClient)obj;
        model.Control = (IDebugControl)obj;
        model.Symbols = (IDebugSymbols)obj;
        model.SysObjects = (IDebugSystemObjects)obj;
        model.DataSpaces = (IDebugDataSpaces)obj;
        model.Advanced = (IDebugAdvanced)obj;

        model.Callbacks = new EventCallbacks();

        // Wire internal EventCallbacks events to public model events.
        // Translate IDebugBreakpoint → uint bpId so the COM type stays encapsulated.
        model.Callbacks.OnBreakpoint += bp =>
        {
            _ = bp.GetId(out uint id);
            model.RaiseBreakpointHit(id);
        };
        model.Callbacks.OnExitProcess += model.RaiseExitProcess;
        model.Callbacks.OnLoadModule += model.RaiseLoadModule;
        model.Callbacks.OnCreateProcess += model.RaiseCreateProcess;
        model.Callbacks.OnExceptionBreakpoint += model.RaiseExceptionBreakpoint;
        model.Callbacks.OnClrNotification += () =>
        {
            model.RaiseClrNotification();
            if (model.ClrNotificationShouldBreak)
                model.Callbacks.ClrNotificationShouldBreak = true;
        };

        Check(model.Client.SetEventCallbacks(model.Callbacks));
    }

    public void CreateProcess(DbgEngWrapperModel model, string cmdLine) => Check(model.Client.CreateProcess(
            0,
            cmdLine,
            CreateProcessFlags.DebugOnlyThisProcess | CreateProcessFlags.CreateNewConsole));

    public void AttachProcess(DbgEngWrapperModel model, uint pid) => Check(model.Client.AttachProcess(0, pid, DebugAttach.Default));

    public void TerminateSession(DbgEngWrapperModel model)
    {
        try { _ = model.Client.TerminateProcesses(); } catch { }
        try { _ = model.Client.EndSession(DebugEnd.ActiveTerminate); } catch { }
    }

    public void DetachSession(DbgEngWrapperModel model)
    {
        try { _ = model.Client.DetachProcesses(); } catch { }
        try { _ = model.Client.EndSession(DebugEnd.ActiveDetach); } catch { }
    }

    // ── Symbols ──

    public void InitializeSymbols(DbgEngWrapperModel model, string? symbolPath, string? sourcePath)
    {
        _ = model.Symbols.SetSymbolOptions(SymOpt.LoadLines | SymOpt.DeferredLoads | SymOpt.UndName);
        if (symbolPath != null)
            _ = model.Symbols.SetSymbolPath(symbolPath);
        if (sourcePath != null)
            _ = model.Symbols.SetSourcePath(sourcePath);
    }

    public (ulong Offset, bool Success) GetOffsetByLine(DbgEngWrapperModel model, uint line, string file)
    {
        int hr = model.Symbols.GetOffsetByLine(line, file, out ulong offset);
        return (offset, hr >= 0);
    }

    public (string Name, ulong Displacement)? GetNameByOffset(DbgEngWrapperModel model, ulong offset)
    {
        IntPtr buf = Marshal.AllocHGlobal(512);
        try
        {
            int hr = model.Symbols.GetNameByOffset(offset, buf, 512, out _, out ulong displacement);
            if (hr < 0)
                return null;
            string name = Marshal.PtrToStringAnsi(buf) ?? "";
            return (name, displacement);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public (uint Line, string File)? GetLineByOffset(DbgEngWrapperModel model, ulong offset)
    {
        IntPtr buf = Marshal.AllocHGlobal(512);
        try
        {
            int hr = model.Symbols.GetLineByOffset(offset, out uint line, buf, 512, out _, out _);
            if (hr < 0)
                return null;
            string file = Marshal.PtrToStringAnsi(buf) ?? "";
            return (line, file);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public ulong? GetModuleByOffset(DbgEngWrapperModel model, ulong offset)
    {
        int hr = model.Symbols.GetModuleByOffset(offset, 0, out _, out ulong moduleBase);
        return hr >= 0 ? moduleBase : null;
    }

    // ── Execution ──

    public WaitForEventResult WaitForEvent(DbgEngWrapperModel model)
    {
        int hr = model.Control.WaitForEvent(0, 0xFFFFFFFF); // INFINITE
        if (hr < 0) return WaitForEventResult.Failed;
        if (hr == 1) return WaitForEventResult.Timeout; // S_FALSE
        return WaitForEventResult.EventOccurred; // S_OK
    }

    public void SetExecutionStatus(DbgEngWrapperModel model, EngineExecutionStatus status) => Check(model.Control.SetExecutionStatus((uint)status));

    public EngineExecutionStatus GetExecutionStatus(DbgEngWrapperModel model)
    {
        _ = model.Control.GetExecutionStatus(out uint status);
        return (EngineExecutionStatus)status;
    }

    public void SetInterrupt(DbgEngWrapperModel model) => model.Control.SetInterrupt(0); // DEBUG_INTERRUPT_ACTIVE

    public int ExecuteCommand(DbgEngWrapperModel model, string command) => model.Control.Execute(DebugOutCtl.Ignore, command, DebugExecute.Default);

    public EngineEventInfo GetLastEventInfo(DbgEngWrapperModel model)
    {
        IntPtr descBuf = Marshal.AllocHGlobal(256);
        try
        {
            _ = model.Control.GetLastEventInformation(
                out uint evtType, out uint evtPid, out uint evtTid,
                IntPtr.Zero, 0, out _,
                descBuf, 256, out _);
            string desc = Marshal.PtrToStringAnsi(descBuf) ?? "";
            return new EngineEventInfo(evtType, evtPid, evtTid, desc);
        }
        finally
        {
            Marshal.FreeHGlobal(descBuf);
        }
    }

    // ── Breakpoints ──

    public (uint BpId, bool Success) AddCodeBreakpoint(DbgEngWrapperModel model, ulong offset)
    {
        int hr = model.Control.AddBreakpoint(
            DebugBreakpointType.Code,
            0xFFFFFFFF, // DEBUG_ANY_ID
            out IDebugBreakpoint? bp);
        if (hr < 0)
            return (0, false);

        _ = bp.SetOffset(offset);
        _ = bp.AddFlags(DebugBreakpointFlag.Enabled);
        _ = bp.GetId(out uint bpId);
        return (bpId, true);
    }

    public (uint BpId, bool Success) AddHardwareBreakpoint(DbgEngWrapperModel model, ulong address, uint size)
    {
        int hr = model.Control.AddBreakpoint(
            DebugBreakpointType.Data,
            0xFFFFFFFF, // DEBUG_ANY_ID
            out IDebugBreakpoint? bp);
        if (hr < 0)
            return (0, false);

        hr = bp.SetDataParameters(size, DebugBreakAccess.Execute);
        if (hr < 0)
        {
            _ = model.Control.RemoveBreakpoint(bp);
            return (0, false);
        }

        _ = bp.SetOffset(address);
        _ = bp.AddFlags(DebugBreakpointFlag.Enabled);
        _ = bp.GetId(out uint bpId);
        return (bpId, true);
    }

    public bool RemoveBreakpoint(DbgEngWrapperModel model, uint bpId)
    {
        int hr = model.Control.GetBreakpointById(bpId, out IDebugBreakpoint? bp);
        if (hr < 0)
            return false;
        _ = model.Control.RemoveBreakpoint(bp);
        return true;
    }

    public uint GetBreakpointCount(DbgEngWrapperModel model)
    {
        _ = model.Control.GetNumberBreakpoints(out uint count);
        return count;
    }

    public uint? GetBreakpointIdByIndex(DbgEngWrapperModel model, uint index)
    {
        int hr = model.Control.GetBreakpointByIndex(index, out IDebugBreakpoint? bp);
        if (hr < 0 || bp == null)
            return null;
        _ = bp.GetId(out uint id);
        return id;
    }

    public (uint BpId, bool Success) AddDeferredBreakpoint(DbgEngWrapperModel model, string file, int line)
    {
        string buCmd = $"bu `{file}:{line}`";
        int hr = model.Control.Execute(DebugOutCtl.Ignore, buCmd, DebugExecute.Default);
        if (hr < 0)
            return (0, false);

        // The deferred breakpoint is the last one in the list.
        _ = model.Control.GetNumberBreakpoints(out uint bpCount);
        if (bpCount == 0)
            return (0, false);

        int bpHr = model.Control.GetBreakpointByIndex(bpCount - 1, out IDebugBreakpoint? bp);
        if (bpHr < 0 || bp == null)
            return (0, false);

        _ = bp.GetId(out uint id);
        return (id, true);
    }

    // ── Stack ──

    public NativeStackFrame[] GetStackTrace(DbgEngWrapperModel model, int maxFrames)
    {
        if (maxFrames <= 0) maxFrames = 50;

        int frameSize = Marshal.SizeOf<DEBUG_STACK_FRAME>();
        IntPtr buf = Marshal.AllocHGlobal(frameSize * maxFrames);
        try
        {
            int hr = model.Control.GetStackTrace(0, 0, 0, buf, (uint)maxFrames, out uint filled);
            if (hr < 0)
            {
                model.CachedStackFrames = [];
                return [];
            }

            // Cache raw frames for SetScopeAndGetLocals.
            DEBUG_STACK_FRAME[] rawFrames = new DEBUG_STACK_FRAME[filled];
            for (int i = 0; i < (int)filled; i++)
                rawFrames[i] = Marshal.PtrToStructure<DEBUG_STACK_FRAME>(buf + i * frameSize);
            model.CachedStackFrames = rawFrames;

            // Return public-facing frames with only the instruction offset.
            NativeStackFrame[] result = new NativeStackFrame[filled];
            for (int i = 0; i < (int)filled; i++)
                result[i] = new NativeStackFrame(rawFrames[i].InstructionOffset);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ── Scopes / Variables ──

    public int SetScopeAndGetLocals(DbgEngWrapperModel model, int frameId)
    {
        // Frame IDs are 1-based (from GetStackTrace).
        int index = frameId - 1;
        if (index < 0 || index >= model.CachedStackFrames.Length)
            return 0;

        DEBUG_STACK_FRAME frame = model.CachedStackFrames[index];

        // Pin the DEBUG_STACK_FRAME and pass to SetScope.
        int frameSize = Marshal.SizeOf<DEBUG_STACK_FRAME>();
        IntPtr frameBuf = Marshal.AllocHGlobal(frameSize);
        try
        {
            Marshal.StructureToPtr(frame, frameBuf, false);
            _ = model.Symbols.SetScope(frame.InstructionOffset, frameBuf, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(frameBuf);
        }

        // Get locals symbol group.
        int hr = model.Symbols.GetScopeSymbolGroup(
            DebugScopeGroup.All, IntPtr.Zero, out IDebugSymbolGroup2? group);
        if (hr < 0)
            return 0;

        _ = group.GetNumberSymbols(out uint count);
        return count == 0 ? 0 : model.Variables.Allocate(group, 0, count);
    }

    public VariableInfo[] GetVariables(DbgEngWrapperModel model, int variablesReference)
    {
        VariableContainer? container = model.Variables.Get(variablesReference);
        if (container == null)
            return [];

        IDebugSymbolGroup2 group = container.Group;
        uint start = container.StartIndex;
        uint count = container.Count;

        // Read parameters for all symbols in the range to check SubElements.
        int paramSize = Marshal.SizeOf<DEBUG_SYMBOL_PARAMETERS>();
        IntPtr paramsBuf = Marshal.AllocHGlobal(paramSize * (int)count);
        DEBUG_SYMBOL_PARAMETERS[] paramArray = new DEBUG_SYMBOL_PARAMETERS[count];
        int hr = group.GetSymbolParameters(start, count, paramsBuf);
        if (hr >= 0)
        {
            for (int i = 0; i < (int)count; i++)
            {
                paramArray[i] = Marshal.PtrToStructure<DEBUG_SYMBOL_PARAMETERS>(
                    paramsBuf + i * paramSize);
            }
        }
        Marshal.FreeHGlobal(paramsBuf);

        IntPtr nameBuf = Marshal.AllocHGlobal(512);
        IntPtr typeBuf = Marshal.AllocHGlobal(512);
        IntPtr valBuf = Marshal.AllocHGlobal(1024);

        try
        {
            VariableInfo[] result = new VariableInfo[count];
            for (uint i = 0; i < count; i++)
            {
                uint idx = start + i;

                string name = $"[{idx}]";
                if (group.GetSymbolName(idx, nameBuf, 512, out _) >= 0)
                    name = Marshal.PtrToStringAnsi(nameBuf) ?? name;

                string? type = null;
                if (group.GetSymbolTypeName(idx, typeBuf, 512, out _) >= 0)
                    type = Marshal.PtrToStringAnsi(typeBuf);

                string value = "";
                if (group.GetSymbolValueText(idx, valBuf, 1024, out _) >= 0)
                    value = Marshal.PtrToStringAnsi(valBuf) ?? "";

                int childRef = 0;
                if (hr >= 0 && paramArray[i].SubElements > 0)
                {
                    int expHr = group.ExpandSymbol(idx, true);
                    if (expHr >= 0)
                    {
                        _ = group.GetNumberSymbols(out uint newTotal);
                        uint childCount = paramArray[i].SubElements;
                        uint childStart = idx + 1;

                        if (childStart + childCount > newTotal)
                            childCount = newTotal - childStart;

                        if (childCount > 0)
                            childRef = model.Variables.Allocate(group, childStart, childCount);
                    }
                }

                result[i] = new VariableInfo(name, value, type, childRef);
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuf);
            Marshal.FreeHGlobal(typeBuf);
            Marshal.FreeHGlobal(valBuf);
        }
    }

    public void ClearVariables(DbgEngWrapperModel model) => model.Variables.Clear();

    // ── Threads ──

    public uint GetCurrentThreadId(DbgEngWrapperModel model)
    {
        _ = model.SysObjects.GetCurrentThreadId(out uint id);
        return id;
    }

    public uint GetCurrentThreadSystemId(DbgEngWrapperModel model)
    {
        _ = model.SysObjects.GetCurrentThreadSystemId(out uint id);
        return id;
    }

    public uint GetEventThreadId(DbgEngWrapperModel model)
    {
        _ = model.SysObjects.GetEventThread(out uint id);
        return id;
    }

    public (uint EngineId, uint SystemId)[] GetThreads(DbgEngWrapperModel model)
    {
        int hr = model.SysObjects.GetNumberThreads(out uint count);
        if (hr < 0 || count == 0)
            return [];

        uint[] ids = new uint[count];
        uint[] sysIds = new uint[count];
        _ = model.SysObjects.GetThreadIdsByIndex(0, count, ids, sysIds);

        (uint, uint)[] result = new (uint, uint)[count];
        for (int i = 0; i < count; i++)
            result[i] = (ids[i], sysIds[i]);
        return result;
    }

    // ── Helpers ──

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }
}