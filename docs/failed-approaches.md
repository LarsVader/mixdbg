# Failed Approaches & Lessons Learned

Hard-won findings from M4 (managed breakpoints) and M5 (managed variables). These document why certain seemingly-viable approaches don't work with a dbgeng-piggybacked ICorDebug V4 process, so future work doesn't repeat the investigation.

## Managed Breakpoints (M4, 2026-04-04)

The core constraint: ICorDebug's `CreateBreakpoint` returns `E_NOTIMPL` on the piggybacked (inspection-only) process. Six alternatives were tried before the CLR Profiler approach succeeded.

### Approach 1: ICorDebug NativeCode + Hardware BPs
Use piggybacked ICorDebug to get `function.NativeCode` → IL-to-native mapping → hardware BP.

**Failed:** `CORDBG_E_FUNCTION_NOT_IL` — the piggybacked ICorDebug cannot access JIT compilation state.

### Approach 2: GetOffsetByLine + Code Breakpoints (INT3)
Use dbgeng's `GetOffsetByLine(line, file)` → set INT3 breakpoint.

**Failed:** `GetOffsetByLine` returns addresses in **zero-filled pre-allocated code buffers** — not actual JIT'd code. INT3 at these addresses crashes the process (`0x800703E6`, `ERROR_PARTIAL_COPY`) by corrupting CLR data structures.

### Approach 3: GetOffsetByLine + Hardware BPs
Same address from `GetOffsetByLine`, but hardware BPs instead of INT3.

**Failed:** Hardware BPs don't crash but never fire — the address is a placeholder (zeros), not where the CPU executes.

### Approach 4: dbgeng `bu` (Deferred Breakpoint)
`bu \`file.cs:line\`` — deferred breakpoint that auto-resolves.

**Failed:** Crashes the process (`0x800703E6`). dbgeng deferred breakpoints with managed source paths cause memory access errors during resolution.

### Approach 5: SOS `!bpmd`
Load SOS extension, use `!bpmd` for CLR-aware breakpoints.

**Failed:** Two blockers: (1) .NET 10 doesn't ship `sos.dll` alongside `coreclr.dll`; (2) dbgeng's SECURE policy blocks loading extension DLLs entirely.

### Approach 6: DAC (XCLRDataProcess) + Hardware BPs
Use DAC to find real JIT'd native entry point via `GetMethodDefinitionByToken` → `EnumInstance` → `GetRepresentativeEntryAddress`.

**Partially worked:** Returns correct JIT'd code address, hardware BPs fire. **But** `StartEnumInstances` consistently returns `S_FALSE` (no JIT instances) even after methods are JIT'd. The DAC created via `CLRDataCreateInstance` with an external data target can read static metadata (modules, method defs) but **cannot detect JIT compilation instances**. This makes it unusable for real-time breakpoint resolution.

### Other M4 Findings

- **Token collision across assemblies:** Method tokens (e.g., `0x0600000E`) exist in many loaded assemblies. Without filtering by assembly name, the DAC finds instances in framework assemblies instead of the target. Always pair tokens with assembly names.
- **CLR notification exceptions (`0xe0444143`):** Fire for various CLR events but NOT for individual method JIT compilations. Cannot detect when a specific method is JIT'd.
- **Polling SetInterrupt trade-off:** 200ms polling starves WPF UI thread (DispatcherTimer never fires). 2s polling is viable but adds latency. The profiler approach eliminated polling entirely.
- **`DOTNET_TieredCompilation=0` + `DOTNET_ReadyToRun=0`:** Ensures stable native addresses (no tier recompilation, no R2R). Methods are still lazily JIT'd — these env vars do NOT force eager JIT.
- **Neutered ICorDebug modules:** After `ProcessStateChanged(FLUSH_ALL)`, old `CorDebugModule` objects are neutered. `EnumerateModules` must always overwrite stored references, not skip known ones.

### M4V2 Transient-on-Continue Lifecycle (Superseded by M4V3)

The initial profiler design (M4V2) installed a hardware BP on `FunctionEnter`, then removed it on `Continue` and re-enabled hooks via a rehook event. This assumed "one ENTER per BP hit."

That assumption fails for any method that runs through more than one BP during a single activation — the canonical case being a loop:

```csharp
foreach (Item item in items) {
    DoSomething(item);   // BP here
}
```

The BP fires on iteration 1. User continues. MixDbg removes the HW BP and re-enables hooks, but the method is still executing — `FunctionEnter` doesn't fire again until the method is called again. Iterations 2..N run with no BP.

M4V3 replaced this with method-lifetime scoping: BPs stay installed for the entire activation (ENTER to LEAVE), so loop iterations all fire correctly. The rehook watcher thread, "transient vs permanent" BP distinction, and `PermanentManagedBreakpointIds` were all eliminated.

### Profiler Implementation Discoveries

These were found during the successful CLR Profiler implementation:

