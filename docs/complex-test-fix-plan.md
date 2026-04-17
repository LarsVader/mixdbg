# Complex Test Scenarios — Fix Plan

## Current State

14 new integration tests in `ComplexScenarioIntegrationTest.cs` exercise async, recursion, exceptions, lambdas, and loops. **12 pass, 2 fail** due to debugger bugs.

All existing integration tests continue to pass.
## Remaining Failing Tests

### 3. `Complex_WhenBpInsideForeachWithLambda_StopsWithSource`

**Symptom:** BP on line 164 (inside foreach loop) fires once on first iteration, but doesn't re-arm for the second iteration. Timeout after 15s.

**Root cause:** The ENTER hook / hardware BP mechanism removes transient BPs on Continue but only re-arms via REHOOK for the enclosing method. Inside a loop, the same method body is already executing — there's no re-entry, so no new ENTER fires.

**Likely fix:** This is a permanent managed BP (set pre-launch), not a transient one. It should persist through Continue like `PermanentManagedBreakpointIds`. Needs investigation of why it's being removed.

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
