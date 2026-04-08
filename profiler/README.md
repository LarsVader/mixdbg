# MixDbgProfiler — CLR Profiler DLL

A native C++ DLL implementing `ICorProfilerCallback2` that runs inside the debuggee process. It sends JIT compilation and function-enter notifications to MixDbg (the debug adapter) via a named pipe, enabling managed breakpoints and stack trace resolution.

## Why a Profiler?

Standard ICorDebug V4 "piggybacked" debugging can't set managed breakpoints before a method is JIT-compiled, and its thread enumeration returns `E_NOTIMPL` in piggybacked mode. The profiler solves both problems:

- **JIT notifications** tell MixDbg the native address of every compiled method, enabling deferred breakpoint resolution.
- **FunctionEnter hooks** let MixDbg set transient hardware breakpoints at exact source lines inside managed methods — no ICorDebug breakpoint API needed.

## How It Works

### JIT Notifications

1. CLR loads the profiler at process startup via `CORECLR_ENABLE_PROFILING` env vars.
2. The profiler connects to MixDbg's named pipe (`MIXDBG_PIPE_NAME`).
3. On every `JITCompilationFinished`, the profiler sends a line: `JIT:<token>:<address>:<size>:<assembly>[:<IL-map>]`.
4. MixDbg matches the token against deferred breakpoints and resolves them.

### Managed Breakpoints via FunctionEnter Hooks

For methods with breakpoints, MixDbg pre-configures "watch" lists via env vars before launching the target:

- `MIXDBG_WATCH_TOKENS` — exact `Assembly:Token` pairs (C# methods, resolved from PDBs).
- `MIXDBG_WATCH_ASSEMBLIES` — full assemblies (C++/CLI, where tokens aren't known ahead of time).

The breakpoint flow:

1. `FunctionIDMapper` enables enter/leave hooks for watched methods.
2. On each call, `FunctionEnterNaked` (x64 ASM stub) saves registers → calls `FunctionEnterImpl` (C++).
3. The profiler disables hooks (`SetEventMask`), sends `ENTER:<token>:<address>:<threadid>:<assembly>` to MixDbg, and blocks on `MIXDBG_ACK_EVENT`.
4. MixDbg sets a transient hardware breakpoint at the exact source line (using IL-to-native mapping), then signals ACK.
5. The method executes without hooks and hits the hardware BP at the correct line.
6. On Continue, MixDbg removes the BP and signals `MIXDBG_REHOOK_EVENT` → the rehook watcher thread re-enables hooks for the next call.

### IL-to-Native Mapping

For watched methods, the `JIT:` notification includes the full `GetILToNativeMapping` data (appended as `:IL0=N0,IL1=N1,...`). MixDbg maps the deferred breakpoint's IL offset (from PDB) to the exact native address inside the JIT'd method body. This means hardware BPs fire at the precise source line, not just at method entry.

## Build

Requires Visual Studio Build Tools with the v145 (or compatible) C++ toolset and x64 MASM.

```bash
make all        # Debug build
make clean      # Clean build artifacts
```

Output: `x64/Debug/MixDbgProfiler.dll`

MixDbg looks for the DLL next to its exe, or in `profiler/x64/Debug/` during development.

## File Structure

| File | Description |
|---|---|
| `CoreClrTypes.h` | Type aliases, `COR_PRF_MONITOR` flags, GUIDs, `ILNativeMap` struct |
| `ProfilerInfo.h` | `ProfilerInfo` — vtable wrapper for `ICorProfilerInfo` (calls by slot index, no corprof.h dependency) |
| `MixDbgProfiler.h` | `MixDbgProfiler` — class declaration with all 75 virtual methods in exact vtable order |
| `MixDbgProfiler.cpp` | `MixDbgProfiler` — implementation: Initialize, Shutdown, JITCompilationFinished, OnFunctionEnter |
| `FunctionCallbacks.cpp` | Free-standing `extern "C"` callbacks: `FunctionIDMapper`, `FunctionEnterImpl`, `FunctionLeaveImpl` |
| `ClassFactory.cpp` | `ClassFactory` — COM class factory for `MixDbgProfiler` |
| `DllExports.cpp` | `DllGetClassObject` + `DllCanUnloadNow` — DLL entry points |
| `EnterLeaveStubs.asm` | x64 MASM naked stubs that save/restore all volatile registers around enter/leave/tailcall hooks |
| `MixDbgProfiler.def` | Module definition: exports `DllGetClassObject` and `DllCanUnloadNow` |
| `Makefile` | MSBuild wrapper for command-line builds |

## Named Pipe Protocol

Text lines over a named pipe, one per notification:

| Message | Format | Direction |
|---|---|---|
| JIT notification (with hooks) | `JIT:<token_hex>:<addr_hex>:<size_hex>:<assembly>[:<IL-map>]\n` | Profiler → MixDbg |
| JIT notification (no hooks) | `<token_hex>:<addr_hex>:<size_hex>:<assembly>\n` | Profiler → MixDbg |
| Function enter | `ENTER:<token_hex>:<body_addr_hex>:<thread_id_hex>:<assembly>\n` | Profiler → MixDbg |

Synchronization events (Windows named events):

| Event | Purpose |
|---|---|
| `MIXDBG_ACK_EVENT` | MixDbg → Profiler: "hardware BP is set, you may unblock" |
| `MIXDBG_REHOOK_EVENT` | MixDbg → Profiler: "user continued, re-enable enter/leave hooks" |

## Key Constraints

- **Vtable layout**: `MixDbgProfiler` declares all `ICorProfilerCallback2` virtual methods in exact `corprof.idl` order. MSVC single-inheritance guarantees they appear in declaration order in the vtable. Do not reorder, add, or remove virtual methods.
- **No corprof.h**: `ProfilerInfo` calls `ICorProfilerInfo` methods by vtable slot index. Slot numbers are from the COM interface definition and are stable across .NET versions.
- **FunctionIDMapper caching**: The CLR caches `FunctionIDMapper` results — watches cannot be added dynamically after startup. All watch lists must be set via env vars before `CreateProcess`.
- **CLSID**: `{D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}` — must match the CLSID MixDbg passes in the `CORECLR_PROFILER` env var.
