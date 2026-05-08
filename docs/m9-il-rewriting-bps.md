# M9: BP-at-Line IL Rewriting (Plan)

> Replaces the M4V3 ENTER/LEAVE + hardware-BP scheme with direct IL injection
> at every breakpointed line. Removes the 4-BP cap permanently, unifies launch
> and attach modes, and lets a lot of M4/M7 plumbing be deleted.
>
> Background: see [`il-rewriting-explanation.md`](il-rewriting-explanation.md)
> for the conceptual "why."

## Context

Today, mixdbg installs managed BPs as hardware breakpoints (DR0–DR3, 4 total),
scoped to method lifetime via the CLR's ENTER/LEAVE profiler hooks. This works
in launch mode but has two structural problems:

1. **Attach mode can't use ENTER/LEAVE** (CLR rejects
   `COR_PRF_MONITOR_ENTERLEAVE` for attached profilers). M7 ships with a
   degraded eager-HW-BP path subject to the 4-concurrent cap.
2. **Method-lifetime scoping carries a lot of accidental complexity** —
   activation counting, the ACK event, the install-on-ENTER /
   uninstall-on-LEAVE state machine, the deferred-LEAVE handling for tiny
   methods, the JIT-time HW-BP install vs ENTER-time install split.

M9 replaces all of that with a single mechanism: rewrite the IL of any method
that has BPs, injecting a `MixDbgHelper.HitBreakpoint(token, line)` P/Invoke
call at each breakpointed IL offset. The helper notifies mixdbg, mixdbg sends
a DAP `stopped` event, the helper blocks until the user continues. No
hardware breakpoints, no ENTER/LEAVE bookkeeping, unlimited BPs, identical
behavior in launch and attach.

## Architecture

### Wire format change

The notification pipe gains a single new message type, replacing the existing
`ENTER:` / `LEAVE:` / `JIT:` BP-driving messages for managed BPs:

```
HIT:<token_hex>:<il_offset_hex>:<thread_id_hex>:<assembly>\n
```

`HIT` is sent by `MixDbgHelper_HitBreakpoint` (new export, same shape as the
existing `MixDbgHelper_Enter`/`Leave`). It blocks on an ACK event until the
user resumes — same pattern that ENTER currently uses, but now per-BP rather
than per-method-entry.

`JIT:` notifications stay (still useful for stack-trace resolution); their BP
side effects go away.

### Helper signatures (P/Invoke targets)

Two new exports added alongside the existing helpers:

```cpp
// Called at every BP injection site. Blocks until ACK.
__declspec(dllexport) void __stdcall MixDbgHelper_HitBreakpoint(
    unsigned int methodToken, unsigned int ilOffset);

// Conditional BP variant — only blocks if `condition` is non-zero.
// The IL rewriter generates the condition expression inline; the helper just
// gates the actual notification.
__declspec(dllexport) void __stdcall MixDbgHelper_HitConditional(
    unsigned int methodToken, unsigned int ilOffset, int condition);
```

Both go into `profiler/MixDbgHelper.cpp` (file already exists from M7).
`MixDbgProfiler.def` adds the two exports.

### IL transformation

For each watched method, the rewriter walks the IL and emits, at each
breakpointed IL offset, a 3-instruction sequence prepended to the original
instruction at that offset:

```
ldc.i4    <method_token>
ldc.i4    <il_offset>
call      void Module.MixDbgHelper::HitBreakpoint(int32, int32)
<original instruction at this offset>
<rest of IL ...>
```

Multiple BPs in the same method = multiple injected triples, one per offset.
No try/finally wrapping, no `ret` rewriting, no exception-clause shifts —
those were specific to the ENTER/LEAVE scheme.

### Branch-offset recomputation

Inserting code shifts every byte after the injection point. So every branch
instruction (`br`, `br.s`, conditional `brfalse`/`brtrue`, etc.) and every
exception-clause offset must be adjusted by the cumulative inserted-byte count
at its target. Standard IL-rewriter mechanics:

1. Walk the original IL, collecting the offset of every instruction.
2. For each injection site, record `(injection_offset, inserted_bytes)`.
3. Build a remap function `oldOffset → newOffset = oldOffset + sum of
   inserted_bytes for all injections at offsets ≤ oldOffset`.
4. Walk again, copying the original IL into the new buffer, applying the
   triple injection at each site, and rewriting every branch's target offset
   through the remap.
