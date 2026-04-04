# M4V2: Managed Breakpoints — Investigation & Findings

## The Problem

With dbgeng as the native debugger, ICorDebug's `CreateBreakpoint` returns E_NOTIMPL on the piggybacked (inspection-only) process. We need an alternative way to set breakpoints on managed (C#) methods.

## What We Tried (M4V2 Session, 2026-04-04)

### Approach 1: ICorDebug NativeCode + Hardware Breakpoints

**Idea:** Use piggybacked ICorDebug to get `function.NativeCode` → IL-to-native mapping → set hardware BP (`ba e1`) at the native address.

**Result:** `CORDBG_E_FUNCTION_NOT_IL` — the piggybacked ICorDebug is inspection-only and cannot access JIT compilation state. `NativeCode` property is not available.

### Approach 2: GetOffsetByLine + Code Breakpoints

**Idea:** Use dbgeng's `GetOffsetByLine(line, file)` to resolve managed source lines to native addresses, then set INT3 (code) breakpoints.

**Result:** `GetOffsetByLine` returns addresses in **zero-filled pre-allocated code buffers** — not actual JIT'd code. Setting INT3 at these addresses crashes the process with `0x800703E6` (`ERROR_PARTIAL_COPY`) because writing INT3 to managed memory corrupts CLR data structures.

**Key finding from disassembly:**
```
WpfApp!Method.#C: 0000 add byte ptr [rax],al  (all zeros — empty buffer)
```

### Approach 3: GetOffsetByLine + Hardware Breakpoints

**Idea:** Same address from `GetOffsetByLine`, but use hardware breakpoints (CPU debug registers) instead of INT3 to avoid corrupting memory.

**Result:** Hardware BPs don't crash but never fire. The address is a placeholder (zeros), not where the CPU actually executes code.

### Approach 4: dbgeng `bu` (Deferred Breakpoint)

**Idea:** Use `bu \`file.cs:line\`` to set a deferred breakpoint that auto-resolves when the symbol becomes available.

**Result:** Crashes the process (`0x800703E6`). dbgeng's deferred breakpoint with managed source file paths causes a memory access error during resolution.

### Approach 5: SOS `!bpmd`

**Idea:** Load the SOS debugger extension and use `!bpmd WpfApp WpfApp.MainWindow.OnAddClick` to set a CLR-aware breakpoint.

**Result:** Two blockers:
1. .NET 10 doesn't ship `sos.dll` alongside `coreclr.dll` (only .NET Core 2.x does)
2. dbgeng's SECURE policy blocks loading extension DLLs entirely

### Approach 6: DAC (XCLRDataProcess) + Hardware Breakpoints (WORKS!)

**Idea:** Use the CLR's Data Access Component (DAC, `mscordaccore.dll`) to find the **real JIT'd native code entry point** via `XCLRDataProcess` → `GetMethodDefinitionByToken` → `EnumInstance` → `GetRepresentativeEntryAddress`. Set hardware BP at that address.

**Result:** This works! The DAC returns the correct JIT'd code address (in the `0x7FF7...` range), and hardware BPs at that address fire correctly.

