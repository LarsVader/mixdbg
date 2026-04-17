# DAP, dbgeng, and COM — Technical Deep Dive

How mixdbg bridges the Debug Adapter Protocol with the Windows Debug Engine.

## How Debuggers Talk to Editors

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

## How dbgeng Works

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
