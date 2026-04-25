# Managed Stepping & Breakpoints — Architecture Reference

Full picture of how managed breakpoints and stepping interact in MixDbg (post-M4V3).

## Three Threads

- **Main thread**: reads DAP requests from stdin, dispatches to handlers.
- **Engine thread**: all dbgeng COM calls, runs `WaitForEvent` loop.
- **Profiler reader thread**: reads JIT/ENTER/LEAVE notifications from named pipe.

## The Event Loop (`EngineLoopStep`)

Every time `WaitForEvent` returns (target stopped), the loop processes in this order:

1. **ProcessProfilerNotifications** (line ~170) — drains the ENTER/LEAVE/JIT queue. ENTER installs HW BPs (count 0→1) and ACKs. LEAVE removes HW BPs (count→0). JIT matches deferred BPs. Returns true if stop was bookkeeping-only → **auto-continue Go**.
2. **DetermineStopReason** (`StepResolutionService`) — checks `ActiveManagedStep`, `HitUserBreakpoint`, `Stepping`, `PauseRequested`. For managed step temp BPs, compares RSP to `OriginStackPointer` (depth check). Returns a `StopReason` enum (Step, Breakpoint, Pause) or null for auto-continue.
3. **CheckStepLanding** (`StepResolutionService`) — for native steps: depth check (RSP < origin → re-step), same-line (re-step), closing brace / sourceless (auto-step-out).
4. **Clear StepOriginStackPointer** (line ~201) — after a step actually stops for the user.
5. **System stop** (line ~206) — if no reason, drain commands and **auto-continue Go**.

## Resume Paths

Every way execution can restart:

| Path | Caller | Sets LastContinuedBpId? | Notes |
|------|--------|------------------------|-------|
| `ExecuteContinueOnEngine` | Continue handler | Yes | BPs stay (method-lifetime) |
| `ExecuteStepOnEngine` | Next/StepIn handler | No | Records `StepOriginStackPointer` |
| `ExecuteStepOutOnEngine` | StepOut handler | No | |
| ENTER auto-continue | Event loop | No | Bookkeeping stop after BP install |
| System stop auto-continue | Event loop | No | |
| Step re-step | CheckStepLanding | No | Same line or deeper stack |
| Auto step-out | CheckStepLanding | No | Brace or sourceless |

## Method-Lifetime Managed Breakpoints (M4V3)

Managed breakpoints use hardware BPs (`ba e1`) at exact native addresses. Their lifecycle is tied to method activation:

1. **User sets BP** → `BindResolvedMethod` creates a `ManagedMethodBreakpointPlan` with `MethodBreakpointSite` entries (one per line).
2. **`FunctionEnter` (count 0→1)** → profiler sends `ENTER:token:addr:tid:asm`, blocks on ACK. MixDbg installs HW BPs for all plan sites, ACKs. Nested/recursive ENTERs just increment count and ACK immediately.
3. **`FunctionLeave`/`FunctionTailcall` (count→0)** → profiler sends `LEAVE:token:tid:asm` (fire-and-forget). MixDbg removes all HW BPs for that method.
4. **Continue/Step** → BPs stay (no removal). Loop iterations all fire correctly.
5. **User clears BP** → `ClearManagedBreakpointsForFile` removes plans, HW BPs, and tracking.

### Mid-session BPs on already-JIT'd methods

`FunctionIDMapper` is called once per function during JIT. If the method wasn't in the initial watch list, sending `WATCH` after JIT cannot retroactively enable hooks. For these methods, `BindResolvedMethod` installs the HW BP immediately — it persists until the user clears the breakpoint (no ENTER/LEAVE lifecycle).

### Piggybacking

If a BP is added while the method is already executing (has an `ActiveMethodBreakpoints` entry with count > 0), the HW BP is installed immediately on top of the existing activation.

## Managed Step Mechanism (Step-Over/Into/Out)

Managed steps don't use dbgeng's step commands. Instead:

1. **Set temp hardware BP** at target address via `SetManagedStepBreakpoint`.
2. **Track temp BP IDs** in `model.ActiveManagedStep.TempBreakpointIds`.
3. **Record origin RSP** in `model.ActiveManagedStep.OriginStackPointer`.
4. **Go** — resume execution.
5. When temp BP fires: `DetermineStopReason` sees `ActiveManagedStep != null` + `HitUserBreakpoint` → checks `TempBreakpointIds.Contains(LastHitBpId)` + RSP depth → returns "step".
6. `CompleteManagedStep` removes all temp BPs.

## Step-Into Special Case (C# → C++/CLI)

When stepping into a C++/CLI method:

1. `TrySetStepIntoBpViaProfiler` creates a one-shot `MethodBreakpointSite` (`IsStepIntoOneShot = true`) on the target method's plan.
2. Sends `WATCH` command to profiler.
3. On next `FunctionEnter`, the plan's one-shot site installs a HW BP; hit triggers step complete.
4. `RemoveStepIntoOneShotSites` cleans up the HW BP and plan site on completion so it doesn't interfere with subsequent step-over operations.

## Native Step Depth Check

