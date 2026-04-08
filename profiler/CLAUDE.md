# MixDbgProfiler — CLR Profiler DLL

Native C++ DLL implementing `ICorProfilerCallback2`. Runs inside the debuggee, sends JIT and function-enter notifications to MixDbg via named pipe. See `README.md` for the full high-level explanation.

## Build

```bash
make all    # or: MSBuild MixDbgProfiler.vcxproj -p:Configuration=Debug -p:Platform=x64
```

Output: `x64/Debug/MixDbgProfiler.dll`

## File Structure

```
profiler/
  CoreClrTypes.h            # Shared types: LPCBYTE, FunctionID, ClassID, mdToken, GUIDs, ILNativeMap, COR_PRF flags
  ProfilerInfo.h            # ProfilerInfo class — ICorProfilerInfo vtable wrapper (calls by slot index)
  MixDbgProfiler.h          # MixDbgProfiler class declaration — ICorProfilerCallback2 (75 virtuals in vtable order)
  MixDbgProfiler.cpp         # MixDbgProfiler implementation — Initialize, Shutdown, JITCompilationFinished, OnFunctionEnter
  FunctionCallbacks.cpp      # extern "C" callbacks — FunctionIDMapper, FunctionEnterImpl, FunctionLeaveImpl, FunctionTailcallImpl
  ClassFactory.h             # ClassFactory class declaration
  ClassFactory.cpp           # ClassFactory implementation — COM class factory creating MixDbgProfiler instances
  DllExports.cpp             # DllGetClassObject + DllCanUnloadNow — COM DLL entry points
  EnterLeaveStubs.asm        # x64 MASM naked stubs — save/restore volatile regs around enter/leave/tailcall hooks
  MixDbgProfiler.def         # Module definition — exports DllGetClassObject, DllCanUnloadNow
  MixDbgProfiler.vcxproj     # MSBuild project (v145 toolset, x64, DynamicLibrary)
  Makefile                   # Build shortcut
```

## Critical Implementation Details

### Vtable Layout (DO NOT REORDER)

`MixDbgProfiler` derives from `IUnknown` and declares all `ICorProfilerCallback` (67 methods, slots 3-69) and `ICorProfilerCallback2` (8 methods, slots 70-77) virtual methods in exact `corprof.idl` order. MSVC single-inheritance places them in declaration order in the vtable. The CLR calls these by slot index — **reordering, adding, or removing virtual methods will silently break the profiler**.

The no-op stubs (`{ return S_OK; }`) are inline in the header. Only `Initialize`, `Shutdown`, and `JITCompilationFinished` have real implementations in `.cpp`.

Non-virtual public methods (`IsWatchedMethod`, `OnFunctionEnter`, `RegisterWatchedFunction`, etc.) have no vtable impact and can be freely added/reordered.

### ProfilerInfo Vtable Slot Indices

`ProfilerInfo` calls `ICorProfilerInfo` methods by raw vtable slot index (no corprof.h header). Slot numbers counted from vtable start (0=QI, 1=AddRef, 2=Release, then ICorProfilerInfo at slot 3+):

| Slot | Method | Used For |
|------|--------|----------|
| 5 | `GetCodeInfo` | Native code address + size after JIT |
| 15 | `GetFunctionInfo` | Metadata token + module ID |
| 16 | `SetEventMask` | Enable/disable JIT + enter/leave notifications |
| 17 | `SetEnterLeaveFunctionHooks` | Register enter/leave/tailcall hook functions |
| 18 | `SetFunctionIDMapper` | Register function ID mapper callback |
| 20 | `GetModuleInfo` | Module file path (for assembly name extraction) |
| 35 | `GetILToNativeMapping` | IL offset → native offset mapping (exact-line BPs) |

### Named Pipe Protocol

Text lines, one per notification. Two formats depending on `m_hooksActive`:

**With hooks** (`MIXDBG_WATCH_TOKENS` or `MIXDBG_WATCH_ASSEMBLIES` set):
- `JIT:<token_hex>:<addr_hex>:<size_hex>:<assembly>[:<IL0=N0,IL1=N1,...>]\n`
- `ENTER:<token_hex>:<body_addr_hex>:<thread_id_hex>:<assembly>\n`

**Without hooks** (JIT notifications only):
- `<token_hex>:<addr_hex>:<size_hex>:<assembly>\n`

### Synchronization

- `MIXDBG_ACK_EVENT` — MixDbg signals after setting hardware BP. Profiler's `OnFunctionEnter` blocks on this (500ms timeout).
- `MIXDBG_REHOOK_EVENT` — MixDbg signals on Continue. Rehook watcher thread re-enables `COR_PRF_MONITOR_ENTERLEAVE`.
- `m_pipeLock` (CRITICAL_SECTION) — protects `WriteToPipe` from concurrent access (main thread JIT + enter hook thread).
- `m_funcLock` (CRITICAL_SECTION) — protects `m_funcSlots` (written by `FunctionIDMapper`, read by `OnFunctionEnter`).

### Thread Model

- **CLR JIT thread**: calls `JITCompilationFinished`. Writes to pipe, optionally blocks on ACK (non-hook mode only for watched methods).
- **Application threads**: call `FunctionEnterNaked` → `FunctionEnterImpl` → `OnFunctionEnter`. Disables hooks, writes ENTER to pipe, blocks on ACK.
- **Rehook watcher thread**: created in `Initialize`. Loops on `WaitForSingleObject(m_hRehookEvent)`, re-enables enter/leave hooks via `SetEventMask`.

### Environment Variables (set by MixDbg before CreateProcess)

| Variable | Format | Purpose |
|----------|--------|---------|
| `CORECLR_ENABLE_PROFILING` | `1` | Tells CLR to load a profiler |
| `CORECLR_PROFILER` | `{D13D53A1-...}` | Profiler CLSID |
| `CORECLR_PROFILER_PATH` | Path to DLL | Profiler DLL location |
| `MIXDBG_PIPE_NAME` | `\\.\pipe\MixDbg_<pid>` | Named pipe for notifications |
| `MIXDBG_ACK_EVENT` | Event name | ACK synchronization event |
| `MIXDBG_REHOOK_EVENT` | Event name | Rehook synchronization event |
| `MIXDBG_WATCH_TOKENS` | `Asm1:Token1,Asm2:Token2,...` | Exact methods to hook (C#) |
| `MIXDBG_WATCH_ASSEMBLIES` | `Asm1,Asm2,...` | Assemblies to hook entirely (C++/CLI) |

### Assembly Stubs (EnterLeaveStubs.asm)

x64 MASM naked functions that the CLR calls directly. They save all volatile registers (7 integer + 6 XMM = 152 bytes), call the C++ implementation with FunctionID in `rcx`, then restore everything. Required because the CLR does NOT save volatile registers before calling enter/leave hooks.

Stack alignment: entry RSP ≡ 8 mod 16. After 7 pushes (56 bytes) RSP ≡ 0 mod 16. Sub 0x80 (128 bytes) keeps alignment. RSP ≡ 0 mod 16 before `call` — correct per x64 ABI.
