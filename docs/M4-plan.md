# M4: Managed Debugging via SOS + ClrMD

## Context

mixdbg currently debugs native C++ code via dbgeng COM interfaces (M1-M3). Managed breakpoints (C# and C++/CLI) return `verified: false` with "Managed breakpoints not yet supported". M4 adds managed debugging so that breakpoints, stack traces, and source navigation work for C#, C++/CLI, and mixed native/managed call stacks.

**Key constraint**: The test target CLRApp3 is C# WPF → C++/CLI wrapper → native C++. C++/CLI compiles to IL and uses Windows PDBs (not portable PDBs). Any solution must handle both C# and C++/CLI managed code.

## Approach: SOS + ClrMD

- **ClrMD** (`Microsoft.Diagnostics.Runtime`) for reading managed state: stack traces, method resolution (file:line → method name), module enumeration. ClrMD creates a `DataTarget` from our existing dbgeng client via `DataTarget.CreateFromDbgEng(pDebugClient)` — no second process attach, no threading conflict. Works with both C# portable PDBs and C++/CLI Windows PDBs.
- **SOS** for setting managed breakpoints (`!bpmd`) — ClrMD is read-only and cannot set breakpoints.
- **Output capture** (`IDebugOutputCallbacks`) to capture `!bpmd` text output and parse breakpoint IDs.

All operations happen on the engine thread via the existing command queue.

## Implementation Steps

### Step 1: Add ClrMD NuGet + output capture infrastructure

**NuGet**: Add `Microsoft.Diagnostics.Runtime` to `MixDbg.csproj`.

**Output capture** (needed for SOS `!bpmd` output):
- `Engine/DbgEng/Interfaces/IDebugOutputCallbacks.cs` — COM interface (GUID `4bf58045-d654-4c40-b0af-683090f356dc`). Single method after IUnknown: `Output(uint Mask, string Text)`
- `Engine/DbgEng/OutputCapture.cs` — Implements `IDebugOutputCallbacks`, accumulates text in `StringBuilder`.

**Modify `IDebugClient.cs`** — Break `_VtblGap4_18` (slots 25-42) to expose:
- `_VtblGap4_5` (slots 25-29: DispatchCallbacks..SetInputCallbacks)
- Slot 30: `GetOutputCallbacks(out IDebugOutputCallbacks Callbacks)`
- Slot 31: `SetOutputCallbacks(IDebugOutputCallbacks Callbacks)`
- `_VtblGap5_11` (slots 32-42: GetOutputMask..OutputIdentity)

### Step 2: SOS output parser

Pure string parsing for `!bpmd` output. Testable with no COM dependencies.

- `Engine/Sos/SosOutputParser.cs` — Static method:
  - `ParseBpmdOutput(string output) -> (bool success, uint? bpId, string? message)` — extracts breakpoint ID from `!bpmd` result text
- Tests: `SosOutputParserTests.cs`

### Step 3: IManagedDebugger service

**New interface `Services/Interfaces/IManagedDebugger.cs`:**
```
- InitializeRuntime(NativeDebuggerModel model) -> bool
  // Creates ClrMD DataTarget from dbgeng client, loads SOS
- SetManagedBreakpoints(NativeDebuggerModel model, string filePath, SourceBreakpoint[] requested) -> Breakpoint[]
  // Uses ClrMD to resolve file:line → method, then !bpmd to set bp
- GetManagedStackFrames(NativeDebuggerModel model) -> StackFrame[]
  // Uses ClrMD ClrThread.EnumerateStackTrace() for managed frames with source info
- bool IsInitialized(NativeDebuggerModel model)
```

**`Services/ManagedDebuggerService.cs`** implementation:

**`InitializeRuntime`**:
1. Get `IntPtr` to our `IDebugClient` COM object via `Marshal.GetIUnknownForObject`
2. `DataTarget.CreateFromDbgEng(ptr)` to create ClrMD data target
3. `dataTarget.ClrVersions[0].CreateRuntime()` to get `ClrRuntime`
4. Execute `.loadby sos coreclr` (fallback: `.load ~/.dotnet/sos/sos.dll`)
5. Store runtime + data target on model

**`SetManagedBreakpoints`** (called on engine thread):
1. Use ClrMD `ClrRuntime` to enumerate modules → methods → sequence points
2. Find the method containing the requested file:line
3. Execute `!bpmd <AssemblyName> <Namespace.Type.Method>` via `OutputCapture`
4. Parse output to get dbgeng breakpoint ID
5. Track in `model.ManagedBreakpointIds`

**`GetManagedStackFrames`** (called on engine thread):
1. Flush/refresh the ClrMD runtime (`runtime.FlushCachedData()`)
2. Find the current thread via `ClrRuntime.Threads`
3. `thread.EnumerateStackTrace()` returns `ClrStackFrame` objects with:
   - `InstructionPointer` — for matching against native frames
   - `Method?.Type?.Name` + `Method?.Name` — full managed method name
   - `Method?.GetSourceLocation()` — file path + line (reads PDB, works for both portable and Windows PDBs)
4. Convert to DAP `StackFrame[]`

**Register in DI:** `ServiceCollectionExtensions.cs`

### Step 4: Model changes + CLR detection

**Modify `NativeDebuggerModel.cs`** — add fields:
- `volatile bool ClrLoaded` — set when coreclr module loads
- `volatile bool ManagedInitialized` — set after ClrMD + SOS init succeeds
- `HashSet<uint> ManagedBreakpointIds` — tracks managed bp IDs (separate from native `UserBreakpointIds`)
- `List<SetBreakpointsArguments> PendingManagedBreakpoints` — managed bps queued before CLR loads
- `ClrRuntime? Runtime` — ClrMD runtime reference (used on engine thread only)
- `DataTarget? DataTarget` — ClrMD data target (disposed on cleanup)

**Modify `NativeDebuggerService.CreateEngine`** — detect CLR in the existing `OnLoadModule` handler:
```csharp
model.Callbacks.OnLoadModule += (mod, img) =>
{
    if (!model.ClrLoaded && mod != null &&
        mod.Equals("coreclr", StringComparison.OrdinalIgnoreCase))
        model.ClrLoaded = true;
};
```

### Step 5: Wire managed breakpoints into the pipeline

**Modify `NativeDebuggerService.SetBreakpointsOnEngine`:**
- When `!_sourceFiles.IsNativeFile(filePath)` and file is `.cs` or C++/CLI: delegate to `IManagedDebugger.SetManagedBreakpoints`
- If `!model.ManagedInitialized`: store in `model.PendingManagedBreakpoints`, return `verified: true` (optimistic)

**Modify `NativeDebuggerService.EngineLoop`** — after each stop, if `model.ClrLoaded && !model.ManagedInitialized`:
- Call `_managedDebugger.InitializeRuntime(model)`
- If successful, apply all `PendingManagedBreakpoints`
- Set `model.ManagedInitialized = true`

**Modify `NativeDebuggerService.OnBreakpoint`:**
- Check `model.ManagedBreakpointIds` in addition to `UserBreakpointIds` — SOS `!bpmd` creates real dbgeng breakpoints that fire through the same callback

**Modify `DebugSessionService.SetBreakpoints`:**
- Remove the early "managed not supported" response for non-native files — let it flow through to the engine

### Step 6: Merge managed + native stack traces

**Modify `NativeDebuggerService.GetStackTraceOnEngine`:**
1. Get native frames via `GetStackTrace` (existing code)
2. If `model.ManagedInitialized`, get managed frames via `_managedDebugger.GetManagedStackFrames(model)`
3. Build a lookup: managed frame IP → managed frame data
4. For each native frame: if its IP matches a managed frame, replace name + source with managed info
5. This produces a unified mixed-mode stack trace

### Step 7: Update SourceFileService

**Modify `ISourceFileService` / `SourceFileService`:**
- Add `bool IsManagedFile(string path)` — returns `true` for `.cs` files and C++/CLI `.cpp` files (detected via `<CLRSupport>` in vcxproj — the inverse of the existing native check)

### Step 8: Tests

- `SosOutputParserTests.cs` — parse `!bpmd` output formats
- `ManagedDebuggerServiceTests.cs` — mock ClrMD types + IDebugControl.Execute, verify correct SOS commands and method resolution
- Update `NativeDebuggerServiceTests.cs` — test managed breakpoint delegation path
- Update `DebugSessionServiceTests.cs` — test .cs breakpoints no longer return "not supported"

### Step 9: Update CLAUDE.md and README.md

- Mark M4 as DONE
- Add `IManagedDebugger` to project structure and architecture diagram
- Document ClrMD integration (`DataTarget.CreateFromDbgEng`) and SOS loading
- Add `Engine/Sos/` directory to structure listing
- Update key dependencies with ClrMD NuGet

## Files to Create
| File | Purpose |
|------|---------|
| `Engine/DbgEng/Interfaces/IDebugOutputCallbacks.cs` | COM interface for output capture |
| `Engine/DbgEng/OutputCapture.cs` | IDebugOutputCallbacks impl |
| `Engine/Sos/SosOutputParser.cs` | Parse `!bpmd` text output |
| `Services/Interfaces/IManagedDebugger.cs` | Managed debugging interface |
| `Services/ManagedDebuggerService.cs` | ClrMD + SOS wrapper service |

## Files to Modify
| File | Change |
|------|--------|
| `MixDbg.csproj` | Add `Microsoft.Diagnostics.Runtime` NuGet |
| `Engine/DbgEng/Interfaces/IDebugClient.cs` | Expose slots 30-31 (Get/SetOutputCallbacks) |
| `Models/NativeDebuggerModel.cs` | Add ClrLoaded, ManagedInitialized, ManagedBreakpointIds, PendingManagedBreakpoints, Runtime, DataTarget |
| `Services/NativeDebuggerService.cs` | Inject IManagedDebugger, delegate managed bps, merge stack traces, init on CLR detect |
| `Services/DebugSessionService.cs` | Remove "managed not supported" early return |
| `Services/Interfaces/ISourceFileService.cs` | Add IsManagedFile method |
| `Services/SourceFileService.cs` | Implement IsManagedFile |
| `ServiceCollectionExtensions.cs` | Register IManagedDebugger |

## Verification

1. **Unit tests**: `dotnet test src/MixDbg.Tests/MixDbg.Tests.csproj` — all existing + new tests pass
2. **Build**: `dotnet build src/MixDbg/MixDbg.csproj -c Debug` — zero warnings
3. **End-to-end** (manual): Launch CLRApp3 via mixdbg from nvim-dap:
   - Set breakpoint in C# `.cs` file → verify it hits
   - Set breakpoint in C++/CLI `.cpp` file → verify it hits
   - Inspect stack trace at a managed breakpoint → see C# method names + source locations
   - Inspect stack trace at a native↔managed boundary → see unified mixed-mode frames
