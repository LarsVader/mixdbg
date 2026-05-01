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

## Silent slot overflow in profiler m_funcSlots

**Identified:** 2026-05-01

`RegisterWatchedFunction` in `MixDbgProfiler.cpp` uses a fixed-size array (`MAX_WATCHED_FUNCS = 64`). When all slots are used, the registration silently fails — the `for` loop finds no free slot and breaks without logging. The method's ENTER/LEAVE hooks will never fire, so its breakpoint will never be installed.

**Why it's low risk today:** With assembly-level watches removed, only methods with exact WATCH tokens consume slots. A session would need 65+ distinct managed breakpoints to hit this. Typical usage is well under 10.

**Fix options:**
- Replace the fixed array with a `std::unordered_map<FunctionID, FunctionWatchInfo>` (best, dynamic sizing)
- Log a warning when the table is full so failures are visible
- Increase `MAX_WATCHED_FUNCS` (band-aid)
