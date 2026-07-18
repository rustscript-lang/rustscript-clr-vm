# CLR Callable Runtime Plan

## Status and scope

This plan tracks CLR VM support for the callable runtime introduced by
[`rustscript-lang/rustscript#4`](https://github.com/rustscript-lang/rustscript/pull/4).
The integration contract is VMBC v10, including script function metadata, callable prototypes,
function regions, root callable bindings, and exported callables.

The work is divided into four ordered goals:

1. decode and validate the VMBC v10 callable contract;
2. implement Program-owned callable values, environments, and real execution frames;
3. compile every root and script function region into resumable CLR IL;
4. expose RSS callables to C#, including queued UI event callbacks.

The first three goals provide bytecode execution parity. The fourth goal makes callable values
useful at the managed host boundary and is part of the required delivery, not an optional extension.

## Implementation checkpoint (2026-07-18)

All four implementation goals are present in this worktree against PR head
`b3b481ef9ce72d07763ae6911746ae0c06b19228`:

- Goal 1 is implemented by the v10-only reader/model validation, upstream encoder wrapper,
  synchronized builtin catalog, import-remap preservation, and wire negative tests.
- Goal 2 is implemented by Program-generation callable values, capture cells and environments,
  frame-relative locals, typed continuations, `CallValue`/`Ret`, pending-operation cancellation,
  reset, shutdown, and the 1,024-frame boundary.
- Goal 3 is implemented by region-aware stack analysis and one generated resumable IL dispatcher
  containing the root and every valid script function region. Typed CLR lowering remains enabled.
- Goal 4 is implemented by `IPdVmCallableProgram`, synchronous and asynchronous managed
  invocation, callback adapters, the serialized FIFO callback runner, lifecycle invalidation, and
  error observation. A Windows test raises a real WinForms `Button.Click` on an STA thread and
  verifies that the RSS callable executes after the originating event returns, calls a typed CLR
  binding, and updates a control on the form's owning STA thread. The Runner owns the native message
  loop; no hidden dispatcher form or second UI thread is created.

Current verification results:

- the upstream PR is merged at the pinned head, and every final check is successful;
- `dotnet build IronRust.sln -c Release` succeeds with zero warnings and zero errors;
- `cargo fmt --check` and all five native compiler tests pass;
- all callable/runtime/UI tests pass, including named and dynamic calls, closure identity and
  every capture mode, recursive self binding, recursion, pending resume/cancellation, stale handles,
  FIFO callbacks, pre-execution callback schema validation, error observation, and the WinForms
  button flow;
- CLR metadata bindings are lowered directly to uniquely named host imports, so the generated
  overlay does not create hundreds of script wrapper callables or leave normalized unit results at
  branch joins;
- the pinned Rust VM and generated CLR execute the same shared callable fixture and both produce
  `[42, 15, 1]` for closure dispatch, named calls, direct recursion, and mutual recursion;
- both the Notepad and embedded-bitmap Minesweeper examples pass RSS function values directly to
  independent click, mouse, closing, and timer bindings; Minesweeper has no action field or shared
  event switch, and both source-wrapper fixtures compile successfully;
- the unfiltered solution run passes all 66 tests: 62 in `PdVm.Tests` and 4 in
  `PdEdge.Http.Tests`.

## Upstream contract to freeze

Temporary development baseline: `b3b481ef9ce72d07763ae6911746ae0c06b19228`, the current verified
head of `rustscript-lang/rustscript#4`. This exact SHA is used until the upstream branch is rebased;
moving to another SHA requires regenerating wire fixtures and rerunning the cross-runtime
verification matrix.

Before updating the CLR dependency, the upstream branch must be rebased and the following details
must be treated as one versioned contract:

- VMBC version is 10. Older VMBC payloads remain unsupported by the v10 reader.
- `Call` remains direct builtin or host-import dispatch.
- `CallValue` is opcode `0x19` with one `argc: u8` operand.
- the stack segment before `CallValue` is `callee, arg0, ..., argN`;
- a completed callable invocation leaves exactly one result, using `null` for unit;
- `Ret` completes the active frame and follows its typed continuation;
- branches remain inside the active function region;
- callable constants are invalid;
- callable values and exported callback handles belong to one Program generation;
- script recursion is limited to 1,024 active script frames;
- the VMBC tail contains, in order, script functions, callable prototypes, function regions,
  root callable bindings, and exported callables;
- `ValueType::Callable` uses tag `9`;
- internal callable operations use the synchronized builtin catalog, including
  `BindCallable` and `DetachLocal`.

The upstream documentation currently mentioning VMBC v9 must be corrected before a release pin is
selected. During development, an exact upstream commit may be used on a feature branch, but the
release pin must refer to the rebased result.

## Goal 1: VMBC v10 wire model and validation

### Outcome

`PdVm.Compiler` can read a v10 payload without discarding callable metadata, rejects malformed
metadata before IL emission, and preserves the metadata through source-overlay import remapping.
The bundled native compiler emits the same wire format as the pinned upstream compiler.

### Model changes

Add CLR representations for:

- `PdVmCallableKind`: function item, closure, or host function;
- `PdVmCaptureBindingMode`: copy, borrow, mutable borrow, or move;
- `PdVmCallableTarget`: script function ID or host import ID;
- `PdVmScriptFunction`: entry IP and end IP;
- `PdVmCallablePrototype`: kind, target, arity, frame local count, parameter slots,
  capture source slots, capture target slots, capture modes, optional self slot, and schema;
- `PdVmFunctionRegion`: start IP, end IP, and optional prototype ID;
- `PdVmRootCallableBinding`: root local slot and prototype ID;
- `PdVmExportedCallable`: exported name and local slot;
- callable `PdVmValueType` and the callable schema shape needed for managed invocation checks.

Extend `PdVmProgramModel` so every reconstruction path preserves these collections. In particular,
the import remap in `PdVmDotNetSourceCompiler` must copy callable metadata rather than rebuilding a
root-only model.

### Reader and native compiler

Update `PdVmVmbcReader` to:

- accept only VMBC v10;
- decode `CallValue` and its one-byte operand;
- decode callable value type tag `9`;
- retain callable schemas instead of skipping them;
- read the callable metadata tail after debug info;
- validate all lengths and checked integer conversions before allocation;
- reject trailing bytes.

Replace the duplicate v8 writer in `native/pd-vm-compiler/src/vmbc.rs` with upstream
`encode_program` where possible. The existing Unicode repair behavior must remain covered by a
focused test. If normalization is still required, perform it before calling the upstream encoder
instead of maintaining a second wire layout.

Update the Rust dependency pin and generate or synchronize the complete builtin catalog from that
same pin. A parity test must compare call indexes, arities, and special builtin IDs so a catalog
change cannot silently alter CLR dispatch.

### Validation rules

The compiler must reject a Program when:

- a function entry or end is not an instruction boundary;
- function regions overlap, leave bytecode uncovered, or exceed bytecode bounds;
- a branch leaves its active region;
- a prototype references a missing function or import;
- parameter, capture, self, root binding, or export slots exceed their local layout;
- capture source, target, and mode arrays have different lengths;
- `CallValue` or `Call` operands are truncated;
- a direct call has an invalid import index or arity;
- a callable appears in the constants table.

### Goal 1 acceptance

- v10 Rust-generated fixtures decode and round-trip through the CLR model.
- v8 and v9 fixtures fail with a precise unsupported-version error.
- malformed callable metadata fixtures fail before assembly generation.
- import remapping changes only import names and retains all callable metadata byte-for-byte at the
  model level.
- the native compiler and upstream encoder emit equivalent v10 payloads for the same source.

## Goal 2: callable values, environments, and execution frames

### Outcome

`PdVm.Runtime` implements the same callable identity, capture, frame, suspension, reset, and return
rules as the Rust runtime. Managed garbage collection may own the CLR objects, but Program
generation checks remain explicit so old callables cannot enter a replacement Program.

### Runtime data structures

Add:

- `PdVmCallableValue`, containing prototype ID, callable kind, optional environment, and Program
  generation token;
- `PdVmCallableEnvironment`, containing shared capture cells;
- `PdVmCaptureCell`, holding a mutable `PdVmValue` shared by borrowed captures;
- `PdVmExecutionFrame`, containing continuation, operand stack base, local base, local count, and
  optional prototype ID;
- `PdVmFrameContinuation`: halt, resume bytecode at an IP, or return to C#;
- a local stack and capture-cell map owned by `PdVmProgramBase`;
- a Program generation registry used to validate managed callable handles and callbacks.

Extend `PdVmValue` with a callable kind and accessors. Equality follows the upstream rules:

- capture-free function items compare by prototype identity within the same Program generation;
- closure aliases compare by environment identity;
- separate closure evaluations compare unequal.

Display and `type_of` report a callable value without exposing internal addresses. Arithmetic,
ordering, shifts, JSON constants, and other unsupported operations reject callable operands through
the existing typed error paths.

### Frame and local semantics

The root invocation starts with an explicit halt continuation. `CallValue` must:

1. validate the stack shape, Program generation, prototype, arity, and argument schema;
2. remove `callee, args...` from the caller operand segment;
3. allocate the target local frame;
4. initialize root callable bindings visible in the target layout;
5. bind arguments, captures, and the optional self slot;
6. push a resume-bytecode frame and transfer to the script function entry;
7. enforce the 1,024-frame limit.

`Ldloc` and `Stloc` resolve relative to the active frame. A borrowed capture uses one shared cell;
copy creates an independent value; move transfers the value and clears the source association.
`BindCallable` creates the environment from prototype metadata, and `DetachLocal` removes a moved
local from its prior capture cell.

`Ret` must:

1. normalize the frame result to exactly one value;
2. clear extra operand values above the frame base;
3. remove frame-local and capture-cell associations;
4. validate the return schema;
5. resume the caller, return the value to C#, or halt according to the continuation.

Yield and pending host calls retain the complete frame stack, operand state, local stack, callable
roots, and active continuation. Resume continues after the host operation without replaying the
call.

### Lifecycle

`ResetForReuse`, shutdown, and Program replacement must:

- cancel a pending host operation;
- clear callback work queues;
- invalidate the current Program generation and its exported callback handles;
- release runtime callable roots and capture environments;
- recreate root locals and root callable bindings;
- return to one root halt frame for a reusable Program.

`IAsyncPdVmHost` needs an additive cancellation hook so reset and shutdown can notify the host of a
pending operation.

### Goal 2 acceptance

- direct calls, nested calls, direct recursion, and mutual recursion have isolated locals.
- copy, borrow, mutable borrow, move, self capture, and escaping closure cases match Rust results.
- callable aliases in arrays and maps retain the correct environment identity.
- yield and pending operations resume inside nested callable frames without replay.
- reset and shutdown invalidate old callable handles and cancel pending host work.
- recursion depth 1,024 is accepted and the next entry reports call-stack overflow.

## Goal 3: resumable CLR IL for all function regions

### Outcome

`PdVmClrCompiler` emits executable IL for the root region and every script function region while
retaining the existing typed arithmetic and intrinsic builtin paths.

### Compiler design

Keep one generated `RunStep` state machine per Program. Compile all function regions into the same
method-level instruction dispatcher and use runtime frame helpers for inter-region control flow.
This design fits the existing instruction-pointer resume model and avoids generating a second
managed state machine for each RSS function.

Refactor `PdVmStackAnalyzer` to:

- seed the root region and every function entry with stack depth zero relative to its frame operand
  base;
- analyze successors only inside the current region;
- calculate per-region and global maximum evaluation depth;
- treat `CallValue(argc)` as popping callee plus arguments and pushing one normalized result;
- treat `Ret` as a region terminator;
- reject incompatible join depths and fallthrough outside the region.

Refactor IL emission so:

- every validated region receives labels and emitted instructions;
- evaluation locals are restored from and persisted to the active operand segment;
- `Ldloc` and `Stloc` call frame-relative runtime helpers;
- direct `Call` retains its current builtin/import path;
- `CallValue` persists evaluation state, asks the runtime to enter a callable, and dispatches to the
  new instruction pointer;
- `Ret` persists the result, asks the runtime to complete the active frame, and either dispatches to
  the caller IP or returns the resulting status;
- instruction budgets include callable entry and return transitions consistently;
- typed IL shortcuts remain enabled when the v10 type map proves operand types.

Generated assemblies must carry the callable prototype, region, root-binding, export, and schema
tables needed by `PdVmProgramBase`; they must not contain the original VMBC payload.

### Goal 3 acceptance

- every valid script function body is present in the generated assembly and is reachable only
  through its callable prototype.
- named calls, dynamic selection between callable values, recursion, and closure calls run without
  an interpreter fallback.
- a pending or yielding host call inside a script callable resumes at the exact CLR instruction.
- generated root-only v10 Programs retain current arithmetic and builtin behavior.
- IL inspection tests confirm typed numeric operations still use native CLR instructions.

## Goal 4: C# callable API and UI event callbacks

### Outcome

C# can resolve an exported RSS function, invoke it synchronously or asynchronously, retain it as a
managed callback, and bind it to a typical UI event without re-entering an already running Program.

### Public managed API

Use an additive interface so existing `IPdVmProgram` consumers remain loadable:

```csharp
public interface IPdVmCallableProgram : IPdVmProgram, IDisposable
{
    Action<Exception>? CallbackErrorObserver { get; set; }

    PdVmScriptCallable ResolveCallable(string exportName);

    PdVmScriptCallable CreateCallable(PdVmCallableValue callable);

    PdVmStatus StartCallable(
        PdVmScriptCallable callable,
        IReadOnlyList<PdVmValue> args,
        IPdVmHost host);

    PdVmValue? TakeCallableResult();

    ValueTask<PdVmValue> InvokeCallableAsync(
        PdVmScriptCallable callable,
        IReadOnlyList<PdVmValue> args,
        IAsyncPdVmHost host,
        CancellationToken cancellationToken = default);

    PdVmScriptCallback<TArgs, TResult> CreateCallback<TArgs, TResult>(
        string exportName,
        IPdVmCallbackAdapter<TArgs, TResult> adapter,
        IAsyncPdVmHost host);

    PdVmScriptCallback<TArgs, TResult> CreateCallback<TArgs, TResult>(
        PdVmScriptCallable callable,
        IPdVmCallbackAdapter<TArgs, TResult> adapter,
        IAsyncPdVmHost host);

    void ResetForReuse();
    void Shutdown();
}
```

`PdVmScriptCallable` is an opaque managed handle containing the Program generation token and the
runtime callable value. Every entry API validates ownership, callable schema, and arity.

`PdVmScriptCallback<TArgs, TResult>` provides:

- `InvokeAsync` for managed code that needs the RSS return value;
- `Post` for fire-and-forget UI events;
- `AsEventHandler` for zero-payload or adapter-backed .NET events;
- `Dispose`/`Unsubscribe` to stop future invocations and remove attached event delegates;
- an error channel for queued callback failures so exceptions are not raised on the UI message
  loop by default.

Adapters convert managed event arguments to `PdVmValue` arrays and convert the RSS result back to
`TResult`. Each adapter may declare its argument and result `PdVmValueType` contract; callback
creation checks that contract against the exported callable schema before any RSS instruction can
run. The initial required adapters are:

- unit arguments and unit result for button/menu click events;
- one `PdVmValue` argument for hosts that already provide VM values;
- a map payload adapter for selected UI event data;
- a custom delegate adapter for application-specific event types.

### Queue and re-entry rules

Each callable Program owns one FIFO callback queue and one serialized execution gate.

- UI delegates only enqueue work; they never call `RunStep` recursively.
- if the Program is running, yielded, or waiting, new events remain queued.
- one runner drains callbacks in order after the current invocation reaches a resumable boundary.
- a callback may yield or wait; the next queued callback begins only after its result is finalized.
- reset, shutdown, callback disposal, or Program replacement rejects queued work associated with the
  invalid generation.
- no implicit event coalescing is performed.
- callback errors are delivered to the returned task or a configurable callback-error observer.

For WinForms, `PdVmWinFormsApplication` attaches the callable Program to the Runner's STA thread and
owns the native message loop. Event callbacks are scheduled with the main form's `BeginInvoke`, so
RSS execution begins after the originating UI event returns while remaining on the same STA thread.
Typed UI bindings execute directly against their owning controls; there is no centralized hidden
window or dedicated dispatcher thread.

Generated `EventLoop` declarations type callback parameters as `fn(...) -> null`. RSS therefore
passes the callable value itself, for example `Ui::BindClick(form, button, on_click)`. The CLR host
converts that VM value to `PdVmCallableValue`, validates its Program generation and signature, and
retains it in the form session; no export-name lookup occurs at the binding point.

### Typical UI event flow

RSS source:

```rust
pub fn on_click() -> null {
    System::Windows::Forms::MessageBox::Show("Called from RSS");
}

Ui::BindClick(form, button, on_click);
```

C# host:

```csharp
var program = (IPdVmCallableProgram)PdVmAssemblyLoader.LoadProgram(programDll);
var callback = program.CreateCallback<PdVmUnit, PdVmUnit>(
    "on_click",
    PdVmCallbackAdapters.Unit,
    host).ScheduleOn(
        SynchronizationContext.Current ??
        throw new InvalidOperationException("a UI synchronization context is required"));

button.Click += callback.AsEventHandler();
form.FormClosed += (_, _) => callback.Dispose();
```

The generated event delegate enqueues `on_click`. The Program runner executes the callable through
the same `CallValue` frame path used by RSS-to-RSS calls. If the callback invokes an async host
operation, its task completes after resume and frame return.

### Goal 4 acceptance

- C# resolves an exported RSS function by name and rejects unknown or non-callable exports.
- C# invokes a callable with zero or more arguments and receives its typed result.
- synchronous invocation rejects yield/wait with a focused diagnostic; async invocation supports
  both states.
- a WinForms button click invokes an RSS callback and the UI thread remains responsive.
- repeated clicks are processed in FIFO order without recursive `RunStep` entry.
- an RSS callback may call typed CLR bindings and update a control on the owning STA thread.
- callback disposal removes the delegate and rejects later posts.
- reset, shutdown, and Program replacement invalidate old callback handles and queued events.
- callback argument and result schema mismatches fail before RSS execution begins.
- callback exceptions reach the configured error observer and do not terminate the UI message loop.

## Delivery sequence

### CLR PR A: `vmbc-v10-callable-model`

- dependency pin and native encoder update;
- wire models, reader, validation, builtin catalog synchronization;
- v10 golden fixtures and negative wire tests.

### CLR PR B: `callable-runtime-frames`

- callable values and environments;
- frame-relative locals, `CallValue`, `Ret`, capture operations;
- suspension, cancellation, reset, and lifecycle tests.

### CLR PR C: `callable-clr-codegen`

- region-aware stack analysis;
- unified resumable IL dispatcher;
- recursive/dynamic/closure IL execution and inspection tests.

### CLR PR D: `csharp-script-callbacks`

- exported callable resolution and managed invocation APIs;
- callback adapters, FIFO runner, invalidation registry;
- WinForms event integration and end-to-end UI tests.

Each PR depends on the prior PR and must leave the solution buildable. Feature-level completion is
declared only after PR D passes the full parity matrix.

## Cross-runtime verification matrix

The same RustScript sources must be compiled by the pinned native compiler and executed by the Rust
runtime and generated CLR assembly. Compare final values, error category, status sequence, and host
call count for:

- root return and nested return;
- named call and dynamic callable selection;
- direct and mutual recursion;
- closure creation, aliasing, and all capture modes;
- callable values stored in arrays and maps;
- callable host imports;
- yield and pending/resume inside one or more script frames;
- cancellation, reset, shutdown, and stale handle rejection;
- direct C# invocation and queued C# UI invocation;
- invalid arity, schema, ownership, branch target, and recursion depth.

CI gates include formatting, native compiler tests, CLR unit tests, generated IL inspection, wire
fixtures, Windows WinForms callback tests, and release builds.

## Completion criteria

The feature is complete when VMBC v10 is the only emitted and accepted callable format, all valid
script bodies execute as generated CLR IL, callable values follow the Program generation lifecycle,
and a C# UI event can invoke an exported RSS callable through the serialized callback queue with
correct async resume and teardown behavior.
