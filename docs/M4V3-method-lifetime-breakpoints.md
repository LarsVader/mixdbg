# M4V3 — Method-Lifetime Managed Breakpoint Lifecycle

Post-mortem and design notes for the third iteration of the managed breakpoint
system in MixDbg. Replaces the "transient-on-Continue" lifecycle that silently
broke loops.

## What went wrong with M4V2

M4V2 installed a hardware BP when the profiler's `FunctionEnter` hook fired,
then removed it on `Continue` and re-enabled hooks via a rehook event. The
working assumption was "one ENTER per BP hit."

That assumption fails for any method that runs through more than one BP during
a single activation. The canonical case:

```csharp
foreach (Item item in items) {
    DoSomething(item);   // BP here
}
```

The BP fires on iteration 1. The user hits Continue. MixDbg removes the HW BP,
signals REHOOK, and the profiler re-enables `FunctionEnter` hooks — but the
method is still executing. `FunctionEnter` doesn't fire again until the method
is called again. Iterations 2..N run with no BP installed.

A secondary problem: the rehook watcher thread, the "transient vs permanent"
BP distinction, the `PermanentManagedBreakpointIds` set, and the special
handling in `RemoveTransientManagedBreakpoints` accumulated complexity without
solving the loop problem.

## The fix: method-lifetime scoping

A managed BP lives exactly as long as its method has at least one activation
on the call stack. Installed on the first `FunctionEnter`, removed on the
final `FunctionLeave`.

### Data model

- **`ManagedMethodBreakpointPlan`** — declarative, per-method config. Created
  when the user calls `setBreakpoints`. Holds one or more `MethodBreakpointSite`
  entries (one per BP line in the method).
- **`ActiveMethodBreakpoint`** — runtime state for a method currently on the
  stack. Tracks `ActivationCount` (incremented on ENTER, decremented on
  LEAVE/TAILCALL) and the HW BP IDs currently installed for that method.
- **`ProfilerNotifications`** queue — unified queue of JIT / ENTER / LEAVE /
  TAILCALL notifications from the profiler pipe, drained on the engine thread.

### State transitions

```
         +-------------+ 1st ENTER (0→1)   +----------------------+
(no plan)| Plan exists | ----------------> | Plan + Active (live) |
         +-------------+                   +----------------------+
               ^         Final LEAVE (→0)     |    ENTER
               |         Remove HW BPs        |    count++
               +-------------+----------------+    (no-op install)
                             |
                        Nested ENTER/LEAVE
                        never touches HW BPs
```

- **First `ENTER` (count 0→1)**: MixDbg installs one HW BP per site in the
  plan (using `JitMethodMappings` for exact IL→native line resolution), then
  signals ACK. The profiler resumes the app thread and the method body runs;
  the HW BPs fire when the user code hits them.
- **Recursive/nested `ENTER` (count ≥ 1)**: MixDbg just increments the
  count and ACKs immediately — the HW BPs from the first activation are
  still installed and valid.
- **`LEAVE` / `TAILCALL`**: count--. When it reaches 0, MixDbg removes every
  HW BP installed for that method and drops the `ActiveMethodBreakpoints`
  entry. No ACK is needed (LEAVE is fire-and-forget from the profiler).

### ACK-on-first-ENTER rationale

The profiler waits on `MIXDBG_ACK_EVENT` after writing every `ENTER:`. This
guarantees the HW BP is installed before the method body runs — otherwise the
BP on the first line would miss on cold calls. Nested/recursive ENTERs pay
the same wait, but MixDbg ACKs them immediately (no work to do since the HW
BP is already live), so the overhead is one event ping-pong per call.

## JIT inlining

Without any countermeasures, the JIT may inline a small watched callee into
its caller. The inlined copy has no `FunctionEnter` prologue, so ENTER never
fires — and the BP is invisible.

`JITInlining` is an `ICorProfilerCallback` slot called before every inlining
decision. We implement it and return `*pfShouldInline = FALSE` whenever the
callee is in a watched list. The JIT emits an out-of-line call, ENTER fires,
the BP is installed. Cost is small because only watched assemblies/tokens are
blocked from inlining; everything else still inlines normally.

## Consequences and limits

