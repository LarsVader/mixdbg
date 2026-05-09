# Technical Debt & Deferred Issues

Items identified during code reviews that are accepted risks or non-urgent improvements. Act on these when working in the relevant area.

## Thread Safety: Dictionary reads from profiler pipe reader thread

**Identified:** 2026-05-01

`ParseEnterNotification` and `ParseLeaveOrTailcallNotification` in `ProfilerPipeService.cs` read `model.ManagedBpPlans` and `model.ActiveMethodBreakpoints` (regular `Dictionary`) from the pipe reader thread, while the engine thread writes to them. `Dictionary.ContainsKey` is not thread-safe for concurrent read/write — a resize on the engine thread during a read could cause `InvalidOperationException` or a missed key.

**Why it's low risk today:** The engine thread is typically in `WaitForEvent` (blocked) when the pipe reader processes notifications. Writes to these dictionaries happen during breakpoint setup (engine stopped) or step resolution (engine stopped). The window for a concurrent resize is very narrow.

**Fix options:**
- Replace with `ConcurrentDictionary` (simplest, small perf cost from locking)
- Take a snapshot (`ToArray()`) before the pipe reader checks (allocation per batch)
- Add a `ReaderWriterLockSlim` around dictionary access (most correct, most invasive)

## LocateProfilerDll is not unit-testable

**Identified:** 2026-05-07

`ProfilerPipeService.LocateProfilerDll` looks for `MixDbgProfiler.dll` next to the exe and falls back to `repoRoot/profiler/x64/Debug/MixDbgProfiler.dll`. The fallback exists on every dev machine and on CI, so a unit test like `SetupProfilerPipeForAttach_WhenProfilerNotFound_DoesNotCallIpc` cannot reliably arrange the not-found state without filesystem manipulation that races with parallel tests and the integration suite. The test currently sits behind `[Fact(Skip = ...)]`.

**Why it's low risk today:** The success path is exercised by `AttachIntegrationTest` and the launch-mode tests; the failure path is verified by inspection.

**Fix options:**
- Inject the resolver behind an interface (`IProfilerLocator`) so unit tests can stub it.
- Pass the override path via constructor injection / config.
- Accept the test as inherently integration-only and delete the unit-test stub.

## Flaky integration test: Loop_WhenNativeBpInsideLoop_StopsInLoopBody

**Identified:** 2026-05-14

`ComplexScenarioIntegrationTest.Loop_WhenNativeBpInsideLoop_StopsInLoopBody` intermittently fails on the assertion `ThenStackTraceHasSource(0, "Calculator.cpp")` at line 240. When it fails, the captured top-frame source path is not from `Calculator.cpp` but from elsewhere in the test tree (truncated as `D:\Lars\Dokumente\coding\CLRApp3\mixdbg\t…`). Passes consistently when run in isolation; the flake only manifests inside the full serial integration suite. Two consecutive full-suite runs (2026-05-13 and 2026-05-14) failed on this single test with identical symptoms.

**Why it's low risk today:** Pure test-side timing flake — the production stack-trace / native source resolution code is correct, the test races against engine-state timing on the BP hit. The same scenario is also covered by adjacent `ComplexScenarioIntegrationTest` cases that exercise the loop-BP path more directly.

**Fix options (preferred: option 2):**
- Have `WhenWaitingForStoppedEvent` correlate the stack trace with the *specific* stopped event (the test currently requests stack trace immediately and may capture state from a slightly earlier/later transition).
- **(preferred)** Add a deterministic wait — read the breakpoint ID from the stopped event, then assert the top frame's source from a payload tied to that BP rather than the most-recent stack trace. Targets the actual race rather than masking it.
- Quarantine the assertion behind `ThenStackTraceHasSourceOneOf("Calculator.cpp", "MainWindow.xaml.cs")` only if option 2 reveals that the engine genuinely reports the C# caller frame first under load.

## DapStartupIntegrationTest parser uses char-length where DAP specifies byte-length

**Identified:** 2026-05-14

`ParseDapMessages` in `test/IntegrationTests/DapStartupIntegrationTest.cs` reads `Content-Length` and slices the UTF-16 `_partialBuffer` by that number of chars. The DAP spec defines `Content-Length` as the UTF-8 byte length, so the two only coincide for ASCII payloads. Today the test only exchanges ASCII (initialize/launch/configurationDone JSON), but `feat(output)` now forwards debuggee `Trace.WriteLine` output as DAP `output` events — a non-en-US CI machine running WpfApp could surface non-ASCII Trace text (date formatting, exception messages, registry strings) that desynchronises the parser. Failure mode: silent timeout via `Assert.Fail` with an unhelpful "no terminated event" diagnostic.

**Why it's low risk today:** Current tests run on en-US dev/CI with WpfApp emitting only ASCII Trace output. No reproduction yet.

**Fix options:**
- Keep `_partialBuffer` as `byte[]` / `MemoryStream`, slice by byte count, decode JSON from the byte slice. Most correct.
- Convert `Content-Length` to a char count by re-encoding the header → bytes → char-equivalent before slicing. Brittle.
- Restrict the test to ASCII outputs explicitly and document the assumption — not a real fix.

## Engine thread can still be pinned by a stalled DAP client (non-debuggee events)

**Identified:** 2026-05-15

The `OnDebuggeeOutput` producer/consumer split moved debuggee-text `SendEvent` off the engine thread (queue → dedicated writer). But the engine thread still calls `_server.SendEvent` synchronously for `stopped`, `terminated`, `output` (e.g. module-load chatter), and others. All of those acquire `DapServerModel.WriteLock`. If the dedicated writer is mid-flush against a wedged DAP client, the engine thread blocks waiting for the lock — exactly the failure mode the producer/consumer split was intended to prevent for debuggee output.

**Why it's low risk today:** Local DAP transports (nvim-dap over stdio) drain quickly. The pin only manifests when the client itself stops reading, which in practice means it's already dead.

**Fix options:**
- Route ALL engine-thread→DAP events through `DebuggeeOutputQueue` (or a sibling queue) so the engine thread never holds the write lock. Most thorough.
- Use a `Try`-style write that times out and drops the message rather than blocking the engine thread.
- Accept the residual risk and document the assumption that the client must drain promptly.

## Silent slot overflow in profiler m_funcSlots

**Identified:** 2026-05-01

`RegisterWatchedFunction` in `MixDbgProfiler.cpp` uses a fixed-size array (`MAX_WATCHED_FUNCS = 64`). When all slots are used, the registration silently fails — the `for` loop finds no free slot and breaks without logging. The method's ENTER/LEAVE hooks will never fire, so its breakpoint will never be installed.

**Why it's low risk today:** With assembly-level watches removed, only methods with exact WATCH tokens consume slots. A session would need 65+ distinct managed breakpoints to hit this. Typical usage is well under 10.

**Fix options:**
- Replace the fixed array with a `std::unordered_map<FunctionID, FunctionWatchInfo>` (best, dynamic sizing)
- Log a warning when the table is full so failures are visible
- Increase `MAX_WATCHED_FUNCS` (band-aid)
