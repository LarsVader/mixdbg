# mixdbg — Architecture

## Design Principles

- **Stateless singletons**: all services are registered as singletons with no mutable state. Mutable state lives in model objects (`DapServerModel`, `DebugSessionModel`, `NativeDebuggerModel`, `DbgEngWrapperModel`, `CorDebugWrapperModel`).
- **DI container**: `Microsoft.Extensions.DependencyInjection`. `Program.cs` builds a `ServiceProvider` via `AddMixDbgCore()` + `AddEngineWrappers()`.
- **Interface-first**: every service has an interface in `Services/Interfaces/` and an implementation in `Services/`.
- **One public type per file**: file name matches the type name.
- **Isolation boundaries**: external COM libraries (dbgeng, ICorDebug/ClrDebug) are encapsulated behind wrapper services. No COM types leak into the main codebase.

## Service Responsibilities

### DAP Layer
- `IDapServer` / `DapServerService` — Content-Length framed JSON-RPC transport over stdin/stdout
- `IDapDispatcher` / `DapDispatcherService` — Routes DAP commands to handler services
- `IDapHandlerService` implementations — Auto-discovered via assembly scanning. Each extends `DapHandlerServiceBase<TResponse, TArgs>` or `DapVoidHandlerServiceBase<TArgs>`. Handlers contain their own session logic (no separate session orchestrator).

### Engine Layer
- `IEngineLifecycleService` — Engine thread management, `WaitForEvent` loop, process lifecycle
- `IBreakpointService` — Native/managed breakpoint management, hit callbacks, classification
- `IEngineQueryService` — Stack trace, scopes, variables, threads
- `ISteppingService` — Execution control (continue, step over/into/out), managed step temp BPs

### Managed Debugging Layer
- `IManagedDebugger` — CLR runtime lifecycle, managed stack frame resolution via profiler JIT map
- `IManagedBreakpointService` — Managed BP setting/removal via PDB resolution + hardware BPs
- `IManagedBreakpointResolver` — Deferred BP resolution via JIT notifications, ENTER/LEAVE hooks
- `IProfilerPipeService` — Named pipe to CLR profiler, JIT/ENTER/LEAVE notification parsing

### Wrapper Layer (MixDbg.EngineWrappers)
- `IDbgEngWrapper` / `DbgEngWrapperService` — All dbgeng COM interop
- `ICorDebugWrapper` / `CorDebugWrapperService` — All ICorDebug V4 / ClrDebug interop
- `IPdbSourceMapper` / `PdbSourceMapperService` — Portable PDB reading via `System.Reflection.Metadata`

## Isolation Boundaries

### DbgEng COM Isolation

All dbgeng COM interop is encapsulated behind `IDbgEngWrapper`. COM interface types (`IDebugClient`, `IDebugControl`, etc.) are `internal` to `Engine.DbgEng` and stored on `DbgEngWrapperModel` (also `internal` properties). The rest of the codebase uses only the wrapper's public API: `EngineExecutionStatus`, `NativeStackFrame`, `VariableInfo`, `EngineEventInfo`.

Engine callback events (breakpoint hit, module load, etc.) are exposed as C# events on `DbgEngWrapperModel`. `NativeDebuggerModel` holds a `DbgEngWrapperModel Wrapper` property.

### ICorDebug V4 Isolation

All ClrDebug NuGet types (`CorDebugProcess`, `SOSDacInterface`, `CorDebugValue` subtypes, etc.) are encapsulated behind `ICorDebugWrapper`. ClrDebug types are `internal` on `CorDebugWrapperModel` (including `ManagedVariableStore` for `CorDebugValue` refs). The rest of the codebase uses `ManagedModuleInfo`, `RawManagedFrame`, `VariableInfo`, and wrapper methods. `NativeDebuggerModel` holds a `CorDebugWrapperModel CorWrapper` property.

## Threading Model

Three threads, one command queue:

### Main Thread
Reads DAP requests from stdin, dispatches to handlers. Handlers marshal work to the engine thread in two ways:
- **Fire-and-forget**: `model.Commands.Add(() => ...)`
- **Synchronous query**: `model.QueueEngineQuery(() => ...)` — queues a command + `TaskCompletionSource`, blocks until the engine thread executes it. Also calls `SetInterrupt` when the engine is in `WaitForEvent`, so mid-session commands are processed immediately.

### Engine Thread
All dbgeng COM calls happen here (thread affinity required). Runs `WaitForEvent` loop. When the target stops, processes queued commands, sends DAP events via `IDapServer`.

**Thread affinity**: ALL dbgeng calls (`DebugCreate`, `CreateProcess`, `WaitForEvent`, `GetStackTrace`, etc.) MUST happen on this thread. `EngineLifecycleService` enforces this by exposing only `OnEngine`-suffixed methods and thread-safe methods (`Break`, `Terminate`, `Detach`).

### Profiler Reader Thread
Reads JIT/ENTER/LEAVE notifications from the named pipe connected to `MixDbgProfiler.dll` (running in-process in the target). Enqueues notifications to `ProfilerNotifications` and calls `SetInterrupt` to wake the engine thread when a notification matches a deferred breakpoint.

## dbgeng COM Interop Details

Inside the wrapper boundary (`Engine/DbgEng/`, `DbgEngWrapperService`):

- Uses `[ComImport]` + `_VtblGap` for vtable layout. All vtable positions verified against `dbgeng.h`.
- **String output**: `IntPtr` + `Marshal.PtrToStringAnsi` — NOT `StringBuilder` (defaults to UTF-16 on .NET, dbgeng writes ANSI).
- **Stack frames**: `IntPtr` + `Marshal.PtrToStructure` for `GetStackTrace` — COM array marshaling with `[PreserveSig]` doesn't copy data back.
- `DEBUG_STACK_FRAME` is 128 bytes: 15 x `ulong` + `int Virtual` + `uint FrameNumber`.

### DEBUG_STATUS Constants

Exposed as `EngineExecutionStatus` enum (public). Internal dbgeng values:
```
NO_CHANGE = 0    GO = 1    GO_HANDLED = 2    GO_NOT_HANDLED = 3
STEP_OVER = 4    STEP_INTO = 5    BREAK = 6    NO_DEBUGGEE = 7
```

### Event Callback Return Values

`IDebugEventCallbacks` methods return a `DEBUG_STATUS` that tells dbgeng what to do:
- `BREAK` (6) → `WaitForEvent` returns (engine can process commands)
- `GO` (1) → continue running (`WaitForEvent` does NOT return)

Current settings: `Breakpoint` → BREAK, `CreateProcess` → BREAK, `ExitProcess` → BREAK, `Exception` → BREAK, `LoadModule` → GO, threads → GO.

## Process Startup Sequence

1. **Pre-configDone**: First `WaitForEvent` return → `ConfigDone` is false → enter `ProcessCommandsUntilResume`. Process queued commands (setBreakpoints, then Continue from configurationDone).
2. **`ConfigDone` is set by the Continue command ON THE ENGINE THREAD** — not from the main thread. This avoids a race where configDone is true before the engine processes initial events.
3. **Post-configDone system stops**: auto-continue silently (no DAP event).
4. **User breakpoint / step / pause**: send DAP `stopped` event, enter command loop.

## Native Breakpoints

- Before engine exists: stored as pending in `DebugSessionModel.PendingBreakpoints`, applied in `ConfigurationDone`.
- At initial stop, `GetOffsetByLine` usually fails (module not loaded). Fallback: `bu` command (deferred breakpoint) — dbgeng resolves when module loads.
- Breakpoint IDs: dbgeng assigns 0-based IDs. Pending responses use IDs starting at 1000 to avoid collision.
- `UserBreakpointIds` HashSet tracks which dbgeng breakpoint IDs are user BPs (vs system breakpoints).
- `ISourceFileService.IsNativeFile`: rejects `.cs` files AND `.cpp` files in C++/CLI projects (scans vcxproj for `<CLRSupport>`). For `.cpp` files where vcxproj detection fails (large projects, deep nesting), `BreakpointService` has a C++/CLI fallback that tries `ResolveMethodFromCliFile` via dbgeng's Windows PDB before falling back to native `bu`.

