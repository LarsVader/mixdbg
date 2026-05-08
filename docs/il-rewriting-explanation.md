# IL Rewriting for Managed Breakpoints — Conceptual Background

> Why mixdbg's launch-mode managed BPs use the design they do, why attach mode
> can't use that design directly, and what IL rewriting would change. Written
> for a reader new to debugger internals.

## The basic problem: how does a breakpoint actually work?

A breakpoint isn't magic. The CPU has to physically stop when it executes the
instruction at a specific memory address. Two ways to do that on x86-64:

1. **Software breakpoint** — overwrite the byte at that address with `INT 3`
   (`0xCC`). When the CPU hits it, it traps into the debugger. Cheap, unlimited
   count. But you need write access to the memory and have to remember the
   original byte to restore it.

2. **Hardware breakpoint** — the CPU has 4 special registers (DR0–DR3) that
   can hold "stop when execution reaches address X". No memory modification
   needed. But there are **only 4 of them** in the entire process, total.
   That's a hardware limit, not a software one.

For .NET / managed code, mixdbg uses hardware breakpoints because messing with
JIT-compiled memory pages is risky (the JIT can move/replace code at any time;
software BPs get lost). So we have a 4-BP-at-a-time budget across the whole
process.

## How mixdbg sidesteps the 4-BP limit (in launch mode)

A user might set 50 breakpoints. We obviously can't have 50 hardware BPs
active. The trick mixdbg uses (called **M4V3** in the docs):

> A breakpoint only needs to be "active" while the program is actually inside
> the method that contains it.

If method `Foo()` has a breakpoint on line 65, that BP only matters while
`Foo()` is currently executing. The instant `Foo` returns, the BP can be
uninstalled — nothing else will hit it until `Foo` is called again.

So the scheme is:

- When `Foo()` is **entered** → install the hardware BP at line 65
- When `Foo()` **exits** → uninstall it
- 50 user BPs across 50 methods → still only need ~1–4 hardware BPs at any
  moment (one per method currently on the call stack)

That works because the .NET runtime can tell the profiler **"hey, method X
just got called"** and **"hey, method X just returned"** — those are called
the **ENTER** and **LEAVE** hooks. The profiler tells mixdbg, mixdbg installs
or uninstalls the HW BP. Result: unlimited BPs, no 4-cap.

## Why attach breaks this

When you attach to an already-running process (instead of launching it), the
.NET runtime says **"sorry, no ENTER/LEAVE hooks for you."** This is a hard
CLR rule: those hooks have to be set before any method is JIT-compiled, and
at attach time methods are already compiled. The flag
`COR_PRF_MONITOR_ENTERLEAVE` is explicitly NOT in
`COR_PRF_ALLOWABLE_AFTER_ATTACH_FLAGS`.

So the install-on-ENTER / uninstall-on-LEAVE trick stops working in attach.
We're stuck with permanent HW BPs and the 4-at-a-time cap. This is the
documented limitation of M7 attach as currently shipped.

## What the rewriter would do

Other .NET tools (Datadog, New Relic, OpenTelemetry .NET auto-instrumentation)
faced this same restriction and invented a workaround: **fake your own
ENTER/LEAVE notifications by modifying the method's IL bytecode at runtime.**

If method `Foo()` originally looks like (in pseudocode):

```
void Foo() {
    DoStuff();
    return;
}
```

The rewriter would change it to:

```
void Foo() {
    MixDbgHelper.Enter("Foo");      // ← injected
    try {
        DoStuff();
        // (rewritten: 'return' becomes 'jump to end')
    }
    finally {
        MixDbgHelper.Leave("Foo");  // ← injected, runs even on exception
    }
    return;
}
```

`MixDbgHelper.Enter` and `MixDbgHelper.Leave` are functions exported by the
profiler DLL (already shipped — see `profiler/MixDbgHelper.cpp`). When `Foo`
is called now, it tells mixdbg "I'm entering" and "I'm leaving" — exactly
mimicking what the runtime ENTER/LEAVE hooks would do, just by injected code
instead of runtime callbacks.

The runtime supports this rewriting via an API called **ReJIT**: "throw away
the current compiled version of method X and recompile it from this new IL
I'm giving you."

## The better idea: BP-at-line rewriting

Instead of injecting just at method entry/exit, inject a hook **at every
breakpointed line**:

```
void Foo() {
    int a = 1;
    MixDbgHelper.HitBreakpoint(token=0xFOO, line=42);  // ← BP on line 42
    int b = a + 1;
    MixDbgHelper.HitBreakpoint(token=0xFOO, line=43);  // ← BP on line 43
    DoStuff(b);
    MixDbgHelper.HitBreakpoint(token=0xFOO, line=45);  // ← BP on line 45
}
```

`HitBreakpoint` is just a P/Invoke into the profiler DLL. The profiler tells
mixdbg "thread X hit BP at line Y." Mixdbg sends a `stopped` event to the
IDE. When the user continues, the helper returns and execution proceeds.

