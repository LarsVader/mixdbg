# mixdbg — Mixed-Mode DAP Debug Adapter

A custom DAP adapter wrapping Windows `dbgeng.dll` for simultaneous C# and native C++ debugging from Neovim's nvim-dap. See `README.md` for a detailed explanation of DAP, dbgeng, and the architecture.

## Build

```bash
cd mixdbg
dotnet build src/MixDbg/MixDbg.csproj -c Debug
```

Output: `src/MixDbg/bin/Debug/net10.0/win-x64/MixDbg.exe`

## Test Target

The CLRApp3 solution in the parent directory (`..`): C# WPF frontend → C++/CLI wrapper → native C++ library. Build with `make build` from `..`.

## nvim-dap Integration

Adapter registered in `C:\Users\LarsVader\AppData\Local\nvim\lua\plugins\debug\nvim-dap.lua` as `mixdbg` (type: executable, hardcoded path to the built exe). A "Mixed C#/C++ (mixdbg)" config is in both `dap.configurations.cpp` and `dap.configurations.cs`.

## Project Structure

```
src/MixDbg/
  Program.cs                     # Entry point — DI composition root
  ServiceCollectionExtensions.cs # AddMixDbgCore() — registers all services + models
  Dap/
    DapMessages.cs               # All DAP protocol types as C# records, DisconnectException
  Engine/
    DbgEng/
      Constants.cs               # DEBUG_STATUS_*, breakpoint flags, DebugCreate P/Invoke, DEBUG_STACK_FRAME struct
      Interfaces.cs              # COM interfaces: IDebugClient, IDebugControl, IDebugSymbols, IDebugBreakpoint, IDebugSystemObjects, IDebugEventCallbacks
      EventCallbacks.cs          # IDebugEventCallbacks implementation — return values control WaitForEvent behavior
  Models/
    DapServerModel.cs            # DAP transport state: streams, write lock, sequence counter
    DapDispatcherModel.cs        # Dispatcher state: handler registrations
    DebugSessionModel.cs         # Session state: engine ref, pending breakpoints, SessionState enum
    NativeDebuggerModel.cs       # Engine state: COM interfaces, threads, flags, breakpoint tracking
    LogEntry.cs                  # Immutable log record (timestamp, level, sender, message)
    LogStore.cs                  # Mutable log state: entries list, lock, file path
  Services/
    Interfaces/
      IDapServer.cs              # Stateless DAP transport — all methods take DapServerModel
      IDapDispatcher.cs          # Stateless request dispatcher — all methods take DapDispatcherModel
      IDebugSession.cs           # Stateless session orchestrator — all methods take DebugSessionModel
      INativeDebugger.cs         # Stateless dbgeng wrapper — all methods take NativeDebuggerModel
      ILoggingService.cs         # LogInfo/LogWarning/LogError with [CallerFilePath] — all take LogStore
      ISourceFileService.cs      # IsNativeFile(string path)
    DapServerService.cs          # IDapServer: Content-Length framed JSON-RPC transport
    DapDispatcherService.cs      # IDapDispatcher: command routing, request/response lifecycle
    DebugSessionService.cs       # IDebugSession: state machine, delegates to INativeDebugger
    NativeDebuggerService.cs     # INativeDebugger: dbgeng COM wrapper, engine thread, breakpoints
    LoggingService.cs            # ILoggingService: file + in-memory logger, [CallerFilePath] sender
    SourceFileService.cs         # ISourceFileService: native vs managed/CLI file detection
  Handlers/
    InitializeHandler.cs         # DAP initialize handshake
    LifecycleHandlers.cs         # launch, attach, configurationDone, disconnect, terminate, threads
    StubHandlers.cs              # setBreakpoints, continue, next, stepIn, stepOut, stackTrace, scopes, variables, evaluate
```

## Architecture

**DI container** (`Microsoft.Extensions.DependencyInjection`): `Program.cs` builds a `ServiceProvider` via `AddMixDbgCore()`. Follows model+service pattern from zonr: all services are stateless singletons; all mutable state lives in model objects created by services via `CreateModel()` and registered as singletons. Service methods take their model as the first parameter. `NativeDebuggerModel` is created per-session (one per Launch/Attach) by `INativeDebugger.CreateModel()`, stored in `DebugSessionModel.Engine`.