5. Walk the original exception-clause table, applying the remap to TryOffset,
   TryLength, HandlerOffset, HandlerLength, FilterOffset.
6. Branch-form widening: if any short-form branch's new target distance
   exceeds 127 bytes, widen to long form. Must be done in a fixed-point loop
   because widening shifts other branches.

### Helper P/Invoke registration

Same approach M7's stub already plans for the ENTER/LEAVE rewriter — emit a
`Module.MixDbgHelper` TypeDef + two pinvoke methoddefs (`HitBreakpoint`,
`HitConditional`) into each watched module's metadata via `IMetaDataEmit2`.
Pointing at `MixDbgProfiler.dll` exports. Cache the resulting
`mdMethodDef` tokens per module so subsequent rewrites reuse them.

### ReJIT vs initial JIT

- **Method JIT'd after the profiler attaches** (or in launch mode): the
  rewriter is invoked from `JITCompilationStarted` via the standard
  `GetReJITParameters` path; the runtime hands us the original IL, we hand
  back the rewritten body.
- **Method already JIT'd at attach time**: call
  `ICorProfilerInfo4::RequestReJITWithInliners(0, 1, &moduleId, &token)` to
  force recompilation. The runtime then calls `GetReJITParameters` and the
  flow merges with the above.

### Mid-session BPs

When the user adds a BP mid-session:

1. PDB lookup → `(methodToken, assembly, ilOffset)`. Already implemented in
   `IPdbSourceMapper.GetMethodSequencePoints`.
2. If the method is already in our `WatchedMethods` registry → call
   `RequestReJIT` with the new BP set; the next `GetReJITParameters` emits
   the additional injection.
3. If not yet JIT'd → just register; `JITCompilationStarted` will pick it up.

When a BP is removed: same flow but the rewriter omits that injection. Setting
zero BPs in a method triggers `RequestRevert` to restore the original IL.

## What gets removed

Concrete deletions enabled by M9:

- `profiler/EnterLeaveStubs.asm` — the naked x64 enter/leave stubs.
- `profiler/FunctionCallbacks.cpp` — `FunctionEnterImpl` /
  `FunctionLeaveImpl` / `FunctionTailcallImpl`, the `FunctionIDMapper`.
- `MixDbgProfiler.cpp::OnFunctionEnter` / `OnFunctionLeave` and the
  `m_funcSlots` / `RegisterWatchedFunction` / `FindWatchedFunction`
  machinery.
- `MixDbgProfiler.cpp::JITInlining` (the inline-blocking dance — no longer
  matters since the inlined copy is rewritten too via ReJIT).
- `m_hooksActive` flag and the dual-format JITCompilationFinished branches.
- `MixDbgHelper_Enter` / `MixDbgHelper_Leave` exports (replaced by
  `HitBreakpoint`).
- `NativeDebuggerModel.ManagedBpPlans` / `ActiveMethodBreakpoints` /
  `MethodBreakpointSite` / `ManagedMethodBreakpointPlan` — the entire
  activation-counting model.
- `NativeDebuggerModel.JitMethodMappings` (still wanted for stack traces,
  but BP-side use goes away).
- `NativeDebuggerModel.IsRejitMode` flag (M7 added — no longer needed when
  attach and launch share the same path).
- `ProfilerPipeService.ParseEnterNotification` /
  `ParseLeaveOrTailcallNotification`.
- `ManagedBreakpointResolverService.HandleEnter` / `HandleLeaveOrTailcall` /
  `InstallEagerHardwareBp` / `FoldJitIntoPlans` /
  `ProcessProfilerNotifications`'s ENTER/LEAVE/TAILCALL switch arms.
- `ManagedBreakpointService.SetManagedCodeBreakpoint`'s hardware-BP
  installation path (managed BPs no longer use HW BPs at all).

`ProfilerNotification` enum collapses to just `JitNotification` (for stack
traces) + `HitBreakpointNotification` (new).

What stays:

- The diagnostic-IPC `AttachProfiler` path (M7) — still needed to load the
  profiler into a running process.
- `ProfilerClientDataBuilder` and the IPC client.
- Native breakpoints via dbgeng — completely unaffected.
- Stepping (M6) — unaffected.
- PDB resolution — unaffected.
- Stack-trace resolution via `JitMethodMap` — unaffected.

## File-by-file outline

### New files

- `profiler/IlRewriter.h` / `IlRewriter.cpp` — the bytecode walker / branch
  fixer / rewriter. Estimated ~600 lines.
