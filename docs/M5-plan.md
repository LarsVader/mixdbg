# M5: Managed Variable Inspection via ICorDebug V4

## Context

When stopped at a C# stack frame, selecting it shows no variables. `GetScopesOnEngine(frameId)` calls `_wrapper.SetScopeAndGetLocals` which returns 0 for managed frames because dbgeng has no managed symbol info. M5 adds variable inspection for managed (C#) frames using the already-piggybacked ICorDebug V4 process.

**ICorDebug V4 instead of ClrMD**: The README mentions ClrMD but ICorDebug is the right tool. ClrMD is a heap analysis library with no frame-level locals API. ICorDebug V4 has exactly the right API: `ICorDebugILFrame.EnumerateLocalVariables()` / `GetLocalVariable(index)`. The piggybacked process is already initialized and `GetRawManagedFrames` already walks frames with `frame is CorDebugILFrame` casts (CorDebugWrapperService.cs:172). No new NuGet needed.

**Risk**: `EnumerateLocalVariables` might return `E_NOTIMPL` on V4 piggybacked process. Mitigation: fall back to `GetLocalVariable(index)` with PDB-derived count.

## Implementation

### Phase 1: PDB Local Variable Names

**`IPdbSourceMapper.cs`** + **`PdbSourceMapperService.cs`** (EngineWrappers)

Add two methods:
- `GetLocalVariableNames(assemblyPath, methodToken, ilOffset) -> (string Name, int Index)[]` -- reads `LocalScope` table from portable PDB, filters by IL offset range, returns (name, slot index) pairs
- `GetParameterNames(assemblyPath, methodToken) -> string[]` -- reads `MethodDefinition.GetParameters()` from PE metadata, returns parameter names in order

These use the existing `GetOrLoadReader` (PDB) and `GetOrLoadPeReader` (PE) caches. `System.Reflection.Metadata` API: `reader.GetLocalScopes(methodDefHandle)` -> `LocalScope.GetLocalVariables()` -> `LocalVariable.Name` + `LocalVariable.Index`.

### Phase 2: ManagedVariableStore

**New file**: `EngineWrappers/Models/ManagedVariableStore.cs`

```
ManagedVariableStore
  BaseOffset = 100_000  (native VariableStore starts at 1, never reaches this)
  Allocate(ManagedVariableEntry) -> int ref
  Get(int ref) -> ManagedVariableEntry?
  static IsManaged(int ref) -> bool  (ref >= BaseOffset)
  Clear()
```

`ManagedVariableEntry` has three variants (discriminated by non-null field):
- **Locals scope**: `(string Name, CorDebugValue Value)[] Locals` -- top-level locals + args
- **Object fields**: `CorDebugObjectValue ObjectValue` + class/module refs for field enum
- **Array elements**: `CorDebugArrayValue ArrayValue` + count

Store lives on `CorDebugWrapperModel.VariableStore`. All ClrDebug types stay internal to EngineWrappers.

### Phase 3: ICorDebug Variable Inspection

**`ICorDebugWrapper.cs`** -- add 3 methods:
```csharp
int InitializeManagedLocals(CorDebugWrapperModel model, uint osThreadId,
    ulong ip, string? assemblyPath, int methodToken, int ilOffset);
VariableInfo[] GetManagedVariables(CorDebugWrapperModel model, int variablesReference);
void ClearManagedVariables(CorDebugWrapperModel model);
```

**`CorDebugWrapperService.cs`** implementation:

`InitializeManagedLocals`:
1. Walk `Process.Threads` -> find thread by OS ID (same as GetRawManagedFrames)
2. Walk chains -> frames -> find `CorDebugILFrame` matching methodToken
3. Try `ilFrame.EnumerateLocalVariables()` -> `CorDebugValue[]` (catch E_NOTIMPL, fall back to `GetLocalVariable(i)`)
4. Try `ilFrame.EnumerateArguments()` -> `CorDebugValue[]` (same fallback)
5. Get names from PDB (`_pdbMapper.GetLocalVariableNames`, `_pdbMapper.GetParameterNames`)
6. Combine into `ManagedVariableEntry` with named values, allocate store ref

`GetManagedVariables`:
1. Look up entry in store
2. **Locals scope**: format each (Name, CorDebugValue) pair by CorElementType:
   - `BOOLEAN/I1/U1/.../I8/U8/R4/R8`: `CorDebugGenericValue.GetValue()` -> BitConverter -> string
   - `STRING`: `CorDebugReferenceValue.IsNull` check, then `.Dereference()` -> `CorDebugStringValue.GetString()`
   - `CLASS`: null check, dereference -> `CorDebugObjectValue`, show `{TypeName}`, allocate child ref
   - `VALUETYPE`: `CorDebugObjectValue.GetClass()`, show `{TypeName}`, allocate child ref
   - `SZARRAY`: dereference -> `CorDebugArrayValue`, show `Type[count]`, allocate child ref
3. **Object fields**: `GetClass()` -> `MetaDataImport.EnumFields` -> `GetFieldValue` per field, walk superclass chain
4. **Array elements**: `GetElementAtPosition(i)` for i in 0..min(count, 100), name as `[i]`

Inject `IPdbSourceMapper` into `CorDebugWrapperService` constructor (DI already supports this).

### Phase 4: Wire Into Scopes/Variables

**`IManagedDebugger.cs`** + **`ManagedDebuggerService.cs`** -- add:
```csharp
int TryGetManagedLocals(NativeDebuggerModel model, ulong instructionPointer);
```
Reuses existing private `FindContainingMethod`, `FindAssemblyPath`, `ComputeILOffset` to look up JitMethodMap, then delegates to `_corDebug.InitializeManagedLocals`.