### Why it's strictly better than the ENTER/LEAVE + HW-BP scheme

- **Unlimited BPs everywhere.** No 4-slot cap, no per-method bookkeeping.
  500 BPs in one method? Fine. The CPU's debug registers are no longer
  involved at all — the BP "fires" because the method's own code calls into
  the profiler.

- **No ENTER/LEAVE infrastructure needed.** The whole `ActiveMethodBreakpoint`
  activation-counting machinery, the ACK event, the install-on-enter /
  uninstall-on-leave choreography — gone. You don't need to know when a method
  starts or ends. You just need to know which lines to inject hooks at.

- **Conditional BPs become trivial.** Want a BP that only fires when
  `count > 100`? Inject:
  ```
  if (count > 100) MixDbgHelper.HitBreakpoint(...);
  ```
  The condition is evaluated by managed code in the target process — no IPC
  round-trip per call. Dramatically faster than the typical "stop, ask
  debugger to evaluate condition, resume if false" pattern.

- **Logpoints** (BP that prints instead of stopping) are a one-line change to
  the helper.

- **Tracepoints / statement coverage** fall out naturally — inject at every
  statement, not just user BPs.

### What it costs

1. **Detecting line-to-IL-offset mapping.** When the user sets a BP on line
   42, we need to know that line 42 corresponds to (say) IL offset 0x14 in
   method `Foo`. PDBs have this info — mixdbg already reads it via
   `PdbSourceMapperService.GetMethodSequencePoints`. So this part is
   essentially free.

2. **The IL rewriter itself.** Bytecode-level fiddly work. Simpler than the
   ENTER/LEAVE wrapper: you're just inserting `ldc.i4 + ldc.i4 + call`
   triples at specific offsets, no try/finally, no `ret` rewriting, no
   exception-clause preservation drama. Estimated ~600 lines vs ~1000 for
   the ENTER/LEAVE wrapper.

3. **Performance overhead in the target.** Every breakpointed line now has a
   function call inserted. If you're not stopped on the BP, the call still
   happens — it just returns immediately because there's no debugger stop to
   do. For a hot loop with a BP inside, this could be measurable. But in
   practice: hot loops aren't where you put BPs, and the call is cheap
   (~tens of nanoseconds).

4. **You still need ReJIT for already-compiled methods in attach mode.** Same
   as before — call `RequestReJIT` to recompile with the new instrumented IL.

## Why "1000 lines" for the basic ENTER/LEAVE wrapper

The transformation conceptually fits on a napkin. The reason real code is
~1000 lines:

- **IL is a binary format.** You don't write it as text; you write raw bytes.
  Every instruction's opcode + operands is encoded by hand. ~256 opcodes,
  each with different operand shapes (1 byte, 4 bytes, length-prefixed string
  blobs, etc.).

- **`return` becomes `jump to end`** — but the "jump" instruction has
  different sizes depending on how far it jumps. If a short jump (1-byte
  offset) needs to suddenly become a long jump (4-byte offset) because the
  rewritten code grew, you have to recompute every other jump in the method
  that might have shifted.

- **Defining the helper functions** isn't a one-liner. To call
  `MixDbgHelper.Enter` from inside the method, the runtime needs to know
  "there's a function called Enter in module XYZ that's a P/Invoke into a
  DLL." You have to add that declaration to the assembly's metadata at
  runtime via another awkward COM API (`IMetaDataEmit2`).

- **Exception handlers in the original method** must be preserved exactly —
  but their byte offsets all shift when you prepend the Enter call, so you
  have to walk the method's exception-clause table and adjust every offset.

- **Edge cases** — methods with weird control flow (`tail.call`, `switch`
  tables, `leave` inside existing finally blocks, generic methods) all need
  correct handling or the runtime rejects the IL with `InvalidProgramException`
  and your method crashes.

It's not algorithmically hard. It's just a lot of fiddly bytecode-level work
where one off-by-one error makes the whole method silently broken.

## Why the launch path doesn't already do BP-at-line rewriting

When M4V3 was designed, the team went with the "natural" approach using
runtime ENTER/LEAVE hooks because they exist and seem to fit. The
IL-rewriting alternative is **a real architectural decision worth
reconsidering** — Datadog, New Relic, and OpenTelemetry all chose IL
rewriting as their primary instrumentation mechanism precisely because it's
more flexible and avoids the runtime-flag restrictions.

If we did BP-at-line rewriting everywhere, we could **delete a huge amount of
mixdbg code**:

- The whole CLR profiler ENTER/LEAVE hook plumbing
- The hardware-BP-installation path
- The `JitMethodMappings` IL-to-native lookup (we'd jump straight to IL
  offsets, not native addresses)
- The activation-counting state machine
- The ACK event sync

A lot of the complexity in M4V3 is working around the ENTER/LEAVE + HW-BP
limitations. With IL rewriting, those limitations don't exist.

This is captured as the **M9 milestone** — see
[`m9-il-rewriting-bps.md`](m9-il-rewriting-bps.md) for the implementation
plan.
