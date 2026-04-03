# mixdbg — Mixed-Mode DAP Debug Adapter

A custom DAP adapter wrapping Windows `dbgeng.dll` for simultaneous C# and native C++ debugging from Neovim's nvim-dap. See `README.md` for a detailed explanation of DAP, dbgeng, and the architecture.

## Build

```bash
dotnet build src/MixDbg.csproj -c Debug
```

Output: `src/bin/Debug/net10.0/win-x64/MixDbg.exe`

## Test Target

The `test/TestApp/` directory contains a mixed-mode WPF app (C# frontend → C++/CLI wrapper → native C++ library) used as the integration test target. Build with `make all` from `test/TestApp/`.

## nvim-dap Integration

Adapter registered in `C:\Users\LarsVader\AppData\Local\nvim\lua\plugins\debug\nvim-dap.lua` as `mixdbg` (type: executable, hardcoded path to the built exe). A "Mixed C#/C++ (mixdbg)" config is in both `dap.configurations.cpp` and `dap.configurations.cs`.

## Project Structure

```
src/
  MixDbg.csproj                  # Production project
  Program.cs                     # Entry point — DI composition root
  ServiceCollectionExtensions.cs # AddMixDbgCore() — registers all services + models
  DapMessages/                   # DAP protocol types (namespace MixDbg.Dap), one file per type
    Protocol/                    # ProtocolMessage, RequestMessage, ResponseMessage, EventMessage, Source, DisconnectException
    Initialize/                  # InitializeRequestArguments, Capabilities
    Lifecycle/                   # LaunchRequestArguments, AttachRequestArguments, DisconnectArguments
    Breakpoints/                 # SetBreakpointsArguments, SourceBreakpoint, Breakpoint, SetBreakpointsResponseBody
    Execution/                   # ContinueArguments, ContinueResponseBody, StepArguments
    Inspection/                  # StackTraceArguments/ResponseBody, StackFrame, Scopes*, Variables*, Variable
    Threads/                     # ThreadsResponseBody, DapThread
    Evaluate/                    # EvaluateArguments, EvaluateResponseBody
    Events/                      # StoppedEventBody, OutputEventBody, BreakpointEventBody, Terminated/InitializedEventBody
  Engine/
    DbgEng/
      Constants/                 # One file per type: DbgEngNative, DebugStatus, DebugAttach, CreateProcessFlags, DebugBreakpoint*, DebugBreakAccess, DebugEvent, DebugEnd, DebugExecute, DebugOutCtl, SymOpt, DebugScopeGroup, DEBUG_STACK_FRAME, DEBUG_SYMBOL_PARAMETERS, DebugSymbolFlags
      Interfaces/                # One file per COM interface: IDebugClient, IDebugControl, IDebugSymbols, IDebugBreakpoint, IDebugSymbolGroup2, IDebugSystemObjects, IDebugEventCallbacks, IDebugOutputCallbacks
      EventCallbacks.cs          # IDebugEventCallbacks implementation — return values control WaitForEvent behavior
      OutputCapture.cs           # IDebugOutputCallbacks implementation — captures SOS command text output
    Sos/
      SosOutputParser.cs         # Parses !bpmd text output to extract breakpoint IDs
      PdbSourceMapper.cs         # Reads portable PDBs to map (method token, IL offset) → (source file, line)
  Models/
    DapServerModel.cs            # DAP transport state: streams, write lock, sequence counter
    DapDispatcherModel.cs        # Dispatcher state: handler registrations
    DebugSessionModel.cs         # Session state: engine ref, pending breakpoints, SessionState enum
    NativeDebuggerModel.cs       # Engine state: COM interfaces, threads, flags, breakpoint tracking, variable store, ClrMD runtime, deferred managed bps
    DeferredManagedBreakpoint.cs # Record: managed bp waiting for JIT compilation (file, line, assembly, method, bpId)
    VariableStore.cs             # Maps variablesReference handles to VariableContainer (symbol group + index range), invalidated per stop
    LogEntry.cs                  # Immutable log record (timestamp, level, sender, message)
    LogStore.cs                  # Mutable log state: entries list, lock, file path
  Services/
    Interfaces/
      IDapServer.cs              # Stateless DAP transport — all methods take DapServerModel
      IDapDispatcher.cs          # Stateless request dispatcher — all methods take DapDispatcherModel
      IDebugSession.cs           # Stateless session orchestrator — all methods take DebugSessionModel
      INativeDebugger.cs         # Stateless dbgeng wrapper — all methods take NativeDebuggerModel
      ILoggingService.cs         # LogInfo/LogWarning/LogError with [CallerFilePath] — all take LogStore
      ISourceFileService.cs      # IsNativeFile(string path), IsManagedFile(string path)
      IManagedDebugger.cs        # Stateless managed debugging — ClrMD + SOS, all methods take NativeDebuggerModel
    DapServerService.cs          # IDapServer: Content-Length framed JSON-RPC transport
    DapDispatcherService.cs      # IDapDispatcher: command routing, request/response lifecycle
    DebugSessionService.cs       # IDebugSession: state machine, delegates to INativeDebugger
    NativeDebuggerService.cs     # INativeDebugger: dbgeng COM wrapper, engine thread, breakpoints
    ManagedDebuggerService.cs    # IManagedDebugger: ClrMD runtime inspection + SOS !bpmd for managed breakpoints
    LoggingService.cs            # ILoggingService: file + in-memory logger, [CallerFilePath] sender
    SourceFileService.cs         # ISourceFileService: native vs managed/CLI file detection
  Handlers/
    InitializeHandler.cs         # DAP initialize handshake
    LifecycleHandlers.cs         # launch, attach, configurationDone, disconnect, terminate, threads
    StubHandlers.cs              # setBreakpoints, continue, next, stepIn, stepOut, stackTrace, scopes, variables, evaluate
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

**DI container** (`Microsoft.Extensions.DependencyInjection`): `Program.cs` builds a `ServiceProvider` via `AddMixDbgCore()`. Follows model+service pattern from zonr: all services are stateless singletons; all mutable state lives in model objects created by services via `CreateModel()` and registered as singletons. Service methods take their model as the first parameter. `NativeDebuggerModel` is created per-session (one per Launch/Attach) by `INativeDebugger.CreateModel()`, stored in `DebugSessionModel.Engine`.

Two threads, one command queue:

- **Main thread**: reads DAP requests from stdin, dispatches to handlers. Handlers that need engine data queue a command + `TaskCompletionSource` and block.
- **Engine thread**: all dbgeng COM calls happen here (thread affinity required). Runs `WaitForEvent` loop. When target stops, processes queued commands, sends DAP events via `IDapServer`.

## Critical Implementation Details

### dbgeng COM Interop

- Uses `[ComImport]` + `_VtblGap` for vtable layout. All vtable positions verified against `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/dbgeng.h`.
- **All string output buffers use `IntPtr` + `Marshal.PtrToStringAnsi`** — NOT `StringBuilder` (defaults to UTF-16 on .NET, dbgeng writes ANSI).
- **`GetStackTrace` frames array uses `IntPtr` + `Marshal.PtrToStructure`** — COM array marshaling with `[PreserveSig]` doesn't copy data back.
- `DEBUG_STACK_FRAME` is 128 bytes: 15 × `ulong` + `int Virtual` + `uint FrameNumber`.

### DEBUG_STATUS Constants (dbgeng.h)

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

ALL dbgeng calls (`DebugCreate`, `CreateProcess`, `WaitForEvent`, `GetStackTrace`, etc.) MUST happen on the engine thread. The `Launch`/`Attach` service methods save parameters in `NativeDebuggerModel` and signal the engine thread, which does the actual COM work. Main thread blocks on `model.EngineReady` until init completes.

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

### Managed Debugging (ClrMD)

- **CLR detection**: `EventCallbacks.OnLoadModule` watches for `coreclr` module name. Sets `model.ClrLoaded` flag. ClrMD initialization happens on the next engine stop (can't init during `GO` state).
- **ClrMD integration**: `DataTarget.CreateFromDbgEng(pDebugClient)` creates a ClrMD data target piggybacking on the existing dbgeng COM session. No second process attach. `ClrRuntime` used for managed stack traces and method resolution.
- **Managed stack traces**: `ClrThread.EnumerateStackTrace()` gives managed frames. Merged with native frames by overlaying onto dbgeng stack positions that have no source resolution (JIT-compiled code shows as raw addresses or `coreclr!` prefixes).
- **Source resolution**: C# uses portable PDBs read by `PdbSourceMapper` via `System.Reflection.Metadata`. C++/CLI uses Windows PDBs read natively by dbgeng's `GetLineByOffset`.
- **Method resolution**: `PdbSourceMapper.FindMethodAtLine` resolves file:line → assembly + method name by reading portable PDBs. Falls back to searching PDBs on disk near the source file's `.csproj` when ClrMD modules aren't loaded yet.
- **Managed breakpoints (hardware)**: Software breakpoints (`int3`) fail on JIT code pages (`0x800703E6`). Instead, uses **hardware execution breakpoints** (`ba e1`, `AddBreakpoint(Data)` + `SetDataParameters(1, Execute)`) which use CPU debug registers — no code modification needed. Limited to 4 concurrent managed breakpoints (x64 DR0-DR3 registers).
- **Deferred managed breakpoints**: Methods not yet JIT-compiled have `NativeCode == 0`. Stored as `DeferredManagedBreakpoint` and resolved when: (a) CLR notification exceptions fire during JIT, or (b) `SetInterrupt(ACTIVE)` forces an engine stop ~2 seconds after init. `FlushCachedData()` + `FindNativeCodeAddress` checks each deferred bp.
- **CLR notification exceptions** (code `e0444143`): Returned as `GO_HANDLED` from `EventCallbacks.Exception`. Also triggers deferred breakpoint resolution via `OnClrNotification` event.
- **Pending breakpoints**: Managed breakpoints received before CLR loads are stored in `model.PendingManagedBreakpoints` and applied after `InitializeRuntime` succeeds.

### Diagnostic Logging

All sessions log to `~/mixdbg.log` — DAP requests/responses, dbgeng events, breakpoint resolution, stack frames. Logging goes through `ILoggingService` (implemented by `LoggingService`), with state in `LogStore`. Uses `[CallerFilePath]` to auto-tag log entries with the source file name. Writes to both in-memory `LogStore.Entries` and the log file.

## Milestones

### M1: DAP Transport — DONE
### M2: Native Debugging via dbgeng — DONE

Native C++ breakpoints, stack traces with source locations, stepping (over/into/out), thread enumeration, pause. Tested end-to-end with TestApp.

### M3: Native Variable Inspection — DONE

Scopes and variables inspection via `IDebugSymbolGroup2`. When the debugger stops, selecting a stack frame returns locals via `SetScope` + `GetScopeSymbolGroup`. Expandable variables (structs/pointers with `SubElements > 0`) allocate child `variablesReference` handles. Variable store invalidated on continue/step.

### M4: Managed Debugging via ClrMD — PARTIAL

Managed stack traces and managed breakpoints work via ClrMD + hardware execution breakpoints. Breakpoints require two clicks in manual sessions (first JITs, second hits). Multi-breakpoint reliability still being debugged.

**Stack traces:** ClrMD (`DataTarget.CreateFromDbgEng`) piggybacking on the existing dbgeng session provides managed stack frames with method names. Source line resolution uses portable PDB reading (`System.Reflection.Metadata`) for C# and dbgeng's native `GetLineByOffset` for C++/CLI. CLR detection via `LoadModule` callback. Stack frame merging overlays managed info onto native frames.

**Managed breakpoints:** Software breakpoints (`int3`) fail on .NET JIT code pages (`0x800703E6`). Bypassed using **hardware execution breakpoints** (`ba e1`) which use CPU debug registers instead of modifying code. ClrMD resolves method names via PDB, then `ClrMethod.NativeCode` provides the JIT-compiled address. Methods not yet JIT'd are deferred and resolved via `SetInterrupt(ACTIVE)` or on the next engine stop. Limited to 4 concurrent managed breakpoints (x64 hardware limit).

**Launch args:** DAP `launch` request `args` field is threaded through to `CreateProcess` command line.

### M5: Managed Variable Inspection via ClrMD — TODO
### M6: Stepping Across Boundaries — TODO
### M7: Polish + Integration — TODO

See README.md for full milestone descriptions.

## Key Dependencies

- `dbgeng.dll` — ships with Windows (System32)
- NuGet `Microsoft.Extensions.DependencyInjection` — DI container
- `dotnet-sos` — `dotnet tool install -g dotnet-sos && dotnet-sos install` (fallback SOS load path)
- NuGet `Microsoft.Diagnostics.Runtime` (ClrMD) — managed stack traces and method resolution
- dbgeng.h reference: `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/dbgeng.h`
