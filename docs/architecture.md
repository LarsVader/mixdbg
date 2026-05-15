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

### Debuggee Output Writer Thread
Drains `NativeDebuggerModel.DebuggeeOutputQueue` (a `BlockingCollection<string>`) and emits DAP `output` events. Decouples dbgeng's `IDebugOutputCallbacks` callback (which runs on the engine thread) from `IDapServer.SendEvent` so a slow stdout reader on the client side cannot back-pressure the engine thread. See "Debuggee Output Forwarding" below.

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
- `ISourceFileService.IsNativeFile`: rejects `.cs` files AND `.cpp` files in C++/CLI projects (scans vcxproj for CLR indicators: `<CLRSupport>`, `<CLRImageType>`, `<CompileAsManaged>`, `/clr`). For `.cpp` files where vcxproj detection fails (large projects, deep nesting), `BreakpointService` has a C++/CLI fallback that tries `ResolveMethodFromCliFile` via dbgeng's Windows PDB before falling back to native `bu`.
- Late-loaded C++/CLI modules: BPs stored in `PendingILBreakpoints` are retried on every `OnLoadModule` event after `ManagedInitialized`. Additionally, `TryInitializeManaged` retries pending BPs immediately after CLR init to catch modules that loaded before coreclr (their `OnLoadModule` was skipped because `ManagedInitialized` was false).

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