Two threads, one command queue:

- **Main thread**: reads DAP requests from stdin, dispatches to handlers. Handlers that need engine data queue a command + `TaskCompletionSource` and block.
- **Engine thread**: all dbgeng COM calls happen here (thread affinity required). Runs `WaitForEvent` loop. When target stops, processes queued commands, sends DAP events via `DapServer`.

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

ALL dbgeng calls (`DebugCreate`, `CreateProcess`, `WaitForEvent`, `GetStackTrace`, etc.) MUST happen on the engine thread. The `Launch`/`Attach` methods save parameters and signal the engine thread, which does the actual COM work. Main thread blocks on `_engineReady` until init completes.

### Process Startup Sequence

1. **Pre-configDone**: First `WaitForEvent` return → `_configDone` is false → enter `ProcessCommandsUntilResume`. Process queued commands (setBreakpoints, then Continue from configurationDone).
2. **`_configDone` is set by the Continue command ON THE ENGINE THREAD** — not from the main thread. This avoids a race where configDone is true before the engine processes initial events.
3. **Post-configDone system stops**: auto-continue silently (no DAP event).
4. **User breakpoint / step / pause**: send DAP `stopped` event, enter command loop.

### Breakpoints

- Before engine exists: stored as pending in `DebugSession._pendingBreakpoints`, applied in `ConfigurationDone`.
- At initial stop, `GetOffsetByLine` usually fails (module not loaded). Fallback: `bu` command (deferred breakpoint) — dbgeng resolves when module loads.
- Breakpoint IDs: dbgeng assigns 0-based IDs. Pending responses use IDs starting at 1000 to avoid collision.
- `_userBreakpointIds` HashSet tracks which dbgeng breakpoint IDs are ours (vs system breakpoints).
- `IsNativeFile` check: rejects `.cs` files AND `.cpp` files in C++/CLI projects (scans vcxproj for `<CLRSupport>`).

### Diagnostic Logging

All sessions log to `~/mixdbg.log` — DAP requests/responses, dbgeng events, breakpoint resolution, stack frames. Logging goes through `ILoggingService` (implemented by `LoggingService`), with state in `LogStore`. Uses `[CallerFilePath]` to auto-tag log entries with the source file name. Writes to both in-memory `LogStore.Entries` and the log file.

## Milestones

### M1: DAP Transport — DONE
### M2: Native Debugging via dbgeng — DONE

Native C++ breakpoints, stack traces with source locations, stepping (over/into/out), thread enumeration, pause. Tested end-to-end with CLRApp3.

### M3: Native Variable Inspection — TODO

Need to implement:
- Add `IDebugSymbolGroup2` COM interface (vtable from dbgeng.h — methods: `GetNumberSymbols`, `GetSymbolName`, `GetSymbolTypeName`, `GetSymbolValueText`, `ExpandSymbol`, `GetSymbolEntryInformation`)
- `ScopesHandler`: use `IDebugSymbols::SetScope` to select stack frame, `GetScopeSymbolGroup(DEBUG_SCOPE_GROUP_LOCALS)` to get locals
- `VariablesHandler`: iterate `IDebugSymbolGroup2` for names/types/values
- `VariableStore`: allocate integer `variablesReference` handles for expandable variables (structs/pointers), cache per stop (invalidate on continue)
- Update `StubHandlers.cs` to wire `scopes` and `variables` to real implementations

### M4: Managed Debugging via SOS + ClrMD — TODO
### M5: Managed Variable Inspection via ClrMD — TODO
### M6: Stepping Across Boundaries — TODO
### M7: Polish + Integration — TODO

See README.md for full milestone descriptions.

## Key Dependencies

- `dbgeng.dll` — ships with Windows (System32)
- NuGet `Microsoft.Extensions.DependencyInjection` — DI container
- `dotnet-sos` — `dotnet tool install -g dotnet-sos && dotnet-sos install` (needed for M4)
- NuGet `Microsoft.Diagnostics.Runtime` (ClrMD) — needed for M5
- dbgeng.h reference: `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/dbgeng.h`