- **Continue/step keep BPs**: No more `RemoveTransientManagedBreakpoints`, no
  rehook event, no rehook watcher thread. Continue just sets Go. The
  `PermanentManagedBreakpointIds` set is gone — the plan is the source of
  truth.
- **Hardware-BP budget is global**: x86-64 has 4 debug registers. The plan
  happily creates more than 4 sites, but `AddHardwareBreakpoint` fails once
  the budget is exhausted. We log a warning; the site's BP silently won't
  fire until a slot frees. Good enough while ≤4 methods with BPs are on the
  stack at once.
- **Recursion uses one slot**: Activation counting handles recursion. The
  first activation installs one HW BP; recursive calls just bump the count.
- **Multi-threading**: `ActivationCount` is incremented once per ENTER
  regardless of thread, so a method running on 2 threads has count=2 and
  holds the HW BP until both finish. Correct but slightly coarser than
  per-thread tracking.
- **Tailcalls**: Treated identically to `Leave` — the current activation is
  ending.
- **Exception unwinds**: `FunctionLeave` is NOT called when an exception
  unwinds past the method. Activation counts can leak; the HW BP sticks.
  The leak is per-method — it self-heals on the next `setBreakpoints` or the
  next process launch. A follow-up can hook
  `ExceptionUnwindFunctionLeave` if this becomes a real issue.
- **Pre-launch BP on a never-called method**: Plan created, `WATCH` sent,
  but `FunctionEnter` never fires. BP never installs. Same behavior as
  before — the user sees the BP as "verified" but the method is dead code.
- **Assembly-level watches without BPs (C++/CLI)**: ENTER fires for every
  method in the watched assembly. For methods without a plan entry, MixDbg
  sends an immediate ACK (no HW BP work). Activation counting is skipped
  for those methods since we don't need to track them.

## Known issues (not yet investigated)

Two integration tests are still failing after this change and are tracked for
follow-up:

1. **`Recursion_WhenStepOverInTryGetA_ReturnsToFibonacciClick`** — step-over
   inside a recursive method that re-enters the same method hits the
   still-installed method-lifetime HW BP on the recursive call, and
   `DetermineStopReason` reports "breakpoint" instead of "step". A trial fix
   (suppress method-lifetime BP hits during `ActiveManagedStep`) was reverted —
   the test expectations may need re-auditing first; stopping on the BP in the
   recursion might actually be the correct debugger behavior, in which case
   the test needs updating rather than the code.

2. **`ManagedBreakpoint_WhenCSharpAddedMidSession_StillFires`** — a mid-session
   C# BP fires but the stack trace for the expected hit is missing
   `MainWindow.xaml.cs`. The BP hit address or the stack frame source
   resolution is off; not diagnosed yet.

Neither failure affects the primary goal: BPs inside loops/foreach work
correctly (covered by
`Complex_WhenBpInsideForeachWithLambda_StopsWithSource`, which now passes).

## Files touched

| File | Change |
| ---- | ------ |
| `profiler/MixDbgProfiler.{h,cpp}` | `OnFunctionLeave`, `JITInlining`, removed rehook thread. |
| `profiler/FunctionCallbacks.cpp` | `FunctionLeaveImpl` / `FunctionTailcallImpl` route to `OnFunctionLeave`. |
| `src/Models/NativeDebuggerModel.cs` | Added `ManagedBpPlans`, `ActiveMethodBreakpoints`, `ProfilerNotifications`. Removed ENTER singleton fields, `ProfilerRehookEvent`, `PermanentManagedBreakpointIds`. |
| `src/Services/ProfilerPipeService.cs` | LEAVE/TAILCALL parsers; all notifications enqueue to `ProfilerNotifications`. |
| `src/Services/ManagedBreakpointResolverService.cs` | New `ProcessProfilerNotifications`; deleted `HandleEnterBreakpoint`. |
| `src/Services/ManagedBreakpointService.cs` | `BindResolvedMethod` creates plan; piggybacks HW BP on live activations; deleted `SetTransientBreakpoint` et al. |
| `src/Services/EngineQueryService.cs` | Removed all `RemoveTransientManagedBreakpoints` and rehook calls. Step-into uses `IsStepIntoOneShot` plan sites. |
| `src/Services/EngineLifecycleService.cs` | Calls `ProcessProfilerNotifications` instead of `HandleEnterBreakpoint`. |