- `profiler/MetadataHelpers.cpp` — encapsulates the `IMetaDataEmit2` calls
  for defining `MixDbgHelper` TypeDef + pinvoke methoddefs. ~150 lines.
- `src/Models/HitBreakpointNotification.cs` — replaces the union of
  EnterNotification / LeaveNotification / TailcallNotification.

### Modified files (profiler side)

- `profiler/MixDbgProfiler.h` — drop ENTER/LEAVE virtuals from the public
  surface (they're still in the vtable, just no-op). Add `m_rewriter`
  pointer.
- `profiler/MixDbgProfiler.cpp` — `Initialize` / `InitializeForAttach` set
  event mask to just `COR_PRF_MONITOR_JIT_COMPILATION | COR_PRF_ENABLE_REJIT`
  (drop ENTERLEAVE entirely). `JITCompilationStarted` calls into the
  rewriter. `GetReJITParameters` calls into the rewriter. `CmdReaderLoop`
  WATCH command requests ReJIT for already-JIT'd methods.
- `profiler/MixDbgHelper.cpp` — replace `Enter`/`Leave` exports with
  `HitBreakpoint` (and optionally `HitConditional`).
- `profiler/MixDbgProfiler.def` — update exports.

### Modified files (mixdbg side)

- `src/Services/ManagedBreakpointResolverService.cs` — collapse to "PDB
  resolve, send WATCH with IL offsets list, wait for HIT notification."
- `src/Services/ManagedBreakpointService.cs` — `SetManagedCodeBreakpoint`
  becomes a no-op for managed BPs (rewriter handles installation).
- `src/Services/ProfilerPipeService.cs` — drop ENTER/LEAVE/TAILCALL parsing,
  add HIT parsing.
- `src/Models/NativeDebuggerModel.cs` — delete the listed fields.
- `src/Services/Interfaces/IPdbSourceMapper.cs` — already exposes everything
  we need; no API change.

### Tests

- Unit tests for the IL rewriter (in C++ — link against the rewriter as a
  library; feed canned IL inputs, assert canned IL outputs):
  - Single BP, single `ret`, no exception handlers
  - Multiple BPs in one method
  - BPs that span exception-clause boundaries
  - Methods with `try/catch`/`try/finally`/`try/fault`
  - Generic methods
  - Methods with `switch` tables
  - Methods that need short→long branch widening
  - Methods that throw without returning
- Integration tests:
  - Existing managed BP integration tests still pass (renamed/cleaned where
    they assert ENTER/LEAVE-specific behavior — most should be agnostic).
  - New test: 10 BPs in a single method (impossible today, fundamental for M9).
  - Conditional BP test (proves the in-process condition path works).
  - Attach test from M7 still passes (and now exercises unlimited BPs).

## Phased rollout

M9 is a replacement, not an addition — running both schemes in parallel is
not worth the maintenance cost. But the implementation can be staged so each
phase ships a buildable, test-passing tree:

### Phase 1: rewriter foundation

- Add `IlRewriter` + `MetadataHelpers` as a standalone library inside the
  profiler. Wire it up to `GetReJITParameters` but **don't** call
  `RequestReJIT` from anywhere yet. The rewriter is dead code until phase 2.
- Add unit tests for the rewriter against canned IL inputs.
- Existing managed BPs continue working unchanged (still using HW BPs +
  ENTER/LEAVE).
- Ship as a single commit: "Add IL rewriter library (no behavior change)."

### Phase 2: opt-in rewriter via env var

- Behind an env var `MIXDBG_USE_IL_REWRITING=1`, switch managed BPs to the
  new HIT-based path. CmdReaderLoop calls `RequestReJIT` for watched methods.
- Both schemes coexist in code; only one is active per session.
- Run integration tests in both modes (env var on / off) to flush out
  regressions.
- Ship as: "Add opt-in IL-rewriting BP path under MIXDBG_USE_IL_REWRITING."

### Phase 3: switch over

- Make IL rewriting the default. Env var becomes a fallback to the legacy
  path (for one release).
- All integration tests run against the new path.
- Ship as: "Switch managed BPs to IL rewriting by default."

### Phase 4: delete the legacy path

- Remove the ENTER/LEAVE plumbing, HW-BP install path for managed BPs, and
  the bypass env var.
- The deletions in "What gets removed" above happen in this commit.
- Ship as: "Remove legacy ENTER/LEAVE managed-BP path."

Each phase leaves a working tree with all tests passing. Phase 1 is the only
phase that's safe to do in isolation without follow-up; phases 2–4 should
land in close succession to avoid prolonged dual-maintenance.

## Risks & open questions

- **Performance overhead.** Every HIT call is a managed→native P/Invoke
  transition (~tens of ns). For BPs in hot loops this is measurable but
  acceptable in practice — debugger usability dominates over instrumented-run
  speed. If it bites, the conditional-BP path can include a fast pre-check
  that skips the helper call entirely when no debugger is attached.

- **Methods that can't be ReJIT'd.** A handful of corner-case methods can't
  ReJIT (PInvokes, methods with stackwalk-related attributes). For those
  we'd fall back to the M4 hardware-BP path. Detection: check
  `RequestReJIT`'s HRESULT and route to the legacy installer on failure. A
  long tail of edge cases lives here — the rewriter design must allow
  per-method fallback, not assume rewriting always works.

- **Tiered compilation interactions.** Tier-0 and Tier-1 versions of a method
  are compiled separately. ReJIT applies to all tiers. We need to verify
  that promotion from Tier-0 to Tier-1 doesn't lose our injected helper
  calls (it shouldn't — ReJIT body is the source of truth — but
  worth confirming with a test).

- **Inlined methods.** If method `A` is inlined into method `B`, the BP we
  inject into `A` won't fire when `B` runs the inlined copy. Two options:
  (a) `RequestReJITWithInliners` to also rejit every inliner of `A`; (b)
  block inlining of any method we've watched (today's `JITInlining`
  approach). (a) is more thorough; (b) is what M4 does. Decide during
  Phase 1.

