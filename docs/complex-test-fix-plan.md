# Complex Test Scenarios — Fix Plan

## Current State

14 new integration tests in `ComplexScenarioIntegrationTest.cs` exercise async, recursion, exceptions, lambdas, and loops. **12 pass, 2 fail** due to debugger bugs.

All existing integration tests (10 SteppingIntegrationTest + 4 ManagedBreakpointIntegrationTest + 505 unit tests) continue to pass.

## Fixed Tests

### 1. `Recursion_WhenStepOverInTryGetA_ReturnsToFibonacciClick` — FIXED

**Original symptom:** Step-into from line 96 stayed on line 97 — behaved as step-over instead of entering TryGetA. Test timed out.

**Root causes (four distinct issues):**

**1a. Line number shift (+2) in MainWindow.xaml.cs.** Modifications to `OnAutoTest` (added `AutoTestComplex` branch, compressed ScheduleActions, added 5 action helpers) added 2 net lines before `OnAddClick`, shifting all line numbers by +2. This silently broke ALL existing integration tests — BPs were set on method signatures instead of first statements, so they never fired. The test constants in `ComplexScenarioIntegrationTest.cs` were written for the intended (correct) line numbers, which didn't match the shifted file.

**Fix:** Compressed a 2-line comment in the `AutoTestComplex` branch to a single inline comment, restoring `OnAddClick` to line 63. All test constants now match actual source lines.

**1b. Step-into reordering regression.** The original fix moved `TrySetNativeStepIntoBp` before `TrySetStepIntoBpViaProfiler`. This caused it to resolve C++/CLI cross-assembly calls (like `ManagedCalculator.Add`) via dbgeng native symbols instead of the profiler WATCH path, setting BPs at wrong addresses.

**Fix:** Reordered resolution chain to: JitMap → Profiler WATCH → Native symbols (last resort). The null-assembly fill-in for same-assembly `MethodDefinition` calls was correct and kept.

**1c. Opening brace sequence point for `out` param methods.** `TryGetA(out int a)` has Roslyn generate a non-hidden sequence point at the opening brace (line 176) for the `out` parameter initialization. Two code paths set BPs at this brace instead of the first statement (line 177):
- `TrySetBpOnJitMethod` (JitMap path): set step-into target BP at seq point[0]
- `TrySetStepIntoBpViaProfiler` (profiler path): created deferred BP with `ilOffset = seqPoints[0].ILOffset`, causing `HandleEnterBreakpoint` to set the transient hardware BP at the brace address

**Fix:** Added prologue-skip heuristic in both paths: skip first seq point when `ILOffset == 0` and the next point has `ILOffset > 0` on a later line. This targets the `out`/`ref` param initialization pattern without affecting normal methods.

**1d. Step-over at `return true;` exits method without reaching next seq point.** Managed step-over set a temp BP at line 180 (next seq point in TryGetA: `a = 0;`), but `return true;` exits the method via a branch to the epilogue — line 180 is never reached.

**Fix:** Added a step-out fallback BP in the caller (via `FindStepOutTarget`) alongside the next-seq-point BP during managed step-over. Whichever fires first wins. Updated the test to expect landing on the `{` brace (line 97, standard debugger behavior) then stepping to the Fibonacci call (line 98).

## Remaining Failing Tests

### 2. `Recursion_WhenStepIntoFibonacciCall_EntersNativeCode` — FIXED

**Original symptom:** Step-into `ManagedCalculator.Fibonacci(n)` from line 98 stays in MainWindow.xaml.cs.

**Root cause:** The managed step-over from line 96 (`if (TryGetA(out int n))`) lands on line 97 (the `{` brace) — Roslyn emits a non-hidden sequence point there for the `out` variable's scope entry. `TryManagedStepInto` then correctly identifies the Fibonacci call target on line 98, but sets its fallback breakpoint at the first sequence point after `currentIL` — which is line 98 (the call site itself). Lines 97 and 98 are only 1 native byte apart (`0x839A` vs `0x839B`), so the fallback BP fires before the call instruction executes. The profiler ENTER hook mechanism never gets a chance to intercept the call.