After a native step, `CheckStepLanding` compares the current RSP (`frames[0].StackOffset`) against `StepOriginStackPointer`. On x86-64 the stack grows downward, so lower RSP = deeper stack. If RSP < origin, the native step entered a called function → re-step to continue past it. This check runs **before** any line comparisons, preventing step-over from landing in recursive calls or called functions at different source lines.

## Re-fire Suppression (`LastContinuedBpId`)

- Set in `ExecuteContinueOnEngine` to `model.LastHitBpId`.
- `HandleBreakpointHit`: if `breakpointId == LastContinuedBpId` → suppress, set `HitUserBreakpoint = false`, return early.
- One-shot: reset to `uint.MaxValue` after any BP hit (suppressed or not).
- **Purpose**: prevent race where SetInterrupt causes the same BP to re-fire immediately after continue.
- **Important**: both `LastHitBpId` and `LastContinuedBpId` are initialized to `uint.MaxValue` (sentinel). If `LastHitBpId` defaults to `0`, `configurationDone`'s continue sets `LastContinuedBpId = 0`, which suppresses the first hit of a native BP with dbgeng ID 0.

## Step-Over vs Step-Into vs Step-Out

**Step-Over** (`TryManagedStepOver`):
- Uses PDB sequence points to find next IL offset > current.
- Sets temp BP at next sequence point's native address.
- Also sets a step-out fallback BP in the caller (handles early returns like `return true;` mid-method).
- Falls back to step-out if no next sequence point (end of method).

**Step-Into** (`TryManagedStepInto`):
- Parses IL bytecode at current offset to find `call`/`callvirt` target.
- Three resolution paths:
  1. Target already in `JitMethodMap` → set temp BP at target's first source line.
  2. Target not JIT'd → one-shot plan site + WATCH command to profiler.
  3. Native target (C++/CLI wrapper) → resolve via `GetOffsetByName`.
- Fallback BP at next source line in caller (step-over behavior).

**Step-Out** (`ExecuteStepOutOnEngine`):
- Uses `FindStepOutTarget` which walks the stack from frame[1] upward.
- Skips frames without resolvable source (C++/CLI thunks, JIT helpers).
- Advances past the call site line to the next sequence point.
- Sets temp BP at that address, Go.

## Auto-Step Landing (`CheckStepLanding`)

After a native step, in order:
1. **Depth check**: RSP < origin → **ReStep** (entered a called function).
2. **Same line**: same source file:line as `StepOriginLocation` → **ReStep**.
3. **Closing brace / sourceless**: → **StepOut** (skip uninteresting frames).
4. Otherwise → **None** (normal stop, report "step").

## Key Model Fields

`NativeDebuggerModel` fields related to stepping and breakpoints:

| Field | Purpose |
|-------|---------|
| `LastHitBpId` | dbgeng ID of most recent BP hit |
| `LastContinuedBpId` | BP ID from last Continue (for re-fire suppression) |
| `HitUserBreakpoint` | `true` if LastHitBpId is a user BP |
| `Stepping` | volatile flag for native steps |
| `StepOriginStackPointer` | RSP before a native step (for depth check) |
| `StepOriginLocation` | source file:line before a native step (for same-line detection) |
| `ActiveManagedStep` | `ManagedStepState` if managed step active, else null |
| `ManagedStepIntoCompleted` | volatile flag set by step-into when completed |
| `UserBreakpointIds` | HashSet of all user BP IDs (native + managed) |
| `ManagedBreakpointIds` | HashSet of active managed HW BP IDs |
| `ManagedBpPlans` | Method breakpoint plans by (Token, Assembly) |
| `ActiveMethodBreakpoints` | Activation counts + installed HW BP IDs by (Token, Assembly) |
| `DeferredManagedBreakpoints` | BPs waiting for JIT compilation |
| `JitMethodMappings` | IL-to-native offset mappings by (Token, Assembly) |
| `JitMethodMap` | All JIT-compiled methods by native code start address |
| `ProfilerNotifications` | Queue of ENTER/LEAVE/JIT notifications from profiler pipe |

## File Reference

- `src/Services/EngineLifecycleService.cs` — event loop, ProcessCommandsUntilResume
- `src/Services/StepResolutionService.cs` — DetermineStopReason (returns StopReason enum), CheckStepLanding, CompleteManagedStep, RemoveStepIntoOneShotSites
- `src/Services/SteppingService.cs` — ExecuteContinueOnEngine, ExecuteStepOnEngine, TryManagedStepOver, TryManagedStepInto, TrySetStepIntoBpViaProfiler, SetManagedStepBreakpoint, CancelActiveManagedStep, ExecuteStepOutOnEngine, FindStepOutTarget
- `src/Services/ManagedBreakpointService.cs` — SetManagedBreakpoints, BindResolvedMethod, SetManagedCodeBreakpoint, ClearManagedBreakpointsForFile
- `src/Services/ManagedBreakpointResolverService.cs` — ProcessProfilerNotifications, FoldJitIntoPlans
- `src/Services/BreakpointService.cs` — HandleBreakpointHit (re-fire suppression), HandleExceptionBreakpoint
- `src/Services/ProfilerPipeService.cs` — ParseJitNotification, ParseEnterNotification, ParseLeaveOrTailcallNotification, RequestInterrupt
- `profiler/MixDbgProfiler.cpp` — JITCompilationFinished, OnFunctionEnter, OnFunctionLeave, JITInlining
