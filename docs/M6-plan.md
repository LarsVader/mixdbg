# M6: Stepping (Native + Managed + Cross-Boundary)

## Status: IN PROGRESS (Phases 1-6 implemented, manual verification remaining)

## Context

Stepping DAP handlers (`next`, `stepIn`, `stepOut`) exist and are wired up to dbgeng's
`SetExecutionStatus(StepOver/StepInto)` and `ExecuteCommand("gu")`. This works for **native
code** because dbgeng has PDB source-level symbols. However:

1. **Step-out is broken**: `StepOutRequestHandlerService` never sets `model.Stepping = true`,
   so after `gu` completes the event loop treats it as a system stop and auto-continues.
2. **Managed stepping doesn't work**: dbgeng has no source symbols for JIT'd code, so
   `StepOver`/`StepInto` step by native instructions — the user sees random stops inside
   JIT'd x64 code instead of stepping by C# source lines.
3. **Cross-boundary stepping** is unpredictable — stepping from native into JIT'd code (or
   vice versa) produces meaningless stops.

## Approach

**Native frames**: Keep using dbgeng's built-in stepping (already works).

**Managed frames**: Convert step operations into "set temporary hardware BP at target native
address, then Go." The infrastructure for this already exists — managed breakpoints use the
same mechanism (JitMethodMap + IL-to-native mapping + `ba e1` hardware BPs).

**Detection**: At step time on the engine thread, check if the current IP is in `JitMethodMap`
via `FindContainingMethod`. If yes → managed stepping. If no → native stepping (existing path).

---

## Phase 1: Fix step-out (bug fix) — DONE

**Problem**: `StepOutRequestHandlerService` doesn't set `model.Stepping = true`. After `gu`
completes, `DetermineStopReason` returns null → auto-continues silently.

**Fix**: Set `model.Stepping = true` in `StepOutRequestHandlerService.ExecuteInternal`,
same as the next/stepIn handlers.

### Files
- `src/Services/Handlers/Execution/StepOutRequestHandlerService.cs` — add `model.Stepping = true`
- `test/UnitTests/Handlers/Execution/ExecutionHandlerServiceTests.cs` — add assertion for Stepping flag

---

## Phase 2: Managed step-over — DONE

### New: `IPdbSourceMapper.GetMethodSequencePoints`

Returns all non-hidden sequence points (IL offset → source file:line) for a method, sorted by
IL offset. `PdbSourceMapperService` already iterates sequence points in `GetSourceLocation` —
extract the iteration into a new method.

```csharp
(int ILOffset, string File, int Line)[] GetMethodSequencePoints(string assemblyPath, int methodToken);
```

### New: `ManagedStepState` model

Track active managed step on `NativeDebuggerModel`:

```csharp
internal sealed class ManagedStepState
{
    public List<uint> TempBreakpointIds { get; } = [];
}

// On NativeDebuggerModel:
internal ManagedStepState? ActiveManagedStep;
```

### Algorithm (in `ExecuteStepOnEngine` when stepKind == StepOver)

1. Get current IP from top stack frame (`GetStackTrace(1)`).
2. `FindContainingMethod(JitMethodMap, ip)` — if null → native → use existing
   `SetExecutionStatus(StepOver)`.
3. If managed:
   a. Compute current IL offset via `ComputeILOffset`.
   b. Call `GetMethodSequencePoints(assemblyPath, methodToken)`.
   c. Find the first sequence point with IL offset > current IL offset → "next line."
   d. If next line exists: get its native address via `JitMethodMapping.GetNativeAddress(nextILOffset)`.
      Set temp hardware BP (`ba e1 <addr>`). Track BP ID in `model.ActiveManagedStep`.
   e. If no next line (end of method): get caller's return address from frame[1], set temp BP there.
   f. Call `SetExecutionStatus(Go)`.

### Event loop changes (`DetermineStopReason`)

Before checking `model.Stepping`, check `model.ActiveManagedStep`:
- If active AND `model.HitUserBreakpoint` is true: check if the hit BP is one of our temp BPs.
  If yes → step completed. If no → real user BP; cancel managed step, report "breakpoint".
- If active AND no user BP hit: step completed (temp BP fired via dbgeng internal tracking).
- On completion: remove all temp BPs in `TempBreakpointIds`, clear `ActiveManagedStep`,
  return reason `"step"`.

### Cleanup paths

`ExecuteContinueOnEngine` must cancel any `ActiveManagedStep` (remove temp BPs, clear state).
Same for starting a new step while one is active.

