# mixdbg — Mixed-Mode DAP Debug Adapter

A custom DAP adapter wrapping Windows `dbgeng.dll` for simultaneous C# and native C++ debugging from Neovim's nvim-dap. See `README.md` for a detailed explanation of DAP, dbgeng, and the architecture.

## Build

```bash
dotnet build src/MixDbg.csproj -c Debug
```

Output: `src/bin/Debug/net10.0/win-x64/MixDbg.exe`

### Profiler DLL

The CLR profiler is a native C++ DLL that sends JIT notifications to MixDbg via a named pipe. Build from `profiler/`:

```bash
cd profiler && make all
```

Output: `profiler/x64/Debug/MixDbgProfiler.dll`

MixDbg looks for the DLL next to its exe, or in `profiler/x64/Debug/` during development.

## Test Target

The `test/TestApp/` directory contains a mixed-mode WPF app (C# frontend → C++/CLI wrapper → native C++ library) used as the integration test target. Build with `make all` from `test/TestApp/`.

## nvim-dap Integration

Adapter registered in `C:\Users\LarsVader\AppData\Local\nvim\lua\plugins\debug\nvim-dap.lua` as `mixdbg` (type: executable, hardcoded path to the built exe). A "Mixed C#/C++ (mixdbg)" config is in both `dap.configurations.cpp` and `dap.configurations.cs`.

## Project Structure

```
src/
  MixDbg.csproj                  # Main project (exe) — DAP protocol, handlers, orchestration
  MixDbg.EngineWrappers/         # Class library — all external COM interop (dbgeng, ICorDebug, PDB)
    MixDbg.EngineWrappers.csproj # References ClrDebug NuGet; all internal types enforced by assembly boundary
    Engine/
      DbgEng/
        Constants/               # Internal: DbgEngNative, DebugStatus, DebugAttach, CreateProcessFlags, etc.
        Interfaces/              # Internal: IDebugClient, IDebugControl, IDebugSymbols, IDebugBreakpoint, etc.
        EventCallbacks.cs        # Internal: IDebugEventCallbacks implementation
        OutputCapture.cs         # Internal: IDebugOutputCallbacks implementation
      CorDebug/
        DbgEngDataTarget.cs      # Internal: ICorDebugMutableDataTarget bridge
        DbgEngClrDataTarget.cs   # Internal: ICLRDataTarget bridge for DAC
        DbgShimBootstrap.cs      # Internal: ICorDebug bootstrap via dbgshim.dll
        ManagedCallbackHandler.cs # Internal: ICorDebug managed callback wrapper
        UnmanagedCallbackHandler.cs # Internal: ICorDebug unmanaged callback wrapper
        RuntimeLibraryProvider.cs # Internal: finds mscordbi.dll next to coreclr.dll
      Sos/
        PdbSourceMapperService.cs # Internal: reads portable PDBs, implements IPdbSourceMapper (singleton)
        SosOutputParser.cs       # Internal: parses SOS command output (unused)
    Models/
      DbgEngWrapperModel.cs      # Public: dbgeng COM wrapper state (COM refs internal), engine events
      CorDebugWrapperModel.cs    # Public: ICorDebug V4 wrapper state (ClrDebug refs internal)
      VariableStore.cs           # Internal: variablesReference → symbol group mapping
      EngineExecutionStatus.cs   # Public enum: Go, StepOver, StepInto, Break, etc.
      EngineEventInfo.cs         # Public record: last debug event info
      NativeStackFrame.cs        # Public record struct: instruction pointer
      VariableInfo.cs            # Public record struct: resolved variable data
      ManagedModuleInfo.cs       # Public record: loaded managed module info
      RawManagedFrame.cs          # Public record: raw managed frame (token, module, IL offset, name)
      LogEntry.cs                # Public: immutable log record
      LogStore.cs                # Public: mutable log state
    Services/
      DbgEngWrapperService.cs    # Internal: implements IDbgEngWrapper
      CorDebugWrapperService.cs  # Internal: implements ICorDebugWrapper (thin COM wrapper)
      Interfaces/
        IDbgEngWrapper.cs        # Public: stateless dbgeng COM wrapper interface
        ICorDebugWrapper.cs      # Public: stateless ICorDebug V4 wrapper interface
        ILoggingService.cs       # Public: logging interface
        IPdbSourceMapper.cs      # Public: PDB source resolution interface
    ServiceCollectionExtensions.cs # AddEngineWrappers() — registers all engine services
  Program.cs                     # Entry point — DI composition root
  ServiceCollectionExtensions.cs # AddMixDbgCore() — registers DAP + orchestration services
  Models/
    DapMessages/                 # DAP protocol types (namespace MixDbg.Models.Dap), one file per type
      Protocol/                  # ProtocolMessage, RequestMessage, ResponseMessage, EventMessage, Source, DisconnectException, EmptyArguments
      Initialize/                # InitializeRequestArguments, Capabilities
      Lifecycle/                 # LaunchRequestArguments, AttachRequestArguments, DisconnectArguments
      Breakpoints/               # SetBreakpointsArguments, SourceBreakpoint, Breakpoint, SetBreakpointsResponseBody
      Execution/                 # ContinueArguments, ContinueResponseBody, StepArguments
      Inspection/                # StackTrace*, Scopes*, Variables*, Variable, StackFrame, Scope, Evaluate*
      Threads/                   # ThreadsResponseBody, DapThread
      Events/                    # StoppedEventBody, OutputEventBody, BreakpointEventBody, Terminated/InitializedEventBody
    DapServerModel.cs            # DAP transport state: streams, write lock, sequence counter
    DebugSessionModel.cs         # Session state: engine ref, pending breakpoints, SessionState enum
    NativeDebuggerModel.cs       # Engine state: DbgEngWrapperModel + CorDebugWrapperModel refs, threads, flags, breakpoint tracking
  Services/
    Interfaces/
      IDapServer.cs              # Stateless DAP transport — all methods take DapServerModel
      IDapDispatcher.cs          # Stateless request dispatcher — Run() dispatches to handler services
      IDapHandlerService.cs      # Handler interface: Command + Execute(JsonElement?)
      IDapMessage.cs             # Marker interface for DAP response types
      IEngineLifecycleService.cs         # Stateless engine lifecycle — engine thread, event loop, break/terminate/detach
      IBreakpointService.cs      # Stateless breakpoint management — set, remove, hit handling
      IEngineQueryService.cs     # Stateless engine queries + execution control — stack trace, scopes, variables, threads, continue, step
      ISourceFileService.cs      # IsNativeFile(string path), IsManagedFile(string path)
      IManagedDebugger.cs        # Stateless managed debugging — BP orchestration, frame merging
      IProfilerPipeService.cs    # Profiler pipe setup and reader thread
    DapServerService.cs          # IDapServer: Content-Length framed JSON-RPC transport
    DapDispatcherService.cs      # IDapDispatcher: command routing via DI-resolved handler services
    EngineLifecycleService.cs     # IEngineLifecycleService: engine thread, event loop, process lifecycle
    BreakpointService.cs         # IBreakpointService: native/managed/deferred breakpoint management, hit callbacks
    EngineQueryService.cs        # IEngineQueryService: stack trace, scopes, variables, threads, execution control
    ManagedDebuggerService.cs    # IManagedDebugger: managed BP lifecycle, profiler interaction, frame merging
    ProfilerPipeService.cs       # IProfilerPipeService: named pipe to CLR profiler, JIT/ENTER notification parsing
    LoggingService.cs            # ILoggingService: file + in-memory logger, [CallerFilePath] sender
    SourceFileService.cs         # ISourceFileService: native vs managed/CLI file detection
    Handlers/
      DapHandlerServiceBase.cs   # Base class for handlers with response body
      DapVoidHandlerServiceBase.cs # Base class for handlers without response body
      Initialize/                # initialize
      Lifecycle/                 # launch, attach, configurationDone, disconnect, terminate, threads
      Breakpoints/               # setBreakpoints, setFunctionBreakpoints, setExceptionBreakpoints
      Execution/                 # continue, next, stepIn, stepOut, pause
      Inspection/                # stackTrace, scopes, variables, evaluate, source, loadedSources
profiler/
  MixDbgProfiler.cpp                 # CLR profiler DLL — ICorProfilerCallback2, sends JIT notifications via named pipe
  MixDbgProfiler.vcxproj             # Native C++ build config (MSBuild, v145 toolset)
  MixDbgProfiler.def                 # DLL exports: DllGetClassObject, DllCanUnloadNow
  Makefile                           # Build shortcut (make all)
test/
  UnitTests/                         # xUnit + NSubstitute unit tests
  IntegrationTests/                  # End-to-end tests against TestApp
  TestApp/                           # Mixed-mode WPF integration test target
  TestApp.sln                        # Solution: NativeLib + CliWrapper + WpfApp
  Makefile                           # Build via MSBuild (make all)
  NativeLib/                         # Native C++ library (Calculator::Add/Multiply)
  CliWrapper/                        # C++/CLI wrapper (ManagedCalculator)
  WpfApp/                            # C# WPF frontend — --auto-test / --auto-test-slow for CI
```

## Architecture

**DI container** (`Microsoft.Extensions.DependencyInjection`): `Program.cs` builds a `ServiceProvider` via `AddMixDbgCore()`. Services are stateless singletons; all mutable state lives in model objects registered as singletons. DAP request handling uses `IDapHandlerService` implementations auto-discovered via assembly scanning — each handler extends `DapHandlerServiceBase<TResponse, TArgs>` or `DapVoidHandlerServiceBase<TArgs>` and contains its own session logic (no separate session orchestrator layer). `NativeDebuggerModel` is created per-session (one per Launch/Attach) by `IEngineLifecycleService.CreateModel()`, stored in `DebugSessionModel.Engine`. The engine-facing functionality is split across three services: `IEngineLifecycleService` (engine lifecycle + event loop), `IBreakpointService` (breakpoint management + hit callbacks), and `IEngineQueryService` (stack trace, scopes, variables, threads, execution control). Handlers inject `IBreakpointService` or `IEngineQueryService` directly for engine-thread operations.

**DbgEng COM isolation**: All dbgeng COM interop is encapsulated behind `IDbgEngWrapper` / `DbgEngWrapperService`. COM interface types (`IDebugClient`, `IDebugControl`, etc.) are `internal` to the `Engine.DbgEng` namespace and stored on `DbgEngWrapperModel` (also `internal` properties). The rest of the codebase uses only the wrapper's public API: `EngineExecutionStatus`, `NativeStackFrame`, `VariableInfo`, `EngineEventInfo`. Engine callback events (breakpoint hit, module load, etc.) are exposed as C# events on `DbgEngWrapperModel`. `NativeDebuggerModel` holds a `DbgEngWrapperModel Wrapper` property. `ManagedDebuggerService` and `ProfilerPipeService` also use `IDbgEngWrapper` for their COM needs (breakpoints, symbols, SetInterrupt).

**ICorDebug V4 isolation**: All ClrDebug NuGet package types (`CorDebugProcess`, `SOSDacInterface`, `XCLRDataProcess`, etc.) are encapsulated behind `ICorDebugWrapper` / `CorDebugWrapperService`. ClrDebug types are `internal` on `CorDebugWrapperModel`. The rest of the codebase uses `ManagedModuleInfo`, `RawManagedFrame`, and wrapper methods. `NativeDebuggerModel` holds a `CorDebugWrapperModel CorWrapper` property. `ManagedDebuggerService` delegates all ICorDebug operations (runtime init, module enumeration, stack traces, DAC resolution) to `ICorDebugWrapper`. The wrapper is a thin COM call layer — PDB source resolution and orchestration logic stay in `ManagedDebuggerService`.

Three threads, one command queue:

- **Main thread**: reads DAP requests from stdin, dispatches to handlers. Handlers own the dispatching to the engine thread: fire-and-forget via `model.Commands.Add(() => ...)`, or synchronous via `model.QueueEngineQuery(() => ...)` which queues a command + `TaskCompletionSource` and blocks until the engine thread executes it.
- **Engine thread**: all dbgeng COM calls happen here (thread affinity required). Runs `WaitForEvent` loop. When target stops, processes queued commands, sends DAP events via `IDapServer`.
- **Profiler reader thread**: reads JIT notifications from the named pipe connected to `MixDbgProfiler.dll` (running in-process in the target). Enqueues `JitNotification` records and calls `SetInterrupt` to wake the engine thread when a notification matches a deferred breakpoint.

## Critical Implementation Details

### dbgeng COM Interop

All dbgeng COM interaction is encapsulated in `DbgEngWrapperService` (implements `IDbgEngWrapper`). No COM types leak outside the wrapper boundary (`Engine/DbgEng/`, `DbgEngWrapperModel`, `DbgEngWrapperService`, `VariableStore`). The rest of the codebase uses `EngineExecutionStatus`, `NativeStackFrame`, `VariableInfo`, and `EngineEventInfo` instead.

Internal implementation details (inside the wrapper):
- Uses `[ComImport]` + `_VtblGap` for vtable layout. All vtable positions verified against `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/dbgeng.h`.
- **All string output buffers use `IntPtr` + `Marshal.PtrToStringAnsi`** — NOT `StringBuilder` (defaults to UTF-16 on .NET, dbgeng writes ANSI).
- **`GetStackTrace` frames array uses `IntPtr` + `Marshal.PtrToStructure`** — COM array marshaling with `[PreserveSig]` doesn't copy data back.
- `DEBUG_STACK_FRAME` is 128 bytes: 15 × `ulong` + `int Virtual` + `uint FrameNumber`.

### DEBUG_STATUS Constants (dbgeng.h)

Exposed as `EngineExecutionStatus` enum (public). Internal dbgeng values:
```
NO_CHANGE = 0    GO = 1    GO_HANDLED = 2    GO_NOT_HANDLED = 3
STEP_OVER = 4    STEP_INTO = 5    BREAK = 6    NO_DEBUGGEE = 7
```

### Event Callback Return Values

The return value from `IDebugEventCallbacks` methods tells dbgeng what to do:
- `BREAK` (6) → `WaitForEvent` returns (engine can process commands)
- `GO` (1) → continue running (`WaitForEvent` does NOT return)

Current settings: `Breakpoint` → BREAK, `CreateProcess` → BREAK, `ExitProcess` → BREAK, `Exception` → BREAK, `LoadModule` → GO, threads → GO.

### Thread Affinity

ALL dbgeng calls (`DebugCreate`, `CreateProcess`, `WaitForEvent`, `GetStackTrace`, etc.) MUST happen on the engine thread. `EngineLifecycleService` exposes only engine-thread methods (suffixed `OnEngine`) and thread-safe methods (`Break`, `Terminate`, `Detach`). Handlers set launch/attach parameters on `NativeDebuggerModel`, call `StartEngineThread`, and block on `model.EngineReady` until init completes. For engine queries, handlers use `model.QueueEngineQuery(() => nativeDebugger.GetStackTraceOnEngine(model, ...))` which marshals the call to the engine thread.

### Process Startup Sequence

1. **Pre-configDone**: First `WaitForEvent` return → `_configDone` is false → enter `ProcessCommandsUntilResume`. Process queued commands (setBreakpoints, then Continue from configurationDone).
2. **`_configDone` is set by the Continue command ON THE ENGINE THREAD** — not from the main thread. This avoids a race where configDone is true before the engine processes initial events.
3. **Post-configDone system stops**: auto-continue silently (no DAP event).
4. **User breakpoint / step / pause**: send DAP `stopped` event, enter command loop.

### Breakpoints

- Before engine exists: stored as pending in `DebugSessionModel.PendingBreakpoints`, applied in `ConfigurationDone`.
- At initial stop, `GetOffsetByLine` usually fails (module not loaded). Fallback: `bu` command (deferred breakpoint) — dbgeng resolves when module loads.
- Breakpoint IDs: dbgeng assigns 0-based IDs. Pending responses use IDs starting at 1000 to avoid collision.
- `NativeDebuggerModel.UserBreakpointIds` HashSet tracks which dbgeng breakpoint IDs are ours (vs system breakpoints).
- `ISourceFileService.IsNativeFile` check: rejects `.cs` files AND `.cpp` files in C++/CLI projects (scans vcxproj for `<CLRSupport>`).

### Managed Debugging (CLR Profiler + ICorDebug V4)

- **CLR detection**: `EventCallbacks.OnLoadModule` watches for `coreclr` module name. Sets `model.ClrLoaded` flag, captures `CoreClrPath` and `CoreClrBaseAddress`. ICorDebug V4 initialization happens on the next engine stop (can't init during `GO` state).
- **ICorDebug V4 integration**: `ICLRDebugging::OpenVirtualProcess` creates an `ICorDebugProcess` piggybacked on the existing dbgeng session via `DbgEngDataTarget` (implements `ICorDebugMutableDataTarget`). No second debugger, no conflicts. dbgeng owns the process; ICorDebug V4 reads/writes memory through the bridge. `RuntimeLibraryProvider` locates `mscordbi.dll` next to `coreclr.dll`.
- **CLR Profiler (`MixDbgProfiler.dll`)**: Native C++ DLL implementing `ICorProfilerCallback2`. CLR loads it at startup via `CORECLR_ENABLE_PROFILING` env vars. Uses two mechanisms:
  1. `JITCompilationFinished` sends `JIT:token:address:size:assembly[:IL-map]` for stack trace resolution and IL-to-native mapping
  2. `FunctionEnter` hooks (via x64 MASM stubs) fire on every call to breakpointed methods, enabling unlimited transient breakpoints
- **Unlimited managed breakpoints via FunctionEnter hooks**: `FunctionIDMapper` selectively enables hooks for watched methods. Two watch granularities:
  1. **Exact token watches** (`MIXDBG_WATCH_TOKENS`): C# methods — resolved from portable PDBs at pre-launch time. Only breakpointed methods are hooked.
  2. **Assembly-level watches** (`MIXDBG_WATCH_ASSEMBLIES`): C++/CLI assemblies — resolved from vcxproj at pre-launch time. ALL methods from the assembly are hooked (can't resolve specific tokens before module loads). Non-BP method ENTERs are ACKed immediately with REHOOK to keep hooks active.
  On each call: profiler disables hooks (`SetEventMask`) → sends ENTER notification → blocks on ACK → MixDbg sets transient hardware BP at exact line address (via IL-to-native mapping) → ACK → method runs without hooks → BP fires at correct line. On Continue: MixDbg removes BP, signals REHOOK event → profiler's watcher thread re-enables hooks for the next call.
- **Exact-line breakpoints via IL-to-native mapping**: Profiler sends `GetILToNativeMapping` data for watched methods in JIT: notifications. MixDbg maps the deferred BP's IL offset (from PDB) to the exact native address inside the method body. Hardware BPs fire at the precise source line, not just at method entry.
- **Managed stack traces**: Profiler's `JitMethodMap` (sorted by native address) maps any IP in JIT'd code to method token + assembly. `ResolveFrameFromProfilerData` binary-searches the map, reverse-maps native IP → IL offset via `JitMethodMappings`, then uses `PdbSourceMapperService` for method name + exact source file:line.
- **Source resolution**: C# uses portable PDBs read by `PdbSourceMapperService` via `System.Reflection.Metadata`. C++/CLI uses Windows PDBs read natively by dbgeng's `GetLineByOffset`.
- **Module tracking**: `ManagedDebuggerService.EnumerateModules` walks `ICorDebugProcess.AppDomains` → assemblies → modules. Called on init and on each dbgeng LoadModule event for managed DLLs. Pending breakpoints bind when their module becomes available.
- **CLR notification exceptions** (code `e0444143`): Returned as `GO_HANDLED` from `EventCallbacks.Exception`.
- **Pending breakpoints**: Managed breakpoints received before CLR loads are stored in `model.PendingManagedBreakpoints` and applied after `InitializeRuntime` succeeds. Breakpoints for modules not yet in ICorDebug are stored as `PendingManagedBreakpoint` and bound on module load.

### Diagnostic Logging

All sessions log to `~/mixdbg.log` — DAP requests/responses, dbgeng events, breakpoint resolution, stack frames. Logging goes through `ILoggingService` (implemented by `LoggingService`), with state in `LogStore`. Uses `[CallerFilePath]` to auto-tag log entries with the source file name. Writes to both in-memory `LogStore.Entries` and the log file.

## Milestones

### M1: DAP Transport — DONE
### M2: Native Debugging via dbgeng — DONE

Native C++ breakpoints, stack traces with source locations, stepping (over/into/out), thread enumeration, pause. Tested end-to-end with TestApp.

### M3: Native Variable Inspection — DONE

Scopes and variables inspection via `IDebugSymbolGroup2`. When the debugger stops, selecting a stack frame returns locals via `SetScope` + `GetScopeSymbolGroup`. Expandable variables (structs/pointers with `SubElements > 0`) allocate child `variablesReference` handles. Variable store invalidated on continue/step.

### M4: Managed Debugging — DONE

**Managed breakpoints (working):** Unlimited first-click breakpoints at exact source lines on both C# and C++/CLI code. Uses a hybrid CLR Profiler approach: `FunctionEnter` hooks (via x64 MASM stubs) detect each call to breakpointed methods, profiler temporarily disables hooks and blocks, MixDbg sets a transient hardware BP at the exact line address (via IL-to-native mapping from `GetILToNativeMapping`), method runs without hooks and hits the BP. On Continue, BP is removed and a REHOOK event re-enables hooks for the next call. C# uses exact token watches (`MIXDBG_WATCH_TOKENS`); C++/CLI uses assembly-level watches (`MIXDBG_WATCH_ASSEMBLIES`) because tokens can't be resolved before module load. All 5 integration tests pass (first-click, slow-user, double-click, exact-line, C++/CLI first-click).

**Managed stack traces (working):** Profiler's `JitMethodMap` maps native IPs to method tokens + assemblies. `ResolveFrameFromProfilerData` binary-searches the map, reverse-maps native IP → IL offset via `JitMethodMappings`, then uses `PdbSourceMapperService` for method name + exact source file:line. Completely replaces the broken ICorDebug piggybacked thread enumeration (`E_NOTIMPL`).

**CLR Profiler (`MixDbgProfiler.dll`):**
- Native C++ DLL implementing `ICorProfilerCallback2`
- CLR loads it via `CORECLR_ENABLE_PROFILING` env vars set before `CreateProcess`
- `JITCompilationFinished` resolves `FunctionID` → method token + native address + code size + assembly name
- Sends `JIT:TOKEN:ADDRESS:SIZE:ASSEMBLY[:IL-map]\n` text lines to MixDbg via named pipe (`MIXDBG_PIPE_NAME`)
- `FunctionEnter` hooks (x64 MASM stubs in `EnterLeaveStubs.asm`) fire on every call to watched methods
- `FunctionIDMapper` enables hooks for methods matching `MIXDBG_WATCH_TOKENS` (exact) or `MIXDBG_WATCH_ASSEMBLIES` (assembly-level for C++/CLI)
- On enter: disables hooks → sends `ENTER:TOKEN:ADDRESS:THREADID:ASSEMBLY\n` → blocks on ACK event (`MIXDBG_ACK_EVENT`) → MixDbg sets transient hardware BP → ACK → method runs → BP fires
- On continue: MixDbg signals REHOOK event (`MIXDBG_REHOOK_EVENT`) → watcher thread re-enables hooks
- Non-BP method ENTER (assembly-level watch): MixDbg ACKs immediately + signals REHOOK
- Profiler CLSID: `{D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}`
- Uses `ICorProfilerInfo` vtable calls by slot index (no corprof.h header dependency)

**Key findings (M4V2, 2026-04-04):** See `docs/M4V2-managed-breakpoints.md` for the full investigation of approaches that failed before the profiler approach succeeded.

**Launch args:** DAP `launch` request `args` field is threaded through to `CreateProcess` command line.

### M5: Managed Variable Inspection via ClrMD — TODO
### M6: Stepping Across Boundaries — TODO
### M7: Polish + Integration — TODO

See README.md for full milestone descriptions.

## Key Dependencies

- `dbgeng.dll` — ships with Windows (System32)
- NuGet `Microsoft.Extensions.DependencyInjection` — DI container
- NuGet `ClrDebug` — ICorDebug V4 COM interop wrappers for managed breakpoints and stack traces
- dbgeng.h reference: `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/dbgeng.h`