**Fix:** Added `CallILOffset` to `GetCallTargetAtOffset` return value so the caller knows the IL position of the `call`/`callvirt` instruction. In `TryManagedStepInto`, the fallback BP threshold is now `CallILOffset + 5` (past the 5-byte call instruction) instead of `currentIL`, ensuring the fallback fires only after the call returns — not before it's made.

### 3. `Complex_WhenBpInsideForeachWithLambda_StopsWithSource`

**Symptom:** BP on line 164 (inside foreach loop) fires once on first iteration, but doesn't re-arm for the second iteration. Timeout after 15s.

**Root cause:** The ENTER hook / hardware BP mechanism removes transient BPs on Continue but only re-arms via REHOOK for the enclosing method. Inside a loop, the same method body is already executing — there's no re-entry, so no new ENTER fires.

**Likely fix:** This is a permanent managed BP (set pre-launch), not a transient one. It should persist through Continue like `PermanentManagedBreakpointIds`. Needs investigation of why it's being removed.

## Verification Results (2026-04-15)

All verified after clean rebuild of MixDbg + TestApp:
- **SteppingIntegrationTest:** 10/10 pass
- **ManagedBreakpointIntegrationTest:** 4/4 pass (8 skipped as expected)
- **ComplexScenarioIntegrationTest:** 13/14 pass (1 remaining failure: issue 3)
- **Unit tests:** 505/505 pass

## Files Changed

### TestApp
- `test/TestApp/NativeLib/Calculator.h` — New methods: Fibonacci, SumRange, FactorialOrThrow, CountPrimes, IsPrime, AccumulateSum
- `test/TestApp/NativeLib/Calculator.cpp` — Implementations (Add at line 7 preserved)
- `test/TestApp/CliWrapper/ManagedCalculator.h` — Wrappers (Add at line 14 preserved)
- `test/TestApp/WpfApp/MainWindow.xaml` — New buttons (Fibonacci, Primes, Factorial, Async Calc, Complex)
- `test/TestApp/WpfApp/MainWindow.xaml.cs` — New handlers after line 91 (OnAddClick at 63, `if` at 65, Add call at 67, ResultText at 68 — all preserved)
- `test/TestApp/WpfApp/App.xaml.cs` — `AutoTestComplex` flag
- `test/TestApp/WpfApp/GlobalUsings.cs` — New file (keeps MainWindow usings at 4 to preserve line numbers)

### Debugger fixes
- `src/Services/EngineQueryService.cs`:
  - `TryManagedStepInto`: fill in null `TargetAssembly` with caller's assembly for same-assembly calls; reorder to JitMap → Profiler → Native; fallback BP now uses `CallILOffset + 5` threshold to land past the call instruction
  - `TrySetBpOnJitMethod`: prologue-skip heuristic for `out`/`ref` param brace seq points
  - `TrySetStepIntoBpViaProfiler`: same prologue-skip heuristic when creating deferred BP IL offset; updated tuple type for `CallILOffset`
  - `TryManagedStepOver`: add step-out fallback BP in caller to handle early returns
- `src/MixDbg.EngineWrappers/Engine/Sos/PdbSourceMapperService.cs`: `GetCallTargetAtOffset` now returns `CallILOffset` (IL position of the call/callvirt instruction)
- `src/MixDbg.EngineWrappers/Services/Interfaces/IPdbSourceMapper.cs`: updated return type to include `CallILOffset`

### Integration tests
- `test/IntegrationTests/ComplexScenarioIntegrationTest.cs` — 14 new tests using `--auto-test-complex`; added `_fibonacciIfBodyLine` constant; updated step-over-return test to include brace landing step