### Files
- `src/MixDbg.EngineWrappers/Services/Interfaces/IPdbSourceMapper.cs` — add `GetMethodSequencePoints`
- `src/MixDbg.EngineWrappers/Engine/Sos/PdbSourceMapperService.cs` — implement it
- `src/Models/NativeDebuggerModel.cs` — add `ManagedStepState`, `ActiveManagedStep`
- `src/Services/EngineQueryService.cs` — branch `ExecuteStepOnEngine` for managed step-over
- `src/Services/EngineLifecycleService.cs` — modify `DetermineStopReason` for `ActiveManagedStep`
- `test/UnitTests/` — tests for `GetMethodSequencePoints`, step-over target computation

### Reuse
- `ManagedDebuggerService.FindContainingMethod` — IP-in-JitMethodMap check
- `ManagedDebuggerService.ComputeILOffset` — native IP → IL offset
- `JitMethodMapping.GetNativeAddress` — IL offset → native address (forward mapping)
- `PdbSourceMapperService` — PDB reading infrastructure
- `IDbgEngWrapper.ExecuteCommand` — `ba e1` hardware BP commands

---

## Phase 3: Managed step-out — DONE

### Algorithm (in `ExecuteStepOutOnEngine`)

1. Get current IP, check if in JitMethodMap.
2. If native → use existing `gu` command.
3. If managed:
   a. Get stack trace (at least 2 frames).
   b. Frame[1]'s `InstructionOffset` is the caller's return address.
   c. Set temp hardware BP at that address.
   d. Track in `model.ActiveManagedStep`.
   e. Call `SetExecutionStatus(Go)`.

Same event loop handling as Phase 2. This replaces native `gu` for managed frames because
`gu` fails across native-to-managed boundaries.

### Files
- `src/Services/EngineQueryService.cs` — branch `ExecuteStepOutOnEngine`

---

## Phase 4: Managed step-into — DONE

### Approach: IL call target parsing (replaced original single-step loop plan)

Instead of a native single-step loop (slow, unpredictable), parse IL bytecode at the current
offset to identify the call target method, then set a temp BP at its first source line.

### Algorithm (in `ExecuteStepOnEngine` when stepKind == StepInto)

1. Get current IP, check if in JitMethodMap. If native → use existing
   `SetExecutionStatus(StepInto)`.
2. If managed:
   a. `GetCallTargetAtOffset` scans IL for `call`/`callvirt` opcodes at the current IL offset.
   b. Resolve target method token via `FindMethodToken` (PE metadata lookup by type+method name).
   c. **C# → C# calls**: Look up target in `JitMethodMap`. Set temp BP at first source line's
      native address (via IL-to-native mapping).
   d. **C# → C++/CLI calls**: Send profiler `WATCH` command + wait for `ENTER` hook + transient BP
      (reuses M4 breakpoint infrastructure).
   e. **C++/CLI → native calls**: `GetOffsetByName` on `IDbgEngWrapper` resolves symbol names to
      native addresses (IDebugSymbols slot 5). Hardware BP skips opening brace, lands on first
      statement.
   f. **Fallback**: When call target cannot be resolved, set temp BP at next source line
      (step-over behavior).
   g. Call `SetExecutionStatus(Go)`.

### New APIs added
- `IPdbSourceMapper.GetCallTargetAtOffset(assemblyPath, methodToken, ilOffset)` — scans IL for
  call/callvirt opcodes, returns target type+method name
- `IPdbSourceMapper.FindMethodToken(assemblyPath, typeName, methodName)` — finds MethodDef token
  by type+method name in PE metadata
- `IDbgEngWrapper.GetOffsetByName(model, symbolName)` — resolves symbol names to native addresses
  (IDebugSymbols vtable slot 5)

### Step-into completion detection
- `ManagedStepIntoCompleted` volatile flag on `NativeDebuggerModel` signals completion inside
  `ProcessCommandsUntilResume` for blocking commands.
- Step-into deferred BPs use BpId=-1 marker, cleaned up in `DetermineStopReason`.

### Files
- `src/MixDbg.EngineWrappers/Services/Interfaces/IPdbSourceMapper.cs` — add `GetCallTargetAtOffset`, `FindMethodToken`
- `src/MixDbg.EngineWrappers/Engine/Sos/PdbSourceMapperService.cs` — implement IL parsing + PE metadata lookup
- `src/MixDbg.EngineWrappers/Services/Interfaces/IDbgEngWrapper.cs` — add `GetOffsetByName`
- `src/MixDbg.EngineWrappers/Services/DbgEngWrapperService.cs` — implement via IDebugSymbols
- `src/MixDbg.EngineWrappers/Engine/DbgEng/Interfaces/IDebugSymbols.cs` — add GetNameByOffset (slot 5)
- `src/Services/EngineQueryService.cs` — managed step-into logic with call target resolution
- `src/Services/EngineLifecycleService.cs` — `ProcessCommandsUntilResume` step-into detection
- `src/Services/ManagedDebuggerService.cs` — step-into helper methods
- `src/Services/Interfaces/IManagedDebugger.cs` — step-into interface additions

