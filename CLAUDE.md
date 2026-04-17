# mixdbg — Mixed-Mode DAP Debug Adapter

A custom DAP adapter wrapping Windows `dbgeng.dll` for simultaneous C# and native C++ debugging from Neovim's nvim-dap. See `docs/architecture.md` for the full architecture reference, `docs/dap.md` for DAP/dbgeng background.

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

## Testing

**Integration tests are the most important tests** — always run them after any code change. They catch real regressions that unit tests miss.

```bash
dotnet test test/IntegrationTests/MixDbg.IntegrationTests.csproj
dotnet test test/UnitTests/MixDbg.UnitTests.csproj
```

Never report a task as done without running both.

## Documentation

After code changes, check whether `docs/` files need updating — especially `docs/architecture.md` (service responsibilities, data model, threading, managed debugging), `docs/stepping-architecture.md` (stepping/BP interactions), and `docs/failed-approaches.md` (if a new dead-end was discovered). Keep `CLAUDE.md` in sync with any changes to build/test commands, project structure, or critical implementation gotchas.

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
        PdbSourceMapperService.cs # Internal: reads portable PDBs, implements IPdbSourceMapper (singleton) — GetMethodSequencePoints, GetCallTargetAtOffset, FindMethodToken
        SosOutputParser.cs       # Internal: parses SOS command output (unused)
    Models/
      DbgEngWrapperModel.cs      # Public: dbgeng COM wrapper state (COM refs internal), engine events
      CorDebugWrapperModel.cs    # Public: ICorDebug V4 wrapper state (ClrDebug refs internal)
      VariableStore.cs           # Internal: variablesReference → symbol group mapping
      ManagedVariableStore.cs    # Internal: managed variablesReference → ICorDebug value mapping
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
        IDbgEngWrapper.cs        # Public: stateless dbgeng COM wrapper interface — includes GetOffsetByName
        ICorDebugWrapper.cs      # Public: stateless ICorDebug V4 wrapper interface
        ILoggingService.cs       # Public: logging interface
        IPdbSourceMapper.cs      # Public: PDB source resolution interface — GetMethodSequencePoints, GetCallTargetAtOffset, FindMethodToken
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
    NativeDebuggerModel.cs       # Engine state: DbgEngWrapperModel + CorDebugWrapperModel refs, threads, flags, breakpoint tracking, ManagedStepState + ActiveManagedStep + ManagedStepIntoCompleted
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
      IManagedDebugger.cs        # Stateless managed debugging — runtime lifecycle, frame resolution
      IManagedBreakpointService.cs # Stateless managed breakpoint setting/removal — PDB resolution, hardware BPs
      IManagedBreakpointResolver.cs # Stateless deferred managed BP resolution — JIT notifications, DAC polling, ENTER hooks
      IProfilerPipeService.cs    # Profiler pipe setup and reader thread
    DapServerService.cs          # IDapServer: Content-Length framed JSON-RPC transport
    DapDispatcherService.cs      # IDapDispatcher: command routing via DI-resolved handler services
    EngineLifecycleService.cs     # IEngineLifecycleService: engine thread, event loop, process lifecycle
    BreakpointService.cs         # IBreakpointService: native/managed/deferred breakpoint management, hit callbacks
    EngineQueryService.cs        # IEngineQueryService: stack trace, scopes, variables, threads, execution control
    ManagedDebuggerService.cs    # IManagedDebugger: runtime lifecycle, stack frame resolution
    ManagedBreakpointService.cs  # IManagedBreakpointService: managed BP setting/removal, PDB resolution, hardware BPs
    ManagedBreakpointResolverService.cs # IManagedBreakpointResolver: deferred BP resolution, JIT notifications, ENTER hooks
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
  IntegrationTests/                  # End-to-end tests against TestApp (xunit.runner.json disables parallel execution)
    SteppingIntegrationTest.cs       # M6: cross-boundary stepping integration tests
  TestApp/                           # Mixed-mode WPF integration test target
    TestApp.sln                      # Solution: NativeLib + CliWrapper + WpfApp
    Makefile                         # Build via MSBuild (make all)
    NativeLib/                       # Native C++ library (Calculator::Add/Multiply)
    CliWrapper/                      # C++/CLI wrapper (ManagedCalculator)
  WpfApp/                            # C# WPF frontend — --auto-test / --auto-test-slow for CI
```

## Architecture

See `docs/architecture.md` for the full architecture reference (service responsibilities, isolation boundaries, threading model, COM interop details, managed debugging, variable inspection, stepping).

Key points for making changes:
- Services are **stateless singletons**; all mutable state lives in model objects.
- ALL dbgeng COM calls MUST happen on the **engine thread** (thread affinity). Methods suffixed `OnEngine`.
- dbgeng string output uses `IntPtr` + `Marshal.PtrToStringAnsi` — NOT `StringBuilder`.
- `ConfigDone` is set ON THE ENGINE THREAD to avoid a race with initial events.
- CLR notification exceptions (code `e0444143`): returned as `GO_HANDLED` from `EventCallbacks.Exception`.
- Managed BPs use method-lifetime scoping (ENTER/LEAVE activation counting). See `docs/architecture.md`.
- Mid-session BPs on already-JIT'd methods without active hooks install HW BPs immediately.
- Stepping architecture: see `docs/stepping-architecture.md`.
- ICorDebug V4 piggybacked on dbgeng is inspection-only — cannot set BPs, enumerate threads, or access JIT state. See `docs/failed-approaches.md` before attempting ICorDebug-based solutions.

## Milestones

### M1: DAP Transport — DONE
### M2: Native Debugging via dbgeng — DONE
### M3: Native Variable Inspection — DONE
### M4: Managed Debugging — DONE

C# and C++/CLI breakpoints at exact source lines via CLR Profiler + hardware BPs. Method-lifetime BP lifecycle (M4V3). Managed stack traces via JitMethodMap + PDB. See `docs/architecture.md`.

### M5: Managed Variable Inspection via SOS/dbgeng — DONE

C# locals/args via SOS `!clrstack -l` output parsing. Managed variable refs start at 100,000. See `docs/architecture.md`.

### M6: Stepping Across Boundaries — DONE

Cross-boundary step over/into/out across C#, C++/CLI, and native C++. See `docs/stepping-architecture.md`.

### M7: Polish + Integration — TODO