- **Step-into across the helper call.** When the user steps over a line that
  has our injected helper call, the user must not visibly step into the
  helper. Stepping is currently driven by native-level temporary BPs in
  `SteppingService` — should "just work" because the helper call returns to
  the user line and the temp BP is at the next user line. Worth a
  step-integration test.

- **Variable inspection at the BP.** When stopped inside the helper waiting
  for ACK, the user expects to inspect locals of the *user* method, not the
  helper. The current managed variable inspection (M5) walks the managed
  stack via SOS; the helper frame should be one frame down, so the user's
  frame is at index 1 not 0. May need a small adjustment in
  `EngineQueryService` to skip helper frames in stack traces shown to the
  user.

- **Managed code path through the helper requires the profiler DLL to be
  loadable from managed code.** It already is (P/Invoke via DllImport works
  for any native DLL on PATH, and the CLR holds the profiler DLL pinned for
  its lifetime).

## Verification

- `make all -C profiler` produces `MixDbgProfiler.dll` with the new exports.
- `dotnet build src/MixDbg.csproj -c Debug` clean.
- `dotnet test test/UnitTests` all pass — including the new rewriter tests
  exercised via a managed test harness that calls the rewriter through a
  thin C export shim.
- `dotnet test test/IntegrationTests` all pass, including the new
  10-BPs-in-one-method test that proves the unlimited-BPs property.
- Manual smoke: attach to a running .NET app from nvim-dap, set 5+ BPs in
  one method, verify all fire on the next call.

## Estimated effort

- Phase 1 (rewriter library + unit tests): 4–6 days
- Phase 2 (opt-in wiring + integration tests): 1–2 days
- Phase 3 (switch default): 1 day
- Phase 4 (cleanup): 1 day

Total: ~1.5 weeks of focused work. The bulk is Phase 1 — the rewriter itself
is the hard part. The mixdbg-side rewiring is mostly deletion.

## Critical files

- `profiler/IlRewriter.{h,cpp}` (new)
- `profiler/MetadataHelpers.cpp` (new)
- `profiler/MixDbgProfiler.cpp`
- `profiler/MixDbgProfiler.h`
- `profiler/MixDbgHelper.cpp`
- `profiler/MixDbgProfiler.def`
- `profiler/MixDbgProfiler.vcxproj`
- `src/Services/ManagedBreakpointResolverService.cs`
- `src/Services/ManagedBreakpointService.cs`
- `src/Services/ProfilerPipeService.cs`
- `src/Models/NativeDebuggerModel.cs`
- `docs/architecture.md` — major rewrite of the "Method-Lifetime Managed
  Breakpoints (M4V3)" section.
- `CLAUDE.md` — M9 status flips DONE; M4V3 description rewritten.