---

## Phase 5: Cross-boundary step-over — DONE (no extra work needed)

Step-over across boundaries works automatically:

- **Managed frame, call into native**: Phase 2 temp BP at the next managed source line handles
  this — the native call executes fully, returns to managed code, temp BP fires. Correct.
- **Native frame, call into managed**: dbgeng's native `StepOver` handles this — steps over the
  entire managed call because dbgeng treats it as one function call. Correct.

---

## Phase 6: Edge cases and hardening — DONE

1. **Hardware BP slot limit (4 on x64)**: Step temp BPs use only 1 temp BP per step (next-line
   OR return-address, not both). End-of-method detected explicitly to choose return-address path.

2. **Exception during managed step-over**: Temp BP at next line won't fire if the method
   throws. The exception callback fires instead. Check `ActiveManagedStep` in the exception
   path — cancel step, remove temp BPs, report exception.

3. **Recursive calls during step-over**: Temp BP at the next line fires on every activation
   (including recursive). This is correct — the first return from recursion stops at the
   right place.

4. **Step-over at end of method**: No next sequence point → fall back to step-out behavior
   (temp BP at caller return address).

5. **Cleanup on new step/continue**: Cancel any `ActiveManagedStep` before starting a new
   operation. `ExecuteContinueOnEngine` removes temp BPs and clears state.

6. **`ProcessCommandsUntilResume` step detection**: Detects step completion for blocking
   commands (`gu`, managed step-into) via `ManagedStepIntoCompleted` volatile flag.

7. **`DetermineStopReason` managed step handling**: Handles `ActiveManagedStep` temp BPs
   (returns `"step"`) and step-into deferred BPs (BpId=-1 marker, cleaned up on hit).

8. **`IsInfrastructureSource` filtering**: Filters profiler, coreclr, Windows Kits, VC CRT,
   and non-existent paths from step targets to avoid stopping in framework code.

---

## Remaining Work

- **Step-out source resolution**: Step-out from managed code currently stops but source
  resolution may not work for all frames (especially when returning to managed code from
  native). Needs manual verification across all boundary types.

---

## Verification

1. **Build**: `dotnet build src/MixDbg.csproj -c Debug` — no warnings
2. **Unit tests**: `dotnet test test/UnitTests/UnitTests.csproj` — all pass
3. **Integration tests**: `dotnet test test/IntegrationTests/MixDbg.IntegrationTests.csproj`
4. **Manual integration test with TestApp**:
   - Set BP in C# code (`WpfApp/MainWindow.xaml.cs`), hit it
   - Step over (`F10`) — should advance to next C# line, not jump to random JIT'd code
   - Step into (`F11`) a method call — should enter the called method's first line
   - Step out (`Shift-F11`) — should return to caller
   - Step into from C# into C++/CLI wrapper — should stop at C++/CLI source line
   - Step into from C++/CLI into native C++ — should stop at native source line
   - Step over a cross-boundary call — should stay in the same frame

## Files Summary

| File | Phase | Changes |
|------|-------|---------|
| `src/Services/Handlers/Execution/StepOutRequestHandlerService.cs` | 1 | Set `Stepping = true` |
| `src/MixDbg.EngineWrappers/Services/Interfaces/IPdbSourceMapper.cs` | 2,4 | Add `GetMethodSequencePoints`, `GetCallTargetAtOffset`, `FindMethodToken` |
| `src/MixDbg.EngineWrappers/Engine/Sos/PdbSourceMapperService.cs` | 2,4 | Implement sequence points, IL parsing, PE metadata lookup |
| `src/MixDbg.EngineWrappers/Services/Interfaces/IDbgEngWrapper.cs` | 4 | Add `GetOffsetByName` |
| `src/MixDbg.EngineWrappers/Services/DbgEngWrapperService.cs` | 4 | Implement `GetOffsetByName` via IDebugSymbols |
| `src/Models/NativeDebuggerModel.cs` | 2 | Add `ManagedStepState`, `ActiveManagedStep`, `ManagedStepIntoCompleted` |
| `src/Services/EngineQueryService.cs` | 2-4 | Managed step-over/out/into logic |
| `src/Services/EngineLifecycleService.cs` | 2,4,6 | `DetermineStopReason` + `ProcessCommandsUntilResume` managed step handling |
| `src/Services/ManagedDebuggerService.cs` | 4 | Step-into helper methods |
| `src/Services/Interfaces/IManagedDebugger.cs` | 4 | Step-into interface additions |
| `test/UnitTests/` | 1-4 | Tests per phase |
| `test/IntegrationTests/SteppingIntegrationTest.cs` | all | Cross-boundary stepping integration tests |
| `test/IntegrationTests/xunit.runner.json` | all | Disable parallel execution for integration tests |
