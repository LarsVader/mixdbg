# mixdbg — Mixed-Mode Debug Adapter

> **Experimental.** This debugger is under active development and has only been tested with a small WPF test application (`test/TestApp/`). It may not work correctly with other projects. Use at your own risk.

A DAP debug adapter for simultaneous C# and native C++ debugging from Neovim. Wraps the Windows Debug Engine (`dbgeng.dll`) — the same engine that powers WinDbg.

## Why

When you have a project with multiple language layers (C# frontend → C++/CLI wrapper → native C++ library), no single open-source debugger handles the full stack:

- **netcoredbg** debugs C# but not native C++
- **codelldb** debugs C++ but not C#
- Both need exclusive access to the Windows debug port

Visual Studio solves this with a proprietary mixed-mode session. **mixdbg** is an open-source alternative, accessible from any editor that speaks DAP.

For a detailed explanation of the DAP protocol, dbgeng COM interop, and the full breakpoint lifecycle, see [docs/dap.md](docs/dap.md).

## Architecture

```
                    Main Thread                    Engine Thread
                    ──────────                    ─────────────
nvim-dap ──stdio──> IDapServer
                      │
                    IDapDispatcher
                      │ (routes requests)
                      v
                    Handlers ──────command queue──> IEngineLifecycleService
                      │                              │
                      │                            IDbgEngWrapper
                      │                            (encapsulates COM)
                      │                              │
                      │                            WaitForEvent loop
                      │                              │
                      │  <───── DAP events ────────  │
                      v                              v
                    IDapServer                     dbgeng.dll
                      │                              │
nvim-dap <──stdio────                            [target.exe]
```

All services are stateless singletons; mutable state lives in model objects. Three threads: main (DAP I/O), engine (dbgeng COM), profiler reader (JIT/ENTER/LEAVE notifications from the CLR profiler).

## Breakpoint Classification

| File type | Mechanism |
|---|---|
| `.cpp`/`.c` in native project | dbgeng software breakpoints (`int3`) |
| `.cpp`/`.h` in C++/CLI project | Hardware BPs via CLR Profiler (assembly-level watch) |
| `.cs` | Hardware BPs via CLR Profiler (exact token watch) |

Managed breakpoints are resolved via portable PDB (method token + IL offset) and set as hardware breakpoints (`ba e1`) at the exact JIT'd native address. The CLR Profiler (`MixDbgProfiler.dll`) hooks `FunctionEnter`/`FunctionLeave` to manage BP lifecycle — BPs live as long as their method has an activation on the stack.

## Current Status

**Working (M1-M6):**
- DAP transport (JSON-RPC over stdin/stdout)
- Process launch via dbgeng
- Native C++ breakpoints (including deferred)
- Managed C# and C++/CLI breakpoints at exact source lines
- Mid-session breakpoint addition on already-JIT'd methods
- Stack traces with source locations (native via dbgeng, managed via CLR Profiler + PDB)
- Native and managed variable inspection
- Cross-boundary stepping (step over/into/out across C#, C++/CLI, and native C++)
- Thread enumeration
- Diagnostic logging to `~/mixdbg.log`

**Not yet implemented (M7):**
- Attach to running process
- Conditional breakpoints
- Exception breakpoints (beyond CLR notification exceptions)
- Watch expressions
- Multi-process debugging

## Build and Run

```bash
dotnet build src/MixDbg.csproj -c Debug    # Debug adapter
cd profiler && make all                      # CLR Profiler DLL
```

MixDbg looks for `MixDbgProfiler.dll` next to its exe, or in `profiler/x64/Debug/` during development.

The adapter is configured in nvim-dap as an executable adapter. Select "Mixed C#/C++ (mixdbg)" from the debug config picker.

## Documentation

- [docs/architecture.md](docs/architecture.md) — Full architecture reference (services, isolation boundaries, threading, COM interop, managed debugging, stepping)
- [docs/dap.md](docs/dap.md) — DAP protocol, dbgeng COM interop, breakpoint lifecycle walkthrough
- [docs/stepping-architecture.md](docs/stepping-architecture.md) — Managed stepping and breakpoint interaction reference
- [docs/failed-approaches.md](docs/failed-approaches.md) — Post-mortem: why ICorDebug, DAC, SOS `!bpmd`, and other approaches failed