**BUT:** The DAC has a ~12 second cache staleness issue. After a method is JIT'd, the DAC doesn't detect the JIT'd code for ~12 seconds despite being recreated each poll. This means:
- **Second-call breakpoints work reliably** (method JIT'd on first call, BP set before second call)
- **First-call breakpoints are unreliable** (BP must be set between JIT and first instruction, but DAC detection is too slow)

### Why the DAC is Slow

`mscordaccore.dll` is loaded once via `NativeLibrary.Load`. Subsequent loads return the **same DLL handle** (already loaded). The DAC has **global state inside the DLL** that caches the CLR's data structures. Our first poll populates this cache with "method not JIT'd". Even when we create a new `XCLRDataProcess` via `CLRDataCreateInstance`, the underlying DLL reuses its cached global state. The cache takes ~12 seconds to become stale/refresh.

### Other Findings

- **EnumerateModules NEUTERED fix:** After `ProcessStateChanged(FLUSH_ALL)`, old `CorDebugModule` objects are neutered. `EnumerateModules` must always overwrite stored module references, not skip already-known ones.

- **Token collision:** Method tokens (e.g., `0x0600000E`) exist in many loaded assemblies. Without filtering by assembly name, the DAC may find a JIT instance in a framework assembly (e.g., `Microsoft.Win32.Registry`) instead of the target assembly (`WpfApp`). Fixed by storing the assembly name in `DeferredManagedBreakpoint` and filtering the XCLRData module enumeration.

- **CLR notification exceptions (0xe0444143):** These fire for various CLR events but NOT for individual method JIT compilations. Cannot be used to detect when a specific method is JIT'd.

- **Polling interval trade-off:** 200ms `SetInterrupt` polling starves the WPF UI thread (DispatcherTimer-based auto-test never fires). 2s polling gives the UI enough time but adds latency.

- **DOTNET_TieredCompilation=0 + DOTNET_ReadyToRun=0:** Set before `CreateProcess` so the child inherits them. Ensures stable native addresses (no tier-0→tier-1 recompilation, no R2R). Methods are still lazily JIT'd on first call — these env vars do NOT force eager JIT.

## Current State (What's Committed)

The codebase has a working pipeline:
1. PDB resolves `file:line` → method token + assembly name
2. DAC (`XCLRDataProcess`) finds the module by assembly name, gets method def by token
3. `EnumInstance` + `GetRepresentativeEntryAddress` returns the real JIT native entry point
4. Hardware BP at that address fires correctly
5. `OnBreakpoint` detects it via `ManagedBreakpointIds`

The pipeline is proven end-to-end but has the 12s DAC latency that prevents first-click breakpoints.

## Two Viable Paths Forward

### Option A: ICorDebug Launch → Force JIT → Detach → Attach dbgeng

**Concept:** Use a full ICorDebug debugger (not piggybacked) temporarily to force JIT compilation of breakpointed methods before the user interacts with the app.

**Flow:**
1. MixDbg receives DAP `launch` request with breakpoints
2. Instead of launching via dbgeng, launch via full ICorDebug (`CorDebugProcess`)
3. Process starts, CLR loads, modules load
4. For each breakpointed method: use `ICorDebugEval.CallFunction` to call `RuntimeHelpers.PrepareMethod(methodHandle)` — this forces JIT compilation without executing the method
5. Methods are now JIT'd at stable native addresses
6. Detach ICorDebug (releases the debug port)
7. Attach dbgeng to the same process (PID) — our existing `Attach(pid)` path
8. Initialize piggybacked ICorDebug + DAC
9. DAC immediately finds JIT'd native addresses (no waiting — they already exist)
10. Set hardware BPs → breakpoints work from first click

**Advantages:**
- Stays entirely in C# using ClrDebug (already a dependency)
- No new native DLLs to build or ship
- Uses APIs we already work with (ICorDebug, DAC, hardware BPs)
- The "gap" between detach and attach (~100-200ms) is acceptable — breakpoints can report as "pending" via DAP during the handoff

**Challenges:**
- `ICorDebugEval` is complex: needs a stopped thread, correct argument marshaling for `RuntimeMethodHandle`
- The launch flow changes significantly (ICorDebug launch → handoff → dbgeng attach)
- Between ICorDebug detach and dbgeng attach, the process runs freely (breakpoints don't work for ~100-200ms)
- Need to verify that the debug port is cleanly released on ICorDebug detach so dbgeng can attach

**Key implementation detail:** `RuntimeHelpers.PrepareMethod` takes a `RuntimeMethodHandle`. To get one, we'd need to call `typeof(MainWindow).GetMethod("OnAddClick").MethodHandle` via `ICorDebugEval`. This requires multiple eval calls (reflection + PrepareMethod), which adds complexity.

### Option B: CLR Profiler DLL (ICorProfilerCallback)

**Concept:** Build a small native C++ DLL that implements `ICorProfilerCallback`. The CLR loads it into the target process at startup and calls `JITCompilationFinished` for every method that gets JIT'd. The profiler sends the method token + native address to MixDbg via a named pipe.

**Flow:**
1. MixDbg sets env vars before `CreateProcess`:
   - `CORECLR_ENABLE_PROFILING=1`
   - `CORECLR_PROFILER={our-guid}`
   - `CORECLR_PROFILER_PATH=path/to/MixDbgProfiler.dll`
2. CLR loads `MixDbgProfiler.dll` into the target process at startup
3. Target process runs normally — no code modifications needed
4. When any method is JIT'd, CLR calls `ICorProfilerCallback::JITCompilationFinished(functionId, hrStatus, fIsSafeToBlock)`
5. The profiler resolves `functionId` → method token + native address
6. Writes `{token, nativeAddress}` to a named pipe
7. MixDbg reads the pipe on its engine thread, matches against deferred BPs
8. Sets hardware BP at the real native address immediately
9. First call to the method hits the BP

**Advantages:**
- Real-time JIT notifications — no polling, no DAC latency
- The CLR handles all the complexity of JIT detection
- Works with any .NET process (no target modification)
- Profiler and native debugger (dbgeng) coexist without conflicts
- No debugger handoff/reattach needed

**Challenges:**
- Requires building a native C++ DLL (separate project, separate language)
- IPC between profiler (in-process) and MixDbg (out-of-process) via named pipe
- The profiler DLL must be shipped alongside MixDbg.exe
- Profiler API versioning across .NET versions
- C++/CLI build infrastructure already exists in TestApp (CliWrapper), so this is feasible

**Key implementation detail:** The profiler DLL would be minimal (~200 lines of C++):
- Implement `ICorProfilerCallback::Initialize` (call `SetEventMask` with `COR_PRF_MONITOR_JIT_COMPILATION`)
- Implement `JITCompilationFinished` (resolve function ID, write to pipe)
- All other callbacks return `S_OK` (no-op)

Both options are viable. Option A stays in C# with existing dependencies. Option B is more robust (real-time notifications, no debugger handoff) but requires a native C++ DLL.