- **Hardware BPs at the method entry point don't fire when enter/leave hooks are active** — the CLR redirects entry through hook trampolines, bypassing the entry address. However, HW BPs at addresses **inside the method body** (obtained via `GetILToNativeMapping`) fire correctly with hooks active. The M4V2 design worked around this by disabling hooks before each call; M4V3 eliminated the workaround entirely by always setting BPs at IL-mapped body addresses, never at method entry.
- **Hardware execution BPs (`ba e1`) fire as `EXCEPTION_SINGLE_STEP` (`0x80000004`) on x64**, not through the `IDebugEventCallbacks.Breakpoint` callback. Must handle in the Exception callback path.
- **x64 MASM stubs must preserve all volatile registers** (rax, rcx, rdx, r8-r11, xmm0-5) with correct 16-byte stack alignment.
- **`GetILToNativeMapping`** provides the offset past any hook preamble — essential for exact-line BPs.
- **Reverse IL mapping** (native IP → IL offset → PDB line) enables accurate stack trace line numbers for managed frames.

## Managed Variable Inspection (M5, 2026-04-12)

### ICorDebug V4 Thread Enumeration Fails

The planned approach (walk `Process.Threads` → chains → frames → `ICorDebugILFrame` → `EnumerateLocalVariables()`) was fully implemented but fails at runtime. The piggybacked ICorDebug V4 process (created via `OpenVirtualProcessImpl` with `DbgEngDataTarget` bridge) **cannot enumerate threads or stack frames**. The data target bridge maps memory reads, but V4 piggybacked mode doesn't support the full `ICorDebugProcess` threading API.

The exact COM error has shifted over time as the surrounding code evolved: early implementations surfaced `CORDBG_E_READVIRTUAL_FAILURE` from `Process.Threads`, later iterations (after work-arounds for the data-target read path) hit `E_NOTIMPL` on `EnumerateChains`/`EnumerateFrames` instead. Same root cause, different call surface. This is the same fundamental limitation that forced M4 to use the CLR profiler. The entire ICorDebug locals path was removed in 2026-05; see `docs/architecture.md` for the current SOS-only path.

### Alternatives Considered

- **DAC (`SOSDacInterface`):** The ClrDebug NuGet exposes module/method-level DAC APIs but not stack frame or local variable APIs. No `GetStackReferences`, no frame-level locals.
- **CLR Profiler (`ICorProfilerInfo2::DoStackSnapshot`):** Could walk the managed stack from inside the target, but there's no profiler API for local variable stack offsets. The JIT's local layout is in GC info (undocumented, version-dependent).
- **SOS via dbgeng (`!clrstack -a`):** Works because SOS reads memory directly via the data target, bypassing ICorDebug threading. Text output parsing is fragile but the infrastructure (`ExecuteCommand` + `OutputCapture`) already existed.

### Chosen: SOS via dbgeng

`!clrstack -a` with output capture. The DAC (already loaded) has all GC info parsing for local variable stack layout. Fragile but functional.

## Assembly-Level Profiler Watches (MIXDBG_WATCH_ASSEMBLIES) (2026-05-01)

The profiler's `FunctionIDMapper` was originally configured to hook **all methods** from watched assemblies (`MIXDBG_WATCH_ASSEMBLIES`). The intent was to ensure ENTER/LEAVE notifications for any C++/CLI method that might later need a breakpoint.

**Failed at scale:** In large C++/CLI assemblies (e.g., DentalBaseDotNet with thousands of methods):

1. **Registration overflow:** `FunctionIDMapper` set `*pbHookFunction = TRUE` for every method in the assembly, filling the 64-slot `m_funcSlots` registration table with random methods. When the actual breakpoint target was JIT'd, `RegisterWatchedFunction` silently failed — the ENTER notification was never sent and the hardware breakpoint was never installed.

2. **ENTER storm:** Every registered method's ENTER notification interrupted the debug engine (9,493 interrupts observed in a real session). Each interrupt required the engine to stop, drain the notification queue, find no matching BP plan, ACK, and resume — making the debugger completely unresponsive.

3. **Unnecessary inlining suppression:** `JITInlining` blocked inlining for all methods in watched assemblies, degrading JIT optimization for code that had no breakpoints.

**Fix:** Only hook methods with exact WATCH tokens (specific method token match). Assembly-level watches were removed entirely — the profiler no longer parses `MIXDBG_WATCH_ASSEMBLIES`. JIT notifications (from `JITCompilationFinished`) are unaffected and still fire for all methods. The MixDbg side also fast-paths ENTER notifications for methods without BP plans, ACKing immediately without interrupting the engine.

## Summary

The fundamental limitation: **ICorDebug V4 piggybacked on dbgeng is inspection-only** — it can read metadata and memory but cannot set breakpoints, enumerate threads reliably, or access JIT state. Any approach requiring ICorDebug runtime operations will fail. The working paths are:

1. **CLR Profiler** for real-time JIT and method enter/leave notifications (runs in-process)
2. **SOS via dbgeng** for stack-local variable inspection (reads memory directly)
3. **Hardware BPs (`ba e1`)** for breakpoints at JIT'd native addresses (set via dbgeng)
