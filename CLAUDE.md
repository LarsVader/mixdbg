# mixdbg — Mixed-Mode DAP Debug Adapter

A custom DAP (Debug Adapter Protocol) adapter that wraps Windows `dbgeng.dll` to enable simultaneous C# and native C++ debugging from Neovim's nvim-dap.

## Problem

Existing debug adapters (netcoredbg for C#, codelldb for C++) each need exclusive ownership of the Windows debug port. Only one can attach to a process at a time. Visual Studio solves this with a proprietary coordinated session. This adapter replicates that capability as an open DAP adapter.

## Architecture

Single-engine approach: dbgeng owns the debug port natively, SOS/ClrMD provide managed debugging on top.

```
nvim-dap  <-- DAP JSON-RPC over stdio -->  mixdbg.exe
                                               |
                                          DebugSession
                                           /        \
                                     NativeDbg    ManagedDbg (future)
                                     (dbgeng)     (SOS + ClrMD)
                                          \        /
                                         dbgeng.dll
                                             |
                                       [target process]
```

### Threading Model

- **Main thread**: DAP I/O — reads JSON-RPC from stdin, writes to stdout
- **Engine thread**: dbgeng `WaitForEvent` loop, processes queued commands when target is stopped

Communication: `BlockingCollection<Action>` command queue + `TaskCompletionSource<T>` for synchronous results.

### COM Interop

Raw COM via `[ComImport]` + `_VtblGap` stubs for vtable layout. Interfaces defined: IDebugClient, IDebugControl, IDebugSymbols, IDebugBreakpoint, IDebugSystemObjects, IDebugEventCallbacks.

## Project Structure

```
src/MixDbg/
  Program.cs                     # Entry point
  Dap/
    DapMessages.cs               # DAP protocol types (records)
    DapServer.cs                 # stdin/stdout JSON-RPC transport
    DapDispatcher.cs             # Command routing
  Engine/
    DebugSession.cs              # State machine, orchestrates DAP <-> engine
    NativeDebugger.cs            # dbgeng wrapper: launch, attach, breakpoints, step, stack
    DbgEng/
      Constants.cs               # dbgeng constants, flags, structs (DEBUG_STACK_FRAME, etc.)
      Interfaces.cs              # COM interface definitions with _VtblGap stubs
      EventCallbacks.cs          # IDebugEventCallbacks implementation
  Handlers/
    InitializeHandler.cs         # DAP initialize handshake
    LifecycleHandlers.cs         # launch, attach, configurationDone, disconnect, threads
    StubHandlers.cs              # Breakpoints, stepping, stack trace, scopes, variables
```

## Build

```bash
cd mixdbg
dotnet build src/MixDbg/MixDbg.csproj -c Debug
```

Output: `src/MixDbg/bin/Debug/net10.0/win-x64/MixDbg.exe`

## nvim-dap Integration

Adapter registered in `C:\Users\LarsVader\AppData\Local\nvim\lua\plugins\debug\nvim-dap.lua` as `mixdbg` (type: executable). A "Mixed C#/C++ (mixdbg)" configuration is added to both `dap.configurations.cpp` and `dap.configurations.cs`.

## Test Target

The CLRApp3 solution in the parent directory: C# WPF frontend → C++/CLI wrapper → native C++ library.

## Milestones

### M1: DAP Transport Layer — DONE
- DAP JSON-RPC over stdin/stdout (Content-Length framing)
- All protocol message types as C# records
- Command dispatcher with handler registration
- Initialize handshake verified with nvim-dap

### M2: Native Debugging via dbgeng — DONE
- dbgeng COM interop (6 interfaces, ~30 methods)
- Process launch (`CreateProcess`) and attach (`AttachProcess`)
- Engine thread with `WaitForEvent` loop + command queue
- Native breakpoints via `GetOffsetByLine` → `AddBreakpoint`
- Stack traces via `GetStackTrace` → `GetNameByOffset` + `GetLineByOffset`
- Stepping: over (`SetExecutionStatus`), into, out (`gu` command)
- Thread enumeration via `IDebugSystemObjects`
- Pause via `SetInterrupt`
- DAP events: stopped, terminated, output
- Integration tested: full launch → stopped → disconnect cycle

### M3: Native Variable Inspection — TODO
- `IDebugSymbolGroup2` for locals/arguments when stopped in native code
- `VariableStore` for DAP `variablesReference` handle allocation
- Struct/class member expansion

### M4: Managed Debugging via SOS + ClrMD — TODO
- Load SOS extension on CLR module load (detect `coreclr.dll`)
- Managed breakpoints via `!bpmd` (requires PDB → method name mapping)
- Mixed stack traces: merge native `GetStackTrace` + `!CLRStack` output
- Source line → method name index via `System.Reflection.Metadata`

### M5: Managed Variable Inspection via ClrMD — TODO
- Custom `IDataReader` routing memory reads through dbgeng's `IDebugDataSpaces`
- `ClrRuntime.Threads` for managed thread enumeration
- `ClrHeap.GetObjectType` for managed object inspection

### M6: Stepping Across Boundaries — TODO
- Step from C# through C++/CLI into native C++ and back
- Detect managed/native transitions by instruction pointer range
- Skip IJW host thunks automatically

### M7: Polish + Integration — TODO
- REPL evaluation (dbgeng `Execute` for native, SOS for managed)
- Thread listing with managed/native annotations
- Error handling and graceful degradation
- Logging via DAP output events

## Key Dependencies

- `dbgeng.dll` — ships with Windows (System32)
- `dotnet-sos` — install via `dotnet tool install -g dotnet-sos && dotnet-sos install`
- NuGet (future): `Microsoft.Diagnostics.Runtime` (ClrMD)

## Risks

- **SOS text parsing**: `!CLRStack`, `!bpmd` output varies by version. Mitigate with ClrMD as primary path.
- **Managed breakpoints**: `!bpmd` works by method name, not line. Need PDB parsing for mapping.
- **ClrMD + dbgeng coexistence**: Need custom `IDataReader` through `IDebugDataSpaces`.
- **C++/CLI IJW symbols**: Mixed assemblies may need `!ip2md` for resolution.