**`EngineQueryService.cs`** -- modify:

`GetScopesOnEngine(model, frameId)`:
1. Try native: `_wrapper.SetScopeAndGetLocals(model.Wrapper, frameId)` -- returns non-zero for C++/C++CLI
2. If 0 and managed initialized: get IP from `model.Wrapper.CachedStackFrames[frameId-1].InstructionOffset`
3. Call `_managedDebugger.TryGetManagedLocals(model, ip)` -- returns managed ref or 0
4. Return scope with whichever ref is non-zero

`GetVariablesOnEngine(model, variablesReference)`:
- Route by `ManagedVariableStore.IsManaged(ref)`: managed -> `_corDebug.GetManagedVariables`, native -> `_wrapper.GetVariables`

`ExecuteContinueOnEngine` / `ExecuteStepOnEngine` / `ExecuteStepOutOnEngine`:
- Add `_corDebug.ClearManagedVariables(model.CorWrapper)` alongside existing `_wrapper.ClearVariables`

Inject `ICorDebugWrapper` into `EngineQueryService` constructor.

### Phase 5: Tests

- `PdbSourceMapperTests` -- `GetLocalVariableNames`/`GetParameterNames` against TestApp's WpfApp portable PDB
- `ManagedVariableStoreTests` -- allocation, retrieval, clear, offset scheme, IsManaged
- `EngineQueryServiceTests` -- mock ICorDebugWrapper, test scopes fallback native->managed, variable routing by ref range, clear calls

### Phase 6: Docs

- CLAUDE.md: mark M5 DONE, update project structure with new files
- README.md: add "Managed variable inspection" to feature list, remove from "not yet implemented"

## Files to Create

| File | Purpose |
|------|---------|
| `src/MixDbg.EngineWrappers/Models/ManagedVariableStore.cs` | Managed variable ref tracking + entry types |

## Files to Modify

| File | Change |
|------|--------|
| `src/MixDbg.EngineWrappers/Services/Interfaces/IPdbSourceMapper.cs` | Add `GetLocalVariableNames`, `GetParameterNames` |
| `src/MixDbg.EngineWrappers/Engine/Sos/PdbSourceMapperService.cs` | Implement PDB local scope + PE parameter reading |
| `src/MixDbg.EngineWrappers/Services/Interfaces/ICorDebugWrapper.cs` | Add `InitializeManagedLocals`, `GetManagedVariables`, `ClearManagedVariables` |
| `src/MixDbg.EngineWrappers/Services/CorDebugWrapperService.cs` | Implement ICorDebugILFrame walking, value formatting, field/array expansion |
| `src/MixDbg.EngineWrappers/Models/CorDebugWrapperModel.cs` | Add `ManagedVariableStore` property |
| `src/Services/Interfaces/IManagedDebugger.cs` | Add `TryGetManagedLocals` |
| `src/Services/ManagedDebuggerService.cs` | Implement `TryGetManagedLocals` |
| `src/Services/EngineQueryService.cs` | Inject ICorDebugWrapper, scopes fallback, variable routing, clear calls |

## Verification

1. `dotnet build src/MixDbg.csproj -c Debug` -- zero warnings
2. `dotnet test test/UnitTests/UnitTests.csproj` -- all pass
3. End-to-end: set BP in WpfApp C# code, verify locals show names/types/values, expand objects to see fields, continue/step refreshes correctly

## Failed Approach: ICorDebug V4 ILFrame Walking (2026-04-12)

**Phase 3 implemented as planned** — `CorDebugWrapperService.InitializeManagedLocals` walked `Process.Threads` → chains → frames → `CorDebugILFrame`, then called `EnumerateLocalVariables()` / `EnumerateArguments()`. Value formatting covered all `CorElementType` variants (primitives via `Marshal` + `BitConverter`, strings, objects with field enumeration + superclass chain walk, arrays).

**Result: complete failure at runtime.** `Process.Threads` enumeration throws immediately:

```
DebugException: Error HRESULT CORDBG_E_READVIRTUAL_FAILURE has been returned from a call to a COM component.
```

The piggybacked ICorDebug V4 process (created via `OpenVirtualProcessImpl` with `DbgEngDataTarget` bridge) cannot enumerate threads. The data target bridge maps memory reads, but the V4 piggybacked mode doesn't support the full `ICorDebugProcess` threading API. This is the same fundamental limitation that forced M4 to use the CLR profiler instead of ICorDebug for managed breakpoints.

**Alternatives considered:**
- **DAC (`SOSDacInterface` / `XCLRDataProcess`)**: The ClrDebug 0.3.4 NuGet exposes module/method-level DAC APIs but not stack frame or local variable APIs. No `GetStackReferences`, no frame-level locals.
- **CLR Profiler (`ICorProfilerInfo2::DoStackSnapshot`)**: Could walk the managed stack from inside the target, but there's no profiler API for local variable stack offsets. The JIT's local layout is in GC info (undocumented, version-dependent), not accessible via `ICorProfilerInfo`.
- **SOS via dbgeng (`!clrstack -l`)**: The DAC already has all GC info parsing built in. SOS commands work through dbgeng even when ICorDebug thread enumeration doesn't, because SOS reads memory directly via the data target. Text output parsing is fragile but the infrastructure (`ExecuteCommand` + `OutputCapture`) already exists.

**Chosen replacement: SOS via dbgeng.** Run `!clrstack -l` with output capture, parse the text for local names/values/addresses. Phases 1, 2, 4, 5, 6 remain unchanged — only Phase 3's `InitializeManagedLocals` internals change.