`FunctionIDMapper` selectively enables hooks for watched methods. Only exact token matches get ENTER/LEAVE hooks:
- **Pre-launch watches** (`MIXDBG_WATCH_TOKENS`): C# methods — resolved from portable PDBs at pre-launch time
- **Mid-session watches** (`WATCH:Assembly:Token`): sent via command pipe when breakpoints are set after launch (both C# and C++/CLI). C++/CLI tokens are resolved from PDBs once the module loads in dbgeng.

Named pipe protocol:
- `READY:\n` — profiler initialization complete
- `JIT:TOKEN:ADDRESS:SIZE:ASSEMBLY[:IL-map]\n` — JIT compilation notification
- `ENTER:TOKEN:BODYADDR:THREADID:ASSEMBLY\n` — method entry (blocks on `MIXDBG_ACK_EVENT`). HW BP address is clamped to `max(mapped, BodyAddress)` so it's not placed before where execution resumes after ACK.
- `LEAVE:TOKEN:THREADID:ASSEMBLY\n` — method exit (fire-and-forget). If LEAVE arrives in the same notification batch as ENTER (tiny method), it's deferred to the next engine stop so the HW BP has a chance to fire.
- `TAILCALL:TOKEN:THREADID:ASSEMBLY\n` — tail call exit (fire-and-forget, same deferral as LEAVE)
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
- **ENTER fast-path (no plan)**: If an ENTER notification arrives for a method with no BP plan and no active breakpoints, `ParseEnterNotification` ACKs immediately on the pipe reader thread without interrupting the engine.

### Managed Stack Traces

Profiler's `JitMethodMap` (sorted by native address) maps any IP in JIT'd code to method token + assembly. `ResolveFrameFromProfilerData` binary-searches the map, reverse-maps native IP → IL offset via `JitMethodMappings`, then uses `PdbSourceMapperService` for method name + exact source file:line.

### Source Resolution

- **C#**: portable PDBs read by `PdbSourceMapperService` via `System.Reflection.Metadata`
- **C++/CLI**: Windows PDBs read natively by dbgeng's `GetLineByOffset`

### Module Tracking

`ICorDebugWrapper.GetModules()` walks `ICorDebugProcess.AppDomains` → assemblies → modules. Called on init and on each dbgeng LoadModule event for managed DLLs. Pending breakpoints bind when their module becomes available.

### Attach to Running Process (M7)

The launch path injects the profiler via `CORECLR_*` env vars set in `ProfilerPipeService.SetupProfilerPipe` before `CreateProcess`. Env vars cannot be retroactively set on a running process, so the attach path uses two CoreCLR mechanisms instead:

1. **Diagnostic IPC** (`\\.\pipe\dotnet-diagnostic-{PID}`) — `ProfilerAttachIpcService` sends the `AttachProfiler` command (command set `0x03`, command id `0x01`) carrying the profiler CLSID, DLL path, and a binary client-data blob. The blob (`ProfilerClientDataBuilder`) holds the pipe names and watch tokens that env vars carry in launch mode.
2. **`ICorProfilerCallback3::InitializeForAttach`** — invoked by the runtime once the profiler DLL is loaded. The profiler reads the client-data blob, opens the named pipe + ACK event + cmd pipe, and sets event mask `COR_PRF_MONITOR_JIT_COMPILATION | COR_PRF_ENABLE_REJIT`. **`COR_PRF_MONITOR_ENTERLEAVE` is not in `COR_PRF_ALLOWABLE_AFTER_ATTACH`** — runtime ENTER/LEAVE hooks never fire for an attached profiler.

Sequencing in `EngineLifecycleService.AttachOrCreateProcess`:

1. `SetupProfilerPipeForAttach` — create pipes, build client data, send `AttachProfiler` IPC, wait for `READY:attach` from the profiler. **Done before** `_wrapper.AttachProcess` because dbgeng suspends every thread in the target — including the runtime thread that processes the AttachProfiler request — and would deadlock the IPC round-trip.
2. `_wrapper.AttachProcess` — invasive dbgeng attach.
3. **Drain the dbgeng attach replay** — first `WaitForEvent` returns at the CreateProcess event; subsequent module-load events arrive only when the engine is in GO state. The drain runs a Go → SetInterrupt → WaitForEvent cycle until coreclr is observed (capped at 50 iterations × ~50 ms = ~3 s for processes with deep module-load chains), which fires every queued `OnLoadModule` callback. Without this, ClrLoaded would only flip later, after some user-triggered module load — too late for managed BPs to bind before code runs through them. Each iteration also calls `ProcessProfilerNotifications` so JITs that arrive during the drain don't stall on the runtime's 500 ms ACK timeout.
4. `TryInitializeManaged` — same `ICorDebug` V4 bootstrap as launch, just kicked off explicitly here because no CLR-notification exception fires for an already-initialized CLR.
5. `SkipNextWaitForEvent = true` — tells the engine loop's first iteration to skip its `WaitForEvent` (the target is already stopped at the attach breakin) and go straight to processing queued DAP commands (setBreakpoints, configurationDone).

**Eager HW BP install** — `ManagedBreakpointResolverService.InstallEagerHardwareBp`. In attach mode (`NativeDebuggerModel.IsRejitMode = true`), every JIT notification that matches a deferred BP installs a HW BP immediately at the IL-mapped native address, instead of waiting for an ENTER hook that will never fire. The profiler's `JITCompilationFinished` blocks the JIT thread on the ACK event for watched methods (500 ms timeout) so the HW BP is in place before the rejitted method body executes; the engine signals the ACK after `FoldJitIntoPlans` runs — unconditionally in attach mode, so a JIT that didn't match an installable site still unblocks promptly instead of stalling on the timeout.

**Limitation**: attach-mode managed BPs are permanent (no LEAVE-driven cleanup) and subject to the 4-concurrent x64 hardware-debug-register cap. The `GetReJITParameters` IL rewriter in `MixDbgProfiler.cpp` is currently a stub — implementing it would inject calls to `MixDbgHelper_Enter`/`Leave` (exported from the profiler DLL) at method entry/exit via `IMetaDataEmit2`-defined P/Invokes, restoring the unlimited-BPs behavior of M4V3. See the TODO block in `GetReJITParameters` for the implementation outline.

## Variable Inspection

### Native (M3)

`IDebugSymbolGroup2` via `SetScope` + `GetScopeSymbolGroup`. Expandable variables (structs/pointers with `SubElements > 0`) allocate child `variablesReference` handles in `VariableStore`. Invalidated on continue/step.

### Managed (M5)

Managed locals are read exclusively via SOS `!clrstack -a` through dbgeng output capture, then parsed for PARAMETERS/LOCALS sections of the top frame. The ICorDebug ILFrame path was tried first but always failed (`E_NOTIMPL` on chain/frame enumeration in the piggybacked V4 process — see `docs/failed-approaches.md`), so it has been removed. Variable names are enriched from portable PDB local scope tables (`GetLocalVariableNames`) and PE parameter metadata (`GetParameterNames`).

`ManagedVariableStore` allocates refs starting at 100,000 (native `VariableStore` starts at 1). `EngineQueryService.GetVariablesOnEngine` routes by `ManagedVariableStore.IsManaged(ref)`.

## Cross-Boundary Stepping (M6)

Stepping across C#, C++/CLI, and native C++ boundaries. Native frames use dbgeng's built-in stepping; managed frames set temporary hardware BPs at target addresses.

See [stepping-architecture.md](stepping-architecture.md) for the full stepping and breakpoint interaction reference.

**Step-over**: PDB sequence points → next IL offset → temp HW BP at native address. Step-out fallback BP in caller handles early returns (skipped for async MoveNext — `await` yields cause MoveNext to return, which would fire the fallback in framework code). Native step-over auto-re-steps on same line, auto-steps-out on closing braces or sourceless lines.

**Step-into**: Parses IL bytecode for `call`/`callvirt` target. Three paths: JitMethodMap hit (temp BP), profiler WATCH (one-shot plan site), or native `GetOffsetByName`. Fallback: step-over behavior.

**Step-out**: `FindStepOutTarget` walks stack, skips sourceless frames (C++/CLI thunks, JIT helpers), advances past call site to next sequence point.

## Diagnostic Logging

All sessions log to `~/mixdbg.log` via `ILoggingService` (with state in `LogStore`). Uses `[CallerFilePath]` to auto-tag log entries with the source file name. Writes to both in-memory `LogStore.Entries` and the log file.

## Debuggee Output Forwarding

`Trace.WriteLine`, `Debug.WriteLine`, and `OutputDebugString` calls from the debuggee surface in the DAP client console (e.g. nvim-dap REPL) via:

```
debuggee → OutputDebugString
        → dbgeng IDebugOutputCallbacks.Output(mask, text)
        → DebuggeeOutputForwarder (filters to DEBUG_OUTPUT_DEBUGGEE = 0x80)
        → DbgEngWrapperModel.OnDebuggeeOutput event (string)
        → EngineLifecycleService handler → NativeDebuggerModel.DebuggeeOutputQueue
        → DebuggeeOutputWriter thread → IDapServer.SendEvent("output", { category = "stdout", output })
```

Key points:

- `DebuggeeOutputForwarder` is a **persistent** `IDebugOutputCallbacks` installed via `IDebugClient.SetOutputCallbacks` once at engine creation. During `ExecuteCommandWithCapture` it's swapped out for a temporary `OutputCapture` so the command's output is captured into the SOS return string; the forwarder is restored in the `finally` block.
- The forwarder owns the dbgeng-mask filter: it forwards only `DEBUG_OUTPUT_DEBUGGEE` (0x80) text. Keeping the filter inside the EngineWrappers assembly means callers outside it never reference dbgeng mask constants — the consumer just sees `Action<string>`.
- The callback runs on the engine thread. The lifecycle handler enqueues to `DebuggeeOutputQueue` and returns immediately. The dedicated `DebuggeeOutputWriter` thread does the `SendEvent` so a stalled DAP client can't deadlock the engine thread inside dbgeng's callback.
- Disposed by `EngineLifecycleService.DisposeAction` in this order: set `Terminated`, `CompleteAdding()` on `Commands`, join the engine thread (the producer of `DebuggeeOutputQueue`), then `CompleteAdding()` on `DebuggeeOutputQueue`, then join the writer thread. The engine join goes first so the queue isn't completed while the engine thread is still producing — that ordering avoids `Add` racing against `CompleteAdding`/`Dispose`.
- Any output dbgeng emits *after* `Terminated` is set (e.g. during `TerminateSession`/`EndSession`) is intentionally dropped by the producer's self-suppress check. Preserving it isn't a goal; clean dispose sequencing is.
- `EngineLifecycleService.CreateEngine` subscribes to all `wrapperModel.On*` events **before** calling `_wrapper.CreateEngine`. Defensive ordering: dbgeng today only fires callbacks from `WaitForEvent` (which runs later in the engine loop), but subscribing up front guarantees no callbacks are lost if a future wrapper change ever invokes them during engine creation.

## DAP Startup Ordering: `initialize` Response Before `initialized` Event

The DAP spec requires the `initialize` **response** to reach the client before the `initialized` **event** is emitted. Violating that ordering deadlocks nvim-dap when no breakpoints are configured: nvim-dap's `event_initialized` handler calls `set_breakpoints`, whose `on_done` callback checks `self.capabilities.supportsConfigurationDoneRequest` to decide whether to send `configurationDone`. With zero breakpoints, `set_breakpoints` runs its `on_done` synchronously — but `self.capabilities` is only populated when the `initialize` response arrives, so an out-of-order event makes the check fail silently and `configurationDone` is never sent. The engine then sits in `ProcessCommandsUntilResume` forever.

`IDapAfterResponseAction` is the mechanism that enforces the ordering. Handlers that need to emit a follow-up message after their response opt in by implementing it; `DapDispatcherService` invokes `OnAfterResponse()` immediately after `SendResponse` returns. `InitializeRequestHandlerService` uses this to send `initialized` from `OnAfterResponse`, never from `ExecuteInternal`.

## Key Dependencies

- `dbgeng.dll` — ships with Windows (System32)
- `Microsoft.Extensions.DependencyInjection` — DI container
- `ClrDebug` NuGet — ICorDebug V4 COM interop wrappers
- dbgeng.h reference: `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/dbgeng.h`
