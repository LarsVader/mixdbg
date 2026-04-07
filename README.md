# mixdbg — Mixed-Mode Debug Adapter

A debug adapter that lets you debug native C++ and (in the future) C# code in the same session from Neovim. It wraps the Windows Debug Engine (`dbgeng.dll`) — the same engine that powers WinDbg.

## Background: What Problem Does This Solve?

When you have a project with multiple language layers (like a C# frontend calling native C++ through a C++/CLI wrapper), debugging is painful:

- **netcoredbg** can debug C# but knows nothing about native C++
- **codelldb** can debug C++ but knows nothing about C#
- Both need exclusive access to the Windows "debug port" — a per-process OS resource that only one debugger can hold at a time

Visual Studio solves this with a proprietary mixed-mode debug session. **mixdbg** is an open-source alternative that aims to do the same, accessible from any editor that speaks DAP.

## Background: How Debuggers Talk to Editors

Debuggers and editors communicate via the **Debug Adapter Protocol (DAP)** — a JSON-RPC protocol over stdin/stdout, defined by Microsoft:

```
Editor (nvim-dap)                    Debug Adapter (mixdbg.exe)
      |                                        |
      |  -- initialize request -->             |
      |  <-- capabilities response --          |
      |  <-- initialized event --              |
      |  -- setBreakpoints request -->         |
      |  -- configurationDone request -->      |
      |                                        |  (process starts running)
      |  <-- stopped event --                  |  (breakpoint hit!)
      |  -- stackTrace request -->             |
      |  <-- stack frames response --          |
      |  -- continue request -->               |
      |                                        |  (process resumes)
```

Key concepts:
- **Requests** flow from editor to adapter (e.g., "set a breakpoint", "continue", "give me the stack trace")
- **Responses** flow back with results
- **Events** flow from adapter to editor asynchronously (e.g., "the process stopped", "the process exited")
- The transport is **stdin/stdout** with `Content-Length` HTTP-style headers framing each JSON message

## Background: How dbgeng Works

`dbgeng.dll` is a Windows system DLL (the engine behind WinDbg). It provides COM interfaces for:

- **Launching/attaching** to processes (`IDebugClient`)
- **Controlling execution** — continue, step over, step into (`IDebugControl`)
- **Setting breakpoints** (`IDebugControl` + `IDebugBreakpoint`)
- **Reading symbols** — function names, source file/line mapping (`IDebugSymbols`)
- **Inspecting threads** (`IDebugSystemObjects`)
- **Receiving events** — breakpoint hit, process exit, module load (`IDebugEventCallbacks`)

The core loop is:

```
1. Create a debug client (DebugCreate)
2. Launch or attach to a process
3. Loop:
   a. Call WaitForEvent() — blocks until something happens
   b. Target is now stopped — inspect state, process commands
   c. Call SetExecutionStatus(GO) to resume
   d. Back to step (a)
```

**Critical constraint**: All dbgeng calls must happen on the same thread that created the client. This is why mixdbg has a dedicated "engine thread."

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

All services are stateless singletons; mutable state lives in model objects (`DapServerModel`, `DebugSessionModel`, `NativeDebuggerModel`, `DbgEngWrapperModel`). DAP requests are routed by `IDapDispatcher` to `IDapHandlerService` implementations (auto-discovered via assembly scanning). Each handler contains its own session logic and delegates to `IEngineLifecycleService` for engine lifecycle, `IBreakpointService` for breakpoints, and `IEngineQueryService` for stack traces, variables, and execution control. All dbgeng COM interop is encapsulated behind `IDbgEngWrapper` / `DbgEngWrapperService` — no COM types leak outside the wrapper boundary.

**Two threads, one queue:**

1. **Main thread** reads DAP JSON requests from stdin, dispatches them to handlers. Handlers own the dispatching: fire-and-forget via `model.Commands.Add(...)`, or synchronous queries via `model.QueueEngineQuery(...)`.

2. **Engine thread** creates the dbgeng client, launches the process, and runs the `WaitForEvent` loop. When the target stops, it processes queued commands (breakpoints, stack trace requests, etc.) and sends DAP events back via the server.

The `BlockingCollection<Action>` in `NativeDebuggerModel` is the bridge. `EngineLifecycleService` owns the engine thread and event loop; `BreakpointService` and `EngineQueryService` expose engine-thread methods (suffixed `OnEngine`) and delegate all COM calls to `IDbgEngWrapper` — handlers dispatch to them:

```csharp
// Handler (main thread) — synchronous query:
var frames = model.QueueEngineQuery(
    () => nativeDebugger.GetStackTraceOnEngine(model, maxFrames));

// Handler (main thread) — fire-and-forget:
model.Commands.Add(() => nativeDebugger.ExecuteContinueOnEngine(model));

// Engine thread (in command processing loop):
var cmd = model.Commands.Take();         // Dequeue
cmd();                                   // Execute — sets the TaskCompletionSource result
```

## How a Breakpoint Works End-to-End

This traces the full journey of a breakpoint from user action to debugger stop:

**1. User sets breakpoint** (presses `<Space>db` in Neovim on `Calculator.cpp:7`)

nvim-dap records it locally. No DAP message yet.

**2. User starts debugging** (presses `F5`, selects "Mixed C#/C++ (mixdbg)")

nvim-dap sends these DAP messages in order:
- `initialize` — handshake, exchange capabilities
- `setBreakpoints` — "I want a breakpoint at Calculator.cpp line 7"
- `launch` — "start WpfApp.exe"
- `configurationDone` — "I'm done configuring, you can run the process"

**3. mixdbg processes `setBreakpoints` (before launch)**

The engine doesn't exist yet, so the breakpoints are stored as "pending" and the response says `verified: true` (optimistically — we know native breakpoints will resolve).

**4. mixdbg processes `launch`**

Creates the engine thread, which calls `DebugCreate` + `CreateProcess` via dbgeng. The process is created in debug mode (suspended).

**5. mixdbg processes `configurationDone`**

Applies pending breakpoints on the engine thread. Since NativeLib.dll isn't loaded yet (the process just started), `GetOffsetByLine` fails. Fallback: use the dbgeng `bu` (breakpoint unresolved) command, which creates a **deferred breakpoint** — dbgeng will automatically resolve it when the module loads.

Then calls `SetExecutionStatus(GO)` — the process starts running.

**6. Process runs, modules load**

As the WPF app initializes, NativeLib.dll gets loaded. dbgeng detects this and resolves the deferred breakpoint to an actual address in `Calculator::Add`.

**7. User clicks the Add button in the WPF app**

The code path reaches `Calculator::Add` at line 7. The CPU hits the `int3` instruction placed by dbgeng. The OS suspends the process and notifies dbgeng.

**8. Engine thread: `WaitForEvent` returns**

The `IDebugEventCallbacks.Breakpoint` callback fires, setting `_hitUserBreakpoint = true`. The engine sends a DAP `stopped` event to nvim-dap.

**9. nvim-dap receives `stopped` event**

It requests `threads`, `stackTrace`, and `scopes`. Each request is queued to the engine thread, which reads dbgeng state and responds.

**10. nvim-dap jumps to Calculator.cpp:7**

The user sees their code with the execution arrow on the breakpoint line.

## COM Interop: How C# Talks to dbgeng

dbgeng exposes COM interfaces (C++ virtual function tables). To call them from C#, we use `[ComImport]` attributes:

```csharp
[ComImport, Guid("27fe5639-8407-4f47-8364-ee118fb08ac8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDebugClient
{
    void _VtblGap1_9();  // Skip 9 vtable slots we don't need

    [PreserveSig]
    int AttachProcess(ulong Server, uint ProcessId, uint AttachFlags);
    // ...
}
```

**`_VtblGap`**: COM interfaces have a fixed vtable layout. If `AttachProcess` is the 10th method (slot 9), we must skip the first 9 slots. The `_VtblGapN_M` naming convention tells the runtime to skip M vtable slots.

**`[PreserveSig]`**: By default, COM interop throws on failure HRESULTs. With `PreserveSig`, we get the raw `int` return value and check it ourselves.

**String marshaling**: dbgeng uses ANSI strings (char*). We pass `IntPtr` buffers and convert with `Marshal.PtrToStringAnsi()` — not `StringBuilder`, which defaults to UTF-16 on .NET.

## Event Callbacks: How dbgeng Notifies Us

We implement `IDebugEventCallbacks` to receive events during `WaitForEvent`:

```csharp
public int Breakpoint(IDebugBreakpoint Bp)
{
    Bp.GetId(out var id);
    model.HitUserBreakpoint = model.UserBreakpointIds.Contains(id);
    return StatusBreak;  // Tell dbgeng to stop (WaitForEvent returns)
}
```

The return value is critical — it tells dbgeng what to do:
- `DEBUG_STATUS_BREAK` (6) → stop execution, `WaitForEvent` returns
- `DEBUG_STATUS_GO` (1) → continue running, `WaitForEvent` does NOT return
- `DEBUG_STATUS_NO_CHANGE` (0) → use default behavior for this event type

Returning the wrong value causes subtle bugs (process never stops, or stops when it shouldn't).

## Process Startup Sequence

The startup has distinct phases:

```
Phase 1: Pre-configDone (engine just created)
  WaitForEvent returns with CREATE_PROCESS event
  Engine silently processes queued commands (setBreakpoints)
  Waits for Continue from configurationDone

Phase 2: Initial system stops (after first Continue)
  Initial breakpoint exception, loader events
  Engine auto-continues all non-user stops

Phase 3: Normal operation
  User breakpoints → send "stopped" event, wait for commands
  Step completions → send "stopped" event
  System events → auto-continue silently
```

## Breakpoint Classification

Not all files can be debugged via dbgeng:

| File type | Debuggable | Why |
|---|---|---|
| `.cpp`/`.c` in native project | Yes | Pure native code, PDB symbols |
| `.cpp`/`.h` in C++/CLI project | Yes | Managed via ICorDebug V4 piggybacked on dbgeng (detected via `<CLRSupport>` in vcxproj) |
| `.cs` | Yes | Managed via ICorDebug V4 piggybacked on dbgeng |

The adapter checks file extensions AND scans the vcxproj for `<CLRSupport>` to classify breakpoints. Native breakpoints use dbgeng directly. Managed breakpoints are resolved via PDB (method token + assembly name) and set as hardware breakpoints (`ba e1`) at the real JIT'd native address reported by the CLR Profiler. The profiler blocks until MixDbg confirms the BP is set, guaranteeing first-click breakpoints. Breakpoints received before the CLR loads are stored as pending and applied automatically once the runtime initializes.

## Diagnostic Logging

All debug sessions write to `~/mixdbg.log` via `ILoggingService` (with state in `LogStore`). Each entry is timestamped and tagged with the calling file name (via `[CallerFilePath]`):
- DAP requests/responses
- dbgeng events (type, thread, description)
- Breakpoint resolution (GetOffsetByLine results, deferred breakpoint creation)
- Stack frame resolution (symbol names, source lines)
- Engine state transitions

## Current Status

**Working (M1+M2+M3+M4):**
- DAP transport (JSON-RPC over stdin/stdout)
- Process launch and attach via dbgeng
- Native C++ breakpoints (including deferred)
- Stack traces with source locations (native via dbgeng, managed via CLR Profiler + PDB)
- Stepping (over, into, out)
- Thread enumeration
- Breakpoint classification (native vs managed vs CLI)
- Native variable inspection (locals, types, values, struct/pointer expansion)
- Managed module/function resolution via ICorDebug V4 piggybacked on dbgeng (`OpenVirtualProcessImpl` + `DbgEngDataTarget` bridge)
- PDB-based source mapping for C# via `System.Reflection.Metadata`
- Command-line argument passthrough in DAP launch requests
- **Unlimited managed breakpoints at exact source lines** via CLR Profiler DLL (`MixDbgProfiler.dll`) — FunctionEnter hooks detect each call to breakpointed methods, profiler temporarily disables hooks and blocks while MixDbg sets a transient hardware BP at the exact line address (via IL-to-native mapping), method runs and hits BP. No debug register limit — BPs are set/removed per call.
- **Managed stack traces** via profiler JIT method map + IL-to-native mapping — binary search maps native IPs to method tokens, reverse IL mapping resolves exact source file:line

**Not yet implemented:**
- Managed variable inspection (M5)
- Cross-boundary stepping (M6)

## Build and Run

```bash
cd mixdbg
dotnet build src/MixDbg.csproj -c Debug    # Debug adapter
cd profiler && make all                      # CLR Profiler DLL
```

MixDbg looks for `MixDbgProfiler.dll` next to its exe, or in `profiler/x64/Debug/` during development.

The adapter is configured in nvim-dap as an executable adapter. Select "Mixed C#/C++ (mixdbg)" from the debug config picker.