## Managed Debugging

### CLR Detection and Initialization

`EventCallbacks.OnLoadModule` watches for `coreclr` module name → sets `model.ClrLoaded`, captures `CoreClrPath` and `CoreClrBaseAddress`. ICorDebug V4 initialization happens on the next engine stop (can't init during `GO` state).

`ICLRDebugging::OpenVirtualProcess` creates an `ICorDebugProcess` piggybacked on the existing dbgeng session via `DbgEngDataTarget` (implements `ICorDebugMutableDataTarget`). No second debugger, no conflicts. dbgeng owns the process; ICorDebug V4 reads/writes memory through the bridge.

### CLR Profiler (`MixDbgProfiler.dll`)

Native C++ DLL implementing `ICorProfilerCallback2`. CLR loads it at startup via `CORECLR_ENABLE_PROFILING` env vars set before `CreateProcess`.

Three mechanisms:
1. **`JITCompilationFinished`** — sends `JIT:token:address:size:assembly[:IL-map]` for stack trace resolution and IL-to-native mapping (for ALL methods, not just watched)
2. **`FunctionEnter`/`FunctionLeave`/`FunctionTailcall`** hooks (x64 MASM stubs in `EnterLeaveStubs.asm`) — fire on every call/return of watched methods
3. **`JITInlining`** — returns `*pfShouldInline = FALSE` for watched callees so hooks always fire

`FunctionIDMapper` selectively enables hooks for watched methods. Two watch granularities:
- **Exact token watches** (`MIXDBG_WATCH_TOKENS`): C# methods — resolved from portable PDBs at pre-launch time
- **Assembly-level watches** (`MIXDBG_WATCH_ASSEMBLIES`): C++/CLI assemblies — all methods hooked because tokens can't be resolved before module load

Named pipe protocol:
- `READY:\n` — profiler initialization complete
- `JIT:TOKEN:ADDRESS:SIZE:ASSEMBLY[:IL-map]\n` — JIT compilation notification
- `ENTER:TOKEN:BODYADDR:THREADID:ASSEMBLY\n` — method entry (blocks on `MIXDBG_ACK_EVENT`)
- `LEAVE:TOKEN:THREADID:ASSEMBLY\n` — method exit (fire-and-forget)
- `TAILCALL:TOKEN:THREADID:ASSEMBLY\n` — tail call exit (fire-and-forget)
- `WATCH:Assembly:TokenHex\n` — command pipe: enable hooks for a method mid-session

Profiler CLSID: `{D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}`. Uses `ICorProfilerInfo` vtable calls by slot index (no corprof.h header dependency).

### Method-Lifetime Managed Breakpoints (M4V3)

A managed BP lives as long as its method has at least one activation on the stack. Two model objects track BP state:

- **`ManagedMethodBreakpointPlan`** — declarative per-method config. Created when the user calls `setBreakpoints`. Holds one or more `MethodBreakpointSite` entries (one per BP line).
- **`ActiveMethodBreakpoint`** — runtime state for a method currently on the stack. Tracks `ActivationCount` and installed HW BP IDs.

State transitions:

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

Flow: user `setBreakpoints` → `BindResolvedMethod` creates a plan per (token, assembly). On `FunctionEnter` (count 0→1), MixDbg installs one HW BP per plan site at its exact line address (via `JitMethodMappings`), ACKs the profiler. The profiler blocks on `MIXDBG_ACK_EVENT` to guarantee the HW BP is installed before the method body runs. Nested/recursive ENTERs just `count++` and ACK immediately. On `FunctionLeave`/`FunctionTailcall` (count→0), all HW BPs for that method are removed. Continue/step do NOT touch BPs.

For mid-session BPs on already-JIT'd methods where `FunctionIDMapper` wasn't called with the token, `BindResolvedMethod` installs the HW BP immediately (no ENTER/LEAVE lifecycle — persists until user clears BP).

#### Edge cases and limits

- **Hardware BP budget**: x86-64 has 4 debug registers. More than 4 plan sites can exist, but `AddHardwareBreakpoint` fails once the budget is exhausted. A warning is logged; the site silently won't fire until a slot frees.
- **Recursion**: One HW slot per method regardless of recursion depth. First activation installs BP; recursive calls bump count.
- **Multi-threading**: `ActivationCount` increments once per ENTER regardless of thread. A method running on 2 threads has count=2 and holds the HW BP until both finish. Correct but coarser than per-thread tracking.
- **Tailcalls**: Treated identically to `Leave` — the current activation is ending.
- **Exception unwinds**: `FunctionLeave` is NOT called when an exception unwinds past the method. Activation counts can leak, keeping the HW BP installed. Self-heals on next `setBreakpoints` or process launch. A follow-up can hook `ExceptionUnwindFunctionLeave` if needed.
- **Assembly-level watches without BPs (C++/CLI)**: ENTER fires for every method in the watched assembly. For methods without a plan entry, MixDbg ACKs immediately (no HW BP work, no activation counting).

### Managed Stack Traces

Profiler's `JitMethodMap` (sorted by native address) maps any IP in JIT'd code to method token + assembly. `ResolveFrameFromProfilerData` binary-searches the map, reverse-maps native IP → IL offset via `JitMethodMappings`, then uses `PdbSourceMapperService` for method name + exact source file:line.

### Source Resolution

- **C#**: portable PDBs read by `PdbSourceMapperService` via `System.Reflection.Metadata`
- **C++/CLI**: Windows PDBs read natively by dbgeng's `GetLineByOffset`

### Module Tracking

`ICorDebugWrapper.GetModules()` walks `ICorDebugProcess.AppDomains` → assemblies → modules. Called on init and on each dbgeng LoadModule event for managed DLLs. Pending breakpoints bind when their module becomes available.

## Variable Inspection

### Native (M3)

`IDebugSymbolGroup2` via `SetScope` + `GetScopeSymbolGroup`. Expandable variables (structs/pointers with `SubElements > 0`) allocate child `variablesReference` handles in `VariableStore`. Invalidated on continue/step.

### Managed (M5)

SOS via dbgeng: `!clrstack -l` with output capture, parse text for local names/values. Variable names come from portable PDB local scope tables (`GetLocalVariableNames`) and PE parameter metadata (`GetParameterNames`).

`ManagedVariableStore` allocates refs starting at 100,000 (native `VariableStore` starts at 1). `EngineQueryService.GetVariablesOnEngine` routes by `ManagedVariableStore.IsManaged(ref)`.

## Cross-Boundary Stepping (M6)

Stepping across C#, C++/CLI, and native C++ boundaries. Native frames use dbgeng's built-in stepping; managed frames set temporary hardware BPs at target addresses.

See [stepping-architecture.md](stepping-architecture.md) for the full stepping and breakpoint interaction reference.

**Step-over**: PDB sequence points → next IL offset → temp HW BP at native address. Step-out fallback BP in caller handles early returns (skipped for async MoveNext — `await` yields cause MoveNext to return, which would fire the fallback in framework code). Native step-over auto-re-steps on same line, auto-steps-out on closing braces or sourceless lines.

**Step-into**: Parses IL bytecode for `call`/`callvirt` target. Three paths: JitMethodMap hit (temp BP), profiler WATCH (one-shot plan site), or native `GetOffsetByName`. Fallback: step-over behavior.

**Step-out**: `FindStepOutTarget` walks stack, skips sourceless frames (C++/CLI thunks, JIT helpers), advances past call site to next sequence point.

## Diagnostic Logging

All sessions log to `~/mixdbg.log` via `ILoggingService` (with state in `LogStore`). Uses `[CallerFilePath]` to auto-tag log entries with the source file name. Writes to both in-memory `LogStore.Entries` and the log file.

## Key Dependencies

- `dbgeng.dll` — ships with Windows (System32)
- `Microsoft.Extensions.DependencyInjection` — DI container
- `ClrDebug` NuGet — ICorDebug V4 COM interop wrappers
- dbgeng.h reference: `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/dbgeng.h`
