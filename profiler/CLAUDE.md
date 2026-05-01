# MixDbgProfiler — CLR Profiler DLL

Native C++ DLL implementing `ICorProfilerCallback2`. Runs inside the debuggee, sends JIT / FunctionEnter / FunctionLeave / FunctionTailcall notifications to MixDbg via named pipe. See `README.md` for the full high-level explanation.

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
  MixDbgProfiler.cpp         # MixDbgProfiler implementation — Initialize, Shutdown, JITCompilationFinished, OnFunctionEnter/Leave, JITInlining, CmdReaderLoop
  FunctionCallbacks.cpp      # extern "C" callbacks — FunctionIDMapper, FunctionEnterImpl, FunctionLeaveImpl (→ OnFunctionLeave), FunctionTailcallImpl (→ OnFunctionLeave)
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

The no-op stubs (`{ return S_OK; }`) are inline in the header. Real implementations in `.cpp`:
- `Initialize` (slot 3)
- `Shutdown` (slot 4)
- `JITCompilationFinished` (slot 24)
- `JITInlining` (slot 28) — returns `*pfShouldInline = FALSE` for watched callees so hooks always fire.

Non-virtual public methods (`IsWatchedMethod`, `OnFunctionEnter`, `OnFunctionLeave`, `RegisterWatchedFunction`, etc.) have no vtable impact and can be freely added/reordered.

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

Text lines, one per notification. Format depends on `m_hooksActive`:

**Notification pipe** (`MIXDBG_PIPE_NAME`, profiler → MixDbg):

With hooks (`MIXDBG_WATCH_TOKENS` set or dynamic `WATCH:` commands received):
- `JIT:<token_hex>:<addr_hex>:<size_hex>:<assembly>[:<IL0=N0,IL1=N1,...>]\n`
- `ENTER:<token_hex>:<body_addr_hex>:<thread_id_hex>:<assembly>\n`
- `LEAVE:<token_hex>:<thread_id_hex>:<assembly>\n`
- `TAILCALL:<token_hex>:<thread_id_hex>:<assembly>\n`

Without hooks (JIT notifications only):
- `<token_hex>:<addr_hex>:<size_hex>:<assembly>\n`

IL-to-native mapping is included for ALL JIT'd methods (not just watched) so mid-session BPs on already-JIT'd methods can resolve exact line addresses.

**Command pipe** (`MIXDBG_CMD_PIPE`, MixDbg → profiler):
- `WATCH:<assembly>:<token_hex>\n` — dynamically adds a method to the watch list for mid-session breakpoints. `CmdReaderLoop` thread reads these, adds to `m_watchEntries`, and enables enter/leave hooks if not already active.

### Synchronization

- `MIXDBG_ACK_EVENT` — MixDbg signals this after installing HW BPs on the first `ENTER` of a method (activation 0→1). Profiler's `OnFunctionEnter` always blocks on this (500ms timeout), but MixDbg signals it immediately for nested/recursive ENTERs (no work to do). `OnFunctionLeave` is fire-and-forget — no ACK.
- `m_pipeLock` (CRITICAL_SECTION) — protects `WriteToPipe` from concurrent access (JIT + enter/leave hooks on arbitrary threads).
- `m_watchLock` (CRITICAL_SECTION) — protects `m_watchEntries`/`m_watchCount` (written by cmd reader thread, read by `IsWatchedMethod`).
- `m_funcLock` (CRITICAL_SECTION) — protects `m_funcSlots` (written by `FunctionIDMapper`, read by `OnFunctionEnter`/`OnFunctionLeave`).

### Thread Model

- **CLR JIT thread**: calls `JITCompilationFinished`. Writes to pipe, optionally blocks on ACK (non-hook mode only for watched methods).
- **Application threads**: call `FunctionEnterNaked` / `FunctionLeaveNaked` / `FunctionTailcallNaked` → C++ impls → `OnFunctionEnter` or `OnFunctionLeave`. Enter writes and blocks on ACK; Leave writes and returns.
- **Command reader thread**: created in `Initialize`. Reads `WATCH:` commands from command pipe (`MIXDBG_CMD_PIPE`), adds to `m_watchEntries` under `m_watchLock`, enables hooks if not already active.

### JIT Inlining

`JITInlining` (slot 28) sets `*pfShouldInline = FALSE` whenever the callee is in a watched list (exact token match or assembly match). Without this, the JIT may inline a small watched callee into its caller, and `FunctionEnter`/`FunctionLeave` hooks would never fire for the inlined copy — the BP would be invisible.

### Environment Variables (set by MixDbg before CreateProcess)

| Variable | Format | Purpose |
|----------|--------|---------|
| `CORECLR_ENABLE_PROFILING` | `1` | Tells CLR to load a profiler |
| `CORECLR_PROFILER` | `{D13D53A1-...}` | Profiler CLSID |
| `CORECLR_PROFILER_PATH` | Path to DLL | Profiler DLL location |
| `MIXDBG_PIPE_NAME` | `\\.\pipe\MixDbg_<pid>` | Named pipe for notifications |
| `MIXDBG_ACK_EVENT` | Event name | ACK synchronization event (first-entry sync only) |
| `MIXDBG_CMD_PIPE` | `\\.\pipe\MixDbgCmd_<name>` | Command pipe for dynamic WATCH commands |
| `MIXDBG_WATCH_TOKENS` | `Asm1:Token1,Asm2:Token2,...` | Exact methods to hook (C#) |
| ~~`MIXDBG_WATCH_ASSEMBLIES`~~ | ~~`Asm1,Asm2,...`~~ | Removed — caused ENTER storms in large assemblies (see `docs/failed-approaches.md`) |

### Assembly Stubs (EnterLeaveStubs.asm)

x64 MASM naked functions that the CLR calls directly. They save all volatile registers (7 integer + 6 XMM = 152 bytes), call the C++ implementation with FunctionID in `rcx`, then restore everything. Required because the CLR does NOT save volatile registers before calling enter/leave hooks.

Stack alignment: entry RSP ≡ 8 mod 16. After 7 pushes (56 bytes) RSP ≡ 0 mod 16. Sub 0x80 (128 bytes) keeps alignment. RSP ≡ 0 mod 16 before `call` — correct per x64 ABI.
