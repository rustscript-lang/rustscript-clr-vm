namespace PdVm.Runtime;

public abstract class PdVmProgramBase : IPdVmCallableProgram
{
    private const int MaximumScriptFrameDepth = 1024;

    private readonly List<PdVmValue> _stack = new();
    private readonly List<PdVmValue> _locals = new();
    private readonly Dictionary<int, PdVmCaptureCell> _captureCells = new();
    private readonly Dictionary<int, PdVmCaptureCell> _mutableBorrowAliases = new();
    private readonly HashSet<PdVmCaptureCell> _mutableBorrowCells = new(ReferenceEqualityComparer.Instance);
    private readonly List<PdVmExecutionFrame> _executionFrames = new();
    private readonly SemaphoreSlim _managedExecutionGate = new(1, 1);
    private readonly object _callbackQueueLock = new();
    private readonly Queue<QueuedCallbackWork> _callbackQueue = new();
    private readonly Dictionary<long, MapIteratorState> _mapIterators = new();
    private readonly int _rootLocalCount;
    private readonly PdVmProgramMetadata _metadata;
    private PdVmStatus _lastStatus = PdVmStatus.Halted();
    private ulong? _pendingOpId;
    private IAsyncPdVmHost? _pendingHost;
    private bool _pendingNormalizeResult;
    private long _executedInstructionCount;
    private object _generation = new();
    private PdVmValue? _managedCallableResult;
    private bool _shutdown;
    private bool _callbackRunnerActive;
    private CancellationTokenSource _callbackRunnerCancellation = new();
    private (PdVmValue Value, PdVmCaptureCell Cell)? _lastBorrowedCapture;

    private sealed record QueuedCallbackWork(
        object Generation,
        Func<bool> IsActive,
        Func<CancellationToken, ValueTask> Execute,
        Action<Exception> Reject,
        SynchronizationContext? SynchronizationContext,
        CancellationToken CancellationToken);

    private sealed class MapIteratorState
    {
        internal MapIteratorState(IReadOnlyList<KeyValuePair<PdVmValue, PdVmValue>> entries)
        {
            Entries = entries;
        }

        internal IReadOnlyList<KeyValuePair<PdVmValue, PdVmValue>> Entries { get; }

        internal int Index { get; set; } = -1;

        internal PdVmValue? Key { get; set; }

        internal PdVmValue? Value { get; set; }
    }

    protected PdVmProgramBase(int localCount)
        : this(localCount, string.Empty)
    {
    }

    protected PdVmProgramBase(int localCount, string callableMetadata)
    {
        if (localCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localCount));
        }

        _rootLocalCount = localCount;
        _metadata = PdVmProgramMetadataCodec.Decode(callableMetadata);
        RecreateRootState();
    }

    public IReadOnlyList<PdVmValue> Stack => _stack;

    public IReadOnlyList<PdVmValue> Locals => _locals;

    public IReadOnlyList<PdVmExecutionFrame> ExecutionFrames => _executionFrames;

    public int InstructionPointer { get; private set; }

    public long ExecutedInstructionCount => _executedInstructionCount;

    public Action<Exception>? CallbackErrorObserver { get; set; }

    public PdVmStatus RunStep(IPdVmHost host) => RunStep(host, int.MaxValue);

    public abstract PdVmStatus RunStep(IPdVmHost host, int instructionBudget);

    public PdVmScriptCallable ResolveCallable(string exportName)
    {
        var callable = ResolveExportedCallableValue(exportName);
        return CreateCallable(callable);
    }

    public PdVmScriptCallable CreateCallable(PdVmCallableValue callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        ValidateCallableGeneration(callable);
        return new PdVmScriptCallable(this, callable, _generation);
    }

    public PdVmStatus StartCallable(
        PdVmScriptCallable callable,
        IReadOnlyList<PdVmValue> args,
        IPdVmHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ValidateManagedHandle(callable);
        var prototype = GetPrototype(callable.Value.PrototypeId);
        if (prototype.TargetKind == PdVmRuntimeCallableTargetKind.HostImport)
        {
            return StartManagedHostCallable(prototype, args, host);
        }

        var stackBase = _stack.Count;
        var localBase = _locals.Count;
        StartManagedCallable(callable.Value, args);
        var status = RunStep(host);
        if (status.Kind == PdVmStatusKind.Halted)
        {
            return status;
        }

        AbortManagedInvocation(stackBase, localBase);
        throw new InvalidOperationException(
            status.Kind == PdVmStatusKind.Waiting
                ? "synchronous callable invocation entered a waiting state; use InvokeCallableAsync"
                : "synchronous callable invocation yielded; use InvokeCallableAsync");
    }

    public PdVmValue? TakeCallableResult() => TakeManagedCallableResult();

    public async ValueTask<PdVmValue> InvokeCallableAsync(
        PdVmScriptCallable callable,
        IReadOnlyList<PdVmValue> args,
        IAsyncPdVmHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        await _managedExecutionGate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeCallableCoreAsync(callable, args, host, cancellationToken);
        }
        finally
        {
            _managedExecutionGate.Release();
        }
    }

    public PdVmScriptCallback<TArgs, TResult> CreateCallback<TArgs, TResult>(
        string exportName,
        IPdVmCallbackAdapter<TArgs, TResult> adapter,
        IAsyncPdVmHost host)
    {
        var callable = ResolveCallable(exportName);
        return CreateCallback(callable, adapter, host);
    }

    public PdVmScriptCallback<TArgs, TResult> CreateCallback<TArgs, TResult>(
        PdVmScriptCallable callable,
        IPdVmCallbackAdapter<TArgs, TResult> adapter,
        IAsyncPdVmHost host)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(host);
        ValidateManagedHandle(callable);
        ValidateCallbackAdapter(
            GetPrototype(callable.Value.PrototypeId),
            adapter.ArgumentTypes,
            adapter.ResultType);
        return new PdVmScriptCallback<TArgs, TResult>(
            this,
            callable,
            adapter,
            host);
    }

    public void ResetForReuse()
    {
        InvalidateCallbackQueue(new InvalidOperationException("program generation was reset"));
        ResetRuntimeForReuse();
    }

    public void Shutdown()
    {
        InvalidateCallbackQueue(new ObjectDisposedException(GetType().FullName, "program is shut down"));
        ShutdownRuntime();
    }

    public void Dispose()
    {
        Shutdown();
        _managedExecutionGate.Dispose();
        _callbackRunnerCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    internal ValueTask<TResult> EnqueueCallbackAsync<TResult>(
        PdVmScriptCallable callable,
        IReadOnlyList<PdVmValue> args,
        Func<PdVmValue, TResult> resultAdapter,
        IAsyncPdVmHost host,
        Func<bool> isActive,
        SynchronizationContext? synchronizationContext,
        CancellationToken cancellationToken)
    {
        ValidateManagedHandle(callable);
        if (!isActive())
        {
            throw new ObjectDisposedException("script callback");
        }

        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new QueuedCallbackWork(
            callable.Generation,
            isActive,
            async runnerCancellation =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    runnerCancellation,
                    cancellationToken);
                try
                {
                    var value = await InvokeCallableAsync(callable, args, host, linked.Token);
                    completion.TrySetResult(resultAdapter(value));
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    completion.TrySetCanceled(linked.Token);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            exception => completion.TrySetException(exception),
            synchronizationContext,
            cancellationToken);

        lock (_callbackQueueLock)
        {
            if (_shutdown || !ReferenceEquals(callable.Generation, _generation))
            {
                throw new InvalidOperationException("script callback belongs to an invalid program generation");
            }

            _callbackQueue.Enqueue(work);
            if (!_callbackRunnerActive)
            {
                _callbackRunnerActive = true;
                _ = Task.Run(DrainCallbackQueueAsync);
            }
        }

        return new ValueTask<TResult>(completion.Task);
    }

    internal void ReportCallbackError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            CallbackErrorObserver?.Invoke(exception);
        }
        catch
        {
        }
    }

    public void ResumePending(ulong opId, PdVmCallReturn returnValues)
    {
        if (_pendingOpId is null)
        {
            throw new InvalidOperationException($"program is not waiting on host op {opId}");
        }

        if (_pendingOpId.Value != opId)
        {
            throw new InvalidOperationException(
                $"program is waiting on host op {_pendingOpId.Value}, not {opId}");
        }

        _pendingOpId = null;
        _pendingHost = null;
        if (_pendingNormalizeResult)
        {
            PushNormalizedReturn(returnValues);
        }
        else
        {
            PushReturn(returnValues);
        }
        _pendingNormalizeResult = false;
    }

    protected void EnsureReadyToRunStep()
    {
        if (_shutdown)
        {
            throw new ObjectDisposedException(GetType().FullName, "program is shut down");
        }

        if (_pendingOpId.HasValue)
        {
            throw new InvalidOperationException(
                $"program is waiting on host op {_pendingOpId.Value}; call ResumePending first");
        }

        if (_executionFrames.Count == 0)
        {
            throw new InvalidOperationException("program has no active execution frame");
        }
    }

    protected void SetInstructionPointer(int instructionPointer) => InstructionPointer = instructionPointer;

    protected void AddExecutedInstructions(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _executedInstructionCount += count;
    }

    protected static void ThrowInstructionBudgetExceeded(int instructionBudget) =>
        throw new InvalidOperationException($"execution exceeded {instructionBudget} steps");

    protected PdVmStatus GetLastStatus() => _lastStatus;

    protected PdVmStatus HaltProgram()
    {
        _lastStatus = PdVmStatus.Halted();
        return _lastStatus;
    }

    protected void PushValue(PdVmValue value) => _stack.Add(value);

    protected void ResetStack() => _stack.Clear();

    protected void ResetActiveOperandStack()
    {
        var frame = GetActiveFrame();
        if (_stack.Count < frame.OperandStackBase)
        {
            throw new InvalidOperationException("operand stack is below the active frame base");
        }

        _stack.RemoveRange(frame.OperandStackBase, _stack.Count - frame.OperandStackBase);
    }

    protected PdVmValue PopValue()
    {
        if (_stack.Count == 0)
        {
            throw new InvalidOperationException("stack underflow");
        }

        var index = _stack.Count - 1;
        var value = _stack[index];
        _stack.RemoveAt(index);
        return value;
    }

    protected PdVmValue LoadLocalValue(byte index)
    {
        var absolute = ResolveLocalIndex(index);
        if (_mutableBorrowAliases.TryGetValue(absolute, out var aliasCell))
        {
            _lastBorrowedCapture = null;
            return aliasCell.Value;
        }

        if (_captureCells.TryGetValue(absolute, out var cell))
        {
            _lastBorrowedCapture = IsInsideScriptCallable() && _mutableBorrowCells.Contains(cell)
                ? (cell.Value, cell)
                : null;
            return cell.Value;
        }

        _lastBorrowedCapture = null;
        return _locals[absolute];
    }

    private bool IsInsideScriptCallable() =>
        GetActiveFrameOrDefault() is { PrototypeId: not null };

    protected void StoreLocalValue(byte index, PdVmValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var absolute = ResolveLocalIndex(index);
        if (_mutableBorrowAliases.TryGetValue(absolute, out var aliasCell))
        {
            // The CLR compiler reuses the alias local as a temporary while
            // lowering Set. Only the container returned by Set can publish a
            // borrowed map/array update; staging scalars (usually null) must
            // remain local temporaries.
            if (value.Kind is not (PdVmValueKind.Map or PdVmValueKind.Array))
            {
                _locals[absolute] = value;
                _lastBorrowedCapture = null;
                return;
            }

            if (ReferencesCaptureCell(value, aliasCell, new HashSet<PdVmCaptureCell>(ReferenceEqualityComparer.Instance)))
            {
                throw new InvalidOperationException("callable capture ownership cycle is unsupported");
            }

            aliasCell.Value = value;
            _locals[absolute] = value;
            _lastBorrowedCapture = null;
            return;
        }

        if (_captureCells.TryGetValue(absolute, out var cell))
        {
            if (ReferencesCaptureCell(value, cell, new HashSet<PdVmCaptureCell>(ReferenceEqualityComparer.Instance)))
            {
                throw new InvalidOperationException("callable capture ownership cycle is unsupported");
            }

            cell.Value = value;
        }
        else if (_lastBorrowedCapture is { } borrowed &&
                 ReferenceEquals(value, borrowed.Value))
        {
            _mutableBorrowAliases[absolute] = borrowed.Cell;
            borrowed.Cell.Value = value;
        }

        _locals[absolute] = value;
        _lastBorrowedCapture = null;
    }

    protected PdVmValue[] GetLocalValues() => _locals.ToArray();

    protected bool DispatchCall(
        IPdVmHost host,
        PdVmHostImport[] imports,
        ushort callIndex,
        byte argc,
        int callIp,
        int nextIp)
    {
        var args = PopArgs(argc);
        PdVmCallOutcome outcome;
        if (PdVmBuiltins.TryGetBuiltin(callIndex, out var builtin))
        {
            if (PdVmBuiltins.GetArity(builtin) != argc)
            {
                throw new InvalidOperationException(
                    $"builtin {builtin} expects arity {PdVmBuiltins.GetArity(builtin)}, got {argc}");
            }

            outcome = DispatchBuiltin(builtin, args);
        }
        else
        {
            if (PdVmBuiltins.IsBuiltinIndex(callIndex))
            {
                throw new NotSupportedException(
                    $"builtin call index 0x{callIndex:X4} is not supported by PdVm.Runtime yet");
            }

            if (callIndex >= imports.Length)
            {
                throw new InvalidOperationException($"invalid import index {callIndex}");
            }

            var import = imports[callIndex];
            if (import.Arity != argc)
            {
                throw new InvalidOperationException(
                    $"import '{import.Name}' expects arity {import.Arity}, got {argc}");
            }

            outcome = host.Call(import.Name, args);
        }

        return ApplyCallOutcome(host, outcome, args, callIp, nextIp, restoreCallee: null, normalizeResult: false);
    }

    protected bool DispatchCallValue(
        IPdVmHost host,
        PdVmHostImport[] imports,
        byte argc,
        int callIp,
        int nextIp)
    {
        var operandCount = checked(argc + 1);
        var activeFrame = GetActiveFrame();
        if (_stack.Count - activeFrame.OperandStackBase < operandCount)
        {
            throw new InvalidOperationException("stack underflow");
        }

        var operandBase = _stack.Count - operandCount;
        var calleeValue = _stack[operandBase];
        var callable = calleeValue.AsCallable();
        ValidateCallableGeneration(callable);
        var prototype = GetPrototype(callable.PrototypeId);
        if (prototype.Arity != argc)
        {
            throw new InvalidOperationException(
                $"callable {callable.PrototypeId} expects arity {prototype.Arity}, got {argc}");
        }

        var args = _stack.Skip(operandBase + 1).Take(argc).ToArray();
        ValidateArgumentSchema(prototype, args);
        _stack.RemoveRange(operandBase, operandCount);

        if (prototype.TargetKind == PdVmRuntimeCallableTargetKind.ScriptFunction)
        {
            EnterScriptCallable(callable, prototype, args, operandBase, nextIp);
            return false;
        }

        var importIndex = checked((ushort)prototype.TargetId);
        PdVmCallOutcome outcome;
        if (PdVmBuiltins.TryGetBuiltin(importIndex, out var builtin))
        {
            if (PdVmBuiltins.GetArity(builtin) != argc)
            {
                throw new InvalidOperationException(
                    $"builtin {builtin} expects arity {PdVmBuiltins.GetArity(builtin)}, got {argc}");
            }

            outcome = DispatchBuiltin(builtin, args);
        }
        else
        {
            if (importIndex >= imports.Length)
            {
                throw new InvalidOperationException($"invalid callable import index {importIndex}");
            }

            var import = imports[importIndex];
            if (import.Arity != argc)
            {
                throw new InvalidOperationException(
                    $"import '{import.Name}' expects arity {import.Arity}, got {argc}");
            }

            outcome = host.Call(import.Name, args);
        }
        return ApplyCallOutcome(host, outcome, args, callIp, nextIp, calleeValue, normalizeResult: true);
    }

    protected bool CompleteActiveFrame()
    {
        if (_executionFrames.Count == 0)
        {
            throw new InvalidOperationException("missing active execution frame");
        }

        var frameIndex = _executionFrames.Count - 1;
        var frame = _executionFrames[frameIndex];
        _executionFrames.RemoveAt(frameIndex);
        if (_stack.Count < frame.OperandStackBase)
        {
            throw new InvalidOperationException("operand stack is below the active frame base");
        }

        if (frame.Continuation == PdVmFrameContinuationKind.Halt)
        {
            _lastStatus = PdVmStatus.Halted();
            return true;
        }

        var result = _stack.Count > frame.OperandStackBase ? PopValue() : PdVmValue.Null();
        if (_stack.Count > frame.OperandStackBase)
        {
            _stack.RemoveRange(frame.OperandStackBase, _stack.Count - frame.OperandStackBase);
        }

        if (frame.PrototypeId is uint prototypeId)
        {
            ValidateResultSchema(GetPrototype(prototypeId), result);
            var frameEnd = checked(frame.LocalBase + frame.LocalCount);
            foreach (var absolute in _captureCells.Keys
                         .Where(index => index >= frame.LocalBase && index < frameEnd)
                         .ToArray())
            {
                _captureCells.Remove(absolute);
            }
            foreach (var absolute in _mutableBorrowAliases.Keys
                         .Where(index => index >= frame.LocalBase && index < frameEnd)
                         .ToArray())
            {
                _mutableBorrowAliases.Remove(absolute);
            }

            if (frameEnd != _locals.Count)
            {
                throw new InvalidOperationException("active local frame does not end at the local stack tail");
            }

            _locals.RemoveRange(frame.LocalBase, frame.LocalCount);
        }

        switch (frame.Continuation)
        {
            case PdVmFrameContinuationKind.ResumeBytecode:
                InstructionPointer = frame.ReturnIp;
                _stack.Add(result);
                return false;
            case PdVmFrameContinuationKind.ReturnToManaged:
                _managedCallableResult = result;
                _lastStatus = PdVmStatus.Halted();
                return true;
            default:
                throw new InvalidOperationException($"unexpected frame continuation {frame.Continuation}");
        }
    }

    protected object CurrentGeneration => _generation;

    protected PdVmProgramMetadata CallableMetadata => _metadata;

    protected void ResetRuntimeForReuse()
    {
        CancelPendingHostOperation();
        _generation = new object();
        _shutdown = false;
        _stack.Clear();
        _locals.Clear();
        _captureCells.Clear();
        _mutableBorrowAliases.Clear();
        _mutableBorrowCells.Clear();
        _lastBorrowedCapture = null;
        _executionFrames.Clear();
        _managedCallableResult = null;
        _mapIterators.Clear();
        InstructionPointer = 0;
        RecreateRootState();
    }

    protected void ShutdownRuntime()
    {
        CancelPendingHostOperation();
        _generation = new object();
        _stack.Clear();
        _locals.Clear();
        _captureCells.Clear();
        _mutableBorrowAliases.Clear();
        _mutableBorrowCells.Clear();
        _lastBorrowedCapture = null;
        _executionFrames.Clear();
        _managedCallableResult = null;
        _mapIterators.Clear();
        _shutdown = true;
    }

    protected PdVmValue? TakeManagedCallableResult()
    {
        var result = _managedCallableResult;
        _managedCallableResult = null;
        return result;
    }

    protected PdVmCallableValue ResolveExportedCallableValue(string exportName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        var export = _metadata.ExportedCallables.FirstOrDefault(
            item => string.Equals(item.Name, exportName, StringComparison.Ordinal)) ??
            throw new InvalidOperationException($"unknown exported script function '{exportName}'");
        if (export.LocalSlot >= _locals.Count)
        {
            throw new InvalidOperationException($"exported local slot {export.LocalSlot} is invalid");
        }

        var callable = _locals[export.LocalSlot].AsCallable();
        ValidateCallableGeneration(callable);
        return callable;
    }

    protected void StartManagedCallable(PdVmCallableValue callable, IReadOnlyList<PdVmValue> args)
    {
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(args);
        if (_executionFrames.Count != 0)
        {
            throw new InvalidOperationException("managed callable invocation requires a halted program");
        }

        ValidateCallableGeneration(callable);
        var prototype = GetPrototype(callable.PrototypeId);
        if (prototype.TargetKind != PdVmRuntimeCallableTargetKind.ScriptFunction)
        {
            throw new NotSupportedException("managed host-function callable invocation requires an IPdVmHost dispatch path");
        }

        if (args.Count > byte.MaxValue || prototype.Arity != args.Count)
        {
            throw new InvalidOperationException(
                $"callable {callable.PrototypeId} expects arity {prototype.Arity}, got {args.Count}");
        }

        ValidateArgumentSchema(prototype, args);
        var operandBase = _stack.Count;
        EnterScriptCallable(callable, prototype, args, operandBase, returnIp: 0);
        _executionFrames[^1].Continuation = PdVmFrameContinuationKind.ReturnToManaged;
        _managedCallableResult = null;
    }

    private bool ApplyCallOutcome(
        IPdVmHost host,
        PdVmCallOutcome outcome,
        IReadOnlyList<PdVmValue> args,
        int callIp,
        int nextIp,
        PdVmValue? restoreCallee,
        bool normalizeResult)
    {
        switch (outcome.Kind)
        {
            case PdVmCallOutcomeKind.Return:
                if (normalizeResult)
                {
                    PushNormalizedReturn(outcome.ReturnValues);
                }
                else
                {
                    PushReturn(outcome.ReturnValues);
                }

                InstructionPointer = nextIp;
                return false;
            case PdVmCallOutcomeKind.Halt:
                InstructionPointer = callIp;
                _lastStatus = PdVmStatus.Halted();
                return true;
            case PdVmCallOutcomeKind.Yield:
                if (restoreCallee is not null)
                {
                    _stack.Add(restoreCallee);
                }

                _stack.AddRange(args);
                InstructionPointer = callIp;
                _lastStatus = PdVmStatus.Yielded();
                return true;
            case PdVmCallOutcomeKind.Pending:
                InstructionPointer = nextIp;
                _pendingOpId = outcome.PendingOpId;
                _pendingHost = host as IAsyncPdVmHost;
                _pendingNormalizeResult = normalizeResult;
                _lastStatus = PdVmStatus.Waiting(outcome.PendingOpId);
                return true;
            default:
                throw new InvalidOperationException($"unexpected call outcome {outcome.Kind}");
        }
    }

    private void EnterScriptCallable(
        PdVmCallableValue callable,
        PdVmRuntimeCallablePrototype prototype,
        IReadOnlyList<PdVmValue> args,
        int operandStackBase,
        int returnIp)
    {
        if (_executionFrames.Count(frame => frame.PrototypeId.HasValue) >= MaximumScriptFrameDepth)
        {
            throw new InvalidOperationException($"script call stack overflow (limit {MaximumScriptFrameDepth})");
        }

        if (prototype.TargetId >= _metadata.ScriptFunctions.Length)
        {
            throw new InvalidOperationException($"invalid script function {prototype.TargetId}");
        }

        if (prototype.ParameterSlots.Length != args.Count)
        {
            throw new InvalidOperationException("callable parameter layout does not match its arity");
        }

        // Managed callbacks start after the root frame has halted, so there is
        // no active caller frame from which to inherit callable locals. The
        // root locals remain the lexical environment for the program and may
        // contain closure values referenced by the callback body (for example,
        // an event wrapper calling another RSS function). Preserve those
        // callable slots just as we do for a nested RSS call.
        var inheritedCallables = GetActiveFrameOrDefault() is { } caller
            ? _locals.Skip(caller.LocalBase).Take(caller.LocalCount)
                .Select((value, slot) => (value, slot))
                .Where(item => item.value.Kind == PdVmValueKind.Callable)
                .ToArray()
            : _locals.Take(_rootLocalCount)
                .Select((value, slot) => (value, slot))
                .Where(item => item.value.Kind == PdVmValueKind.Callable)
                .ToArray();
        var localBase = _locals.Count;
        _locals.AddRange(Enumerable.Repeat(PdVmValue.Null(), prototype.FrameLocalCount));
        InitializeRootCallableBindings(localBase, prototype.FrameLocalCount);
        foreach (var (value, slot) in inheritedCallables)
        {
            if (slot < prototype.FrameLocalCount)
            {
                _locals[localBase + slot] = value;
            }
        }

        for (var index = 0; index < args.Count; index++)
        {
            SetFrameLocal(localBase, prototype.FrameLocalCount, prototype.ParameterSlots[index], args[index], "parameter");
        }

        if (callable.Environment is { } environment)
        {
            if (environment.Cells.Count != prototype.CaptureSlots.Length)
            {
                throw new InvalidOperationException("callable environment layout mismatch");
            }

            for (var index = 0; index < environment.Cells.Count; index++)
            {
                var slot = prototype.CaptureSlots[index];
                var absolute = ResolveFrameLocal(localBase, prototype.FrameLocalCount, slot, "capture");
                var cell = environment.Cells[index];
                _locals[absolute] = cell.Value;
                if (prototype.SelfSlot != slot)
                {
                    _captureCells[absolute] = cell;
                    if (prototype.CaptureModes[index] == PdVmRuntimeCaptureBindingMode.BorrowMut)
                    {
                        _mutableBorrowCells.Add(cell);
                    }
                }
            }
        }

        if (prototype.SelfSlot is ushort selfSlot)
        {
            SetFrameLocal(
                localBase,
                prototype.FrameLocalCount,
                selfSlot,
                PdVmValue.FromCallable(callable),
                "self");
        }

        _executionFrames.Add(new PdVmExecutionFrame(
            PdVmFrameContinuationKind.ResumeBytecode,
            returnIp,
            operandStackBase,
            localBase,
            prototype.FrameLocalCount,
            callable.PrototypeId));
        InstructionPointer = checked((int)_metadata.ScriptFunctions[prototype.TargetId].EntryIp);
    }

    private PdVmCallOutcome DispatchBuiltin(
        PdVmBuiltin builtin,
        IReadOnlyList<PdVmValue> args)
    {
        return builtin switch
        {
            PdVmBuiltin.BindCallable => PdVmCallOutcome.Returned(
                PdVmCallReturn.One(BindCallable(args))),
            PdVmBuiltin.DetachLocal => DetachLocal(args),
            PdVmBuiltin.MapIterInit => MapIteratorInit(args),
            PdVmBuiltin.MapIterNext => MapIteratorNext(args),
            PdVmBuiltin.MapIterTakeKey => MapIteratorTake(args, takeKey: true),
            PdVmBuiltin.MapIterTakeValue => MapIteratorTake(args, takeKey: false),
            PdVmBuiltin.MapIterClose => MapIteratorClose(args),
            _ => PdVmBuiltins.Dispatch(PdVmBuiltins.GetCallIndex(builtin), args),
        };
    }

    private PdVmValue BindCallable(IReadOnlyList<PdVmValue> args)
    {
        var prototypeIdValue = args[0].AsInt();
        if (prototypeIdValue < 0 || prototypeIdValue > uint.MaxValue)
        {
            throw new InvalidOperationException($"invalid callable prototype {prototypeIdValue}");
        }

        var prototypeId = (uint)prototypeIdValue;
        var prototype = GetPrototype(prototypeId);
        var captures = args[1].AsArray();
        if (captures.Count != prototype.CaptureSlots.Length ||
            captures.Count != prototype.CaptureSourceSlots.Length ||
            captures.Count != prototype.CaptureModes.Length)
        {
            throw new InvalidOperationException("callable capture layout mismatch");
        }

        var activeBase = GetActiveFrame().LocalBase;
        var cells = new PdVmCaptureCell[captures.Count];
        for (var index = 0; index < captures.Count; index++)
        {
            var source = prototype.CaptureSourceSlots[index];
            var target = prototype.CaptureSlots[index];
            var mode = prototype.CaptureModes[index];
            var selfCapture = prototype.SelfSlot == target;
            if (!selfCapture && mode is PdVmRuntimeCaptureBindingMode.Borrow or PdVmRuntimeCaptureBindingMode.BorrowMut)
            {
                var absolute = ResolveFrameLocal(
                    activeBase,
                    GetActiveFrame().LocalCount,
                    source,
                    "capture source");
                if (!_captureCells.TryGetValue(absolute, out var shared))
                {
                    shared = new PdVmCaptureCell(captures[index]);
                    _captureCells[absolute] = shared;
                }

                if (mode == PdVmRuntimeCaptureBindingMode.BorrowMut)
                {
                    _mutableBorrowCells.Add(shared);
                }

                _locals[absolute] = shared.Value;
                cells[index] = shared;
            }
            else
            {
                cells[index] = new PdVmCaptureCell(captures[index]);
            }
        }

        var environment = prototype.Kind == PdVmRuntimeCallableKind.Closure || cells.Length > 0
            ? new PdVmCallableEnvironment(cells)
            : null;
        return PdVmValue.FromCallable(new PdVmCallableValue(prototypeId, prototype.Kind, environment, _generation));
    }

    private PdVmCallOutcome DetachLocal(IReadOnlyList<PdVmValue> args)
    {
        var slot = args[0].AsInt();
        if (slot < byte.MinValue || slot > byte.MaxValue)
        {
            throw new InvalidOperationException($"invalid local {slot}");
        }

        var absolute = ResolveLocalIndex((byte)slot);
        _captureCells.Remove(absolute);
        _mutableBorrowAliases.Remove(absolute);
        _locals[absolute] = PdVmValue.Null();
        return PdVmCallOutcome.Returned(PdVmCallReturn.None);
    }

    private PdVmCallOutcome MapIteratorInit(IReadOnlyList<PdVmValue> args)
    {
        var map = args[0].AsMap();
        var slot = args[1].AsInt();
        var entries = map.ToArray();
        if (entries.Any(pair => pair.Key.Kind != PdVmValueKind.String))
        {
            throw new InvalidOperationException("borrowed map iteration requires string keys");
        }

        _mapIterators[slot] = new MapIteratorState(entries);
        return PdVmCallOutcome.Returned(PdVmCallReturn.One(args[0]));
    }

    private PdVmCallOutcome MapIteratorNext(IReadOnlyList<PdVmValue> args)
    {
        var state = GetMapIterator(args[0].AsInt());
        state.Index++;
        if (state.Index >= state.Entries.Count)
        {
            state.Key = null;
            state.Value = null;
            return PdVmCallOutcome.Returned(PdVmCallReturn.One(PdVmValue.FromBool(false)));
        }

        var entry = state.Entries[state.Index];
        state.Key = entry.Key;
        state.Value = entry.Value;
        return PdVmCallOutcome.Returned(PdVmCallReturn.One(PdVmValue.FromBool(true)));
    }

    private PdVmCallOutcome MapIteratorTake(IReadOnlyList<PdVmValue> args, bool takeKey)
    {
        var state = GetMapIterator(args[0].AsInt());
        var value = takeKey ? state.Key : state.Value;
        if (value is null)
        {
            throw new InvalidOperationException("map iterator has no current entry");
        }

        if (takeKey)
        {
            state.Key = null;
        }
        else
        {
            state.Value = null;
        }

        return PdVmCallOutcome.Returned(PdVmCallReturn.One(value));
    }

    private PdVmCallOutcome MapIteratorClose(IReadOnlyList<PdVmValue> args)
    {
        _mapIterators.Remove(args[1].AsInt());
        return PdVmCallOutcome.Returned(PdVmCallReturn.One(args[0]));
    }

    private MapIteratorState GetMapIterator(long slot) =>
        _mapIterators.TryGetValue(slot, out var state)
            ? state
            : throw new InvalidOperationException($"map iterator {slot} is not initialized");

    private void RecreateRootState()
    {
        _locals.AddRange(Enumerable.Repeat(PdVmValue.Null(), _rootLocalCount));
        _executionFrames.Add(new PdVmExecutionFrame(
            PdVmFrameContinuationKind.Halt,
            returnIp: 0,
            operandStackBase: 0,
            localBase: 0,
            localCount: _rootLocalCount,
            prototypeId: null));
        InitializeRootCallableBindings(0, _rootLocalCount);
        _lastStatus = PdVmStatus.Halted();
    }

    private void InitializeRootCallableBindings(int localBase, int localCount)
    {
        foreach (var binding in _metadata.RootCallableBindings)
        {
            var prototype = GetPrototype(binding.PrototypeId);
            var absolute = ResolveFrameLocal(localBase, localCount, binding.LocalSlot, "root callable binding");
            _locals[absolute] = PdVmValue.FromCallable(
                new PdVmCallableValue(binding.PrototypeId, prototype.Kind, environment: null, _generation));
        }
    }

    private PdVmRuntimeCallablePrototype GetPrototype(uint prototypeId)
    {
        if (prototypeId >= _metadata.CallablePrototypes.Length)
        {
            throw new InvalidOperationException($"invalid callable prototype {prototypeId}");
        }

        return _metadata.CallablePrototypes[prototypeId];
    }

    private PdVmExecutionFrame GetActiveFrame() => GetActiveFrameOrDefault() ??
        throw new InvalidOperationException("missing active execution frame");

    private PdVmExecutionFrame? GetActiveFrameOrDefault() =>
        _executionFrames.Count == 0 ? null : _executionFrames[^1];

    private int ResolveLocalIndex(byte index)
    {
        var frame = GetActiveFrame();
        return ResolveFrameLocal(frame.LocalBase, frame.LocalCount, index, "local");
    }

    private static int ResolveFrameLocal(int localBase, int localCount, ushort slot, string label)
    {
        if (slot >= localCount)
        {
            throw new InvalidOperationException($"{label} slot {slot} is outside the active frame");
        }

        return checked(localBase + slot);
    }

    private void SetFrameLocal(int localBase, int localCount, ushort slot, PdVmValue value, string label)
    {
        _locals[ResolveFrameLocal(localBase, localCount, slot, label)] = value;
    }

    private PdVmValue[] PopArgs(int argc)
    {
        var frame = GetActiveFrame();
        if (argc < 0 || argc > _stack.Count - frame.OperandStackBase)
        {
            throw new InvalidOperationException("stack underflow");
        }

        var args = new PdVmValue[argc];
        for (var index = argc - 1; index >= 0; index--)
        {
            args[index] = PopValue();
        }

        return args;
    }

    private void PushReturn(PdVmCallReturn values)
    {
        foreach (var value in values.Values)
        {
            _stack.Add(value);
        }
    }

    private void PushNormalizedReturn(PdVmCallReturn values)
    {
        if (values.Values.Count > 1)
        {
            throw new InvalidOperationException("callable host import returned more than one value");
        }

        _stack.Add(values.Values.Count == 0 ? PdVmValue.Null() : values.Values[0]);
    }

    private void ValidateCallableGeneration(PdVmCallableValue callable)
    {
        if (!ReferenceEquals(callable.Generation, _generation))
        {
            throw new InvalidOperationException("script callable belongs to an invalid program generation");
        }
    }

    private static void ValidateArgumentSchema(
        PdVmRuntimeCallablePrototype prototype,
        IReadOnlyList<PdVmValue> args)
    {
        if (prototype.Schema is not { Kind: PdVmRuntimeTypeSchemaKind.Callable } schema)
        {
            return;
        }

        if (schema.Items.Length != args.Count ||
            !schema.Items.Zip(args).All(pair => MatchesSchema(pair.Second, pair.First)))
        {
            throw new InvalidOperationException("callable argument schema mismatch");
        }
    }

    private static void ValidateResultSchema(PdVmRuntimeCallablePrototype prototype, PdVmValue result)
    {
        if (prototype.Schema is { Kind: PdVmRuntimeTypeSchemaKind.Callable, Result: { } schema } &&
            !MatchesSchema(result, schema))
        {
            throw new InvalidOperationException("callable return schema mismatch");
        }
    }

    private static void ValidateCallbackAdapter(
        PdVmRuntimeCallablePrototype prototype,
        IReadOnlyList<PdVmValueType>? argumentTypes,
        PdVmValueType resultType)
    {
        if (argumentTypes is not null && argumentTypes.Count != prototype.Arity)
        {
            throw new InvalidOperationException(
                $"callback adapter declares {argumentTypes.Count} argument type(s), " +
                $"callable expects arity {prototype.Arity}");
        }

        if (prototype.Schema is not { Kind: PdVmRuntimeTypeSchemaKind.Callable } schema)
        {
            return;
        }

        if (argumentTypes is not null)
        {
            for (var index = 0; index < argumentTypes.Count; index++)
            {
                var declaredType = argumentTypes[index];
                if (!IsDeclaredTypeCompatible(declaredType, schema.Items[index]))
                {
                    throw new InvalidOperationException(
                        $"callback adapter argument {index} type {declaredType} does not match " +
                        $"callable schema {schema.Items[index].Kind}");
                }
            }
        }

        if (resultType != PdVmValueType.Unknown &&
            schema.Result is { } resultSchema &&
            !IsDeclaredTypeCompatible(resultType, resultSchema))
        {
            throw new InvalidOperationException(
                $"callback adapter result type {resultType} does not match " +
                $"callable result schema {resultSchema.Kind}");
        }
    }

    private static bool IsDeclaredTypeCompatible(
        PdVmValueType declaredType,
        PdVmRuntimeTypeSchema schema)
    {
        if (declaredType == PdVmValueType.Unknown ||
            schema.Kind is PdVmRuntimeTypeSchemaKind.Unknown or
                PdVmRuntimeTypeSchemaKind.GenericParameter or
                PdVmRuntimeTypeSchemaKind.Named)
        {
            return true;
        }

        return schema.Kind switch
        {
            PdVmRuntimeTypeSchemaKind.Null => declaredType == PdVmValueType.Null,
            PdVmRuntimeTypeSchemaKind.Int => declaredType == PdVmValueType.Int,
            PdVmRuntimeTypeSchemaKind.Float => declaredType == PdVmValueType.Float,
            PdVmRuntimeTypeSchemaKind.Number => declaredType is PdVmValueType.Int or PdVmValueType.Float,
            PdVmRuntimeTypeSchemaKind.Bool => declaredType == PdVmValueType.Bool,
            PdVmRuntimeTypeSchemaKind.String => declaredType == PdVmValueType.String,
            PdVmRuntimeTypeSchemaKind.Bytes => declaredType == PdVmValueType.Bytes,
            PdVmRuntimeTypeSchemaKind.Array or
                PdVmRuntimeTypeSchemaKind.ArrayTuple or
                PdVmRuntimeTypeSchemaKind.ArrayTupleRest => declaredType == PdVmValueType.Array,
            PdVmRuntimeTypeSchemaKind.Map or
                PdVmRuntimeTypeSchemaKind.Object => declaredType == PdVmValueType.Map,
            PdVmRuntimeTypeSchemaKind.Callable => declaredType == PdVmValueType.Callable,
            PdVmRuntimeTypeSchemaKind.Optional => declaredType == PdVmValueType.Null ||
                (schema.Element is not null && IsDeclaredTypeCompatible(declaredType, schema.Element)),
            _ => false,
        };
    }

    internal static bool MatchesSchema(PdVmValue value, PdVmRuntimeTypeSchema schema)
    {
        return schema.Kind switch
        {
            PdVmRuntimeTypeSchemaKind.Unknown or PdVmRuntimeTypeSchemaKind.GenericParameter => true,
            PdVmRuntimeTypeSchemaKind.Null => value.Kind == PdVmValueKind.Null,
            PdVmRuntimeTypeSchemaKind.Int => value.Kind == PdVmValueKind.Int,
            PdVmRuntimeTypeSchemaKind.Float => value.Kind == PdVmValueKind.Float,
            PdVmRuntimeTypeSchemaKind.Number => value.Kind is PdVmValueKind.Int or PdVmValueKind.Float,
            PdVmRuntimeTypeSchemaKind.Bool => value.Kind == PdVmValueKind.Bool,
            PdVmRuntimeTypeSchemaKind.String => value.Kind == PdVmValueKind.String,
            PdVmRuntimeTypeSchemaKind.Bytes => value.Kind == PdVmValueKind.Bytes,
            PdVmRuntimeTypeSchemaKind.Array => value.Kind == PdVmValueKind.Array &&
                (schema.Element is null || value.AsArray().All(item => MatchesSchema(item, schema.Element))),
            PdVmRuntimeTypeSchemaKind.ArrayTuple => value.Kind == PdVmValueKind.Array &&
                value.AsArray().Count == schema.Items.Length &&
                schema.Items.Zip(value.AsArray()).All(pair => MatchesSchema(pair.Second, pair.First)),
            PdVmRuntimeTypeSchemaKind.ArrayTupleRest => value.Kind == PdVmValueKind.Array,
            PdVmRuntimeTypeSchemaKind.Map or PdVmRuntimeTypeSchemaKind.Object => value.Kind == PdVmValueKind.Map,
            PdVmRuntimeTypeSchemaKind.Callable => value.Kind == PdVmValueKind.Callable,
            PdVmRuntimeTypeSchemaKind.Optional => value.Kind == PdVmValueKind.Null ||
                (schema.Element is not null && MatchesSchema(value, schema.Element)),
            PdVmRuntimeTypeSchemaKind.Named => true,
            _ => false,
        };
    }

    private static bool ReferencesCaptureCell(
        PdVmValue value,
        PdVmCaptureCell target,
        HashSet<PdVmCaptureCell> visited)
    {
        switch (value.Kind)
        {
            case PdVmValueKind.Callable:
                var environment = value.AsCallable().Environment;
                if (environment is null)
                {
                    return false;
                }

                foreach (var cell in environment.Cells)
                {
                    if (ReferenceEquals(cell, target))
                    {
                        return true;
                    }

                    if (visited.Add(cell) && ReferencesCaptureCell(cell.Value, target, visited))
                    {
                        return true;
                    }
                }

                return false;
            case PdVmValueKind.Array:
                return value.AsArray().Any(item => ReferencesCaptureCell(item, target, visited));
            case PdVmValueKind.Map:
                return value.AsMap().Any(pair =>
                    ReferencesCaptureCell(pair.Key, target, visited) ||
                    ReferencesCaptureCell(pair.Value, target, visited));
            default:
                return false;
        }
    }

    private PdVmStatus StartManagedHostCallable(
        PdVmRuntimeCallablePrototype prototype,
        IReadOnlyList<PdVmValue> args,
        IPdVmHost host)
    {
        ValidateManagedHostCallable(prototype, args);
        var import = GetManagedHostImport(prototype);
        var outcome = host.Call(import.Name, args);
        switch (outcome.Kind)
        {
            case PdVmCallOutcomeKind.Return:
                var result = NormalizeCallReturn(outcome.ReturnValues);
                ValidateResultSchema(prototype, result);
                _managedCallableResult = result;
                _lastStatus = PdVmStatus.Halted();
                return _lastStatus;
            case PdVmCallOutcomeKind.Pending:
                (host as IAsyncPdVmHost)?.CancelPending(outcome.PendingOpId);
                throw new InvalidOperationException(
                    "synchronous callable invocation entered a waiting state; use InvokeCallableAsync");
            case PdVmCallOutcomeKind.Yield:
                throw new InvalidOperationException(
                    "synchronous callable invocation yielded; use InvokeCallableAsync");
            case PdVmCallOutcomeKind.Halt:
                throw new InvalidOperationException("host callable halted without returning a value");
            default:
                throw new InvalidOperationException($"unexpected call outcome {outcome.Kind}");
        }
    }

    private async ValueTask<PdVmValue> InvokeManagedHostCallableAsync(
        PdVmRuntimeCallablePrototype prototype,
        IReadOnlyList<PdVmValue> args,
        IAsyncPdVmHost host,
        CancellationToken cancellationToken)
    {
        ValidateManagedHostCallable(prototype, args);
        var import = GetManagedHostImport(prototype);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = host.Call(import.Name, args);
            switch (outcome.Kind)
            {
                case PdVmCallOutcomeKind.Return:
                    var result = NormalizeCallReturn(outcome.ReturnValues);
                    ValidateResultSchema(prototype, result);
                    return result;
                case PdVmCallOutcomeKind.Yield:
                    await Task.Yield();
                    continue;
                case PdVmCallOutcomeKind.Pending:
                    try
                    {
                        var values = await host.WaitAsync(outcome.PendingOpId, cancellationToken);
                        var pendingResult = NormalizeCallReturn(values);
                        ValidateResultSchema(prototype, pendingResult);
                        return pendingResult;
                    }
                    catch
                    {
                        host.CancelPending(outcome.PendingOpId);
                        throw;
                    }
                case PdVmCallOutcomeKind.Halt:
                    throw new InvalidOperationException("host callable halted without returning a value");
                default:
                    throw new InvalidOperationException($"unexpected call outcome {outcome.Kind}");
            }
        }
    }

    private void ValidateManagedHostCallable(
        PdVmRuntimeCallablePrototype prototype,
        IReadOnlyList<PdVmValue> args)
    {
        if (_executionFrames.Count != 0)
        {
            throw new InvalidOperationException("managed callable invocation requires a halted program");
        }

        if (args.Count > byte.MaxValue || prototype.Arity != args.Count)
        {
            throw new InvalidOperationException(
                $"callable expects arity {prototype.Arity}, got {args.Count}");
        }

        ValidateArgumentSchema(prototype, args);
    }

    private PdVmRuntimeHostImport GetManagedHostImport(PdVmRuntimeCallablePrototype prototype)
    {
        if (prototype.TargetId >= _metadata.Imports.Length)
        {
            throw new InvalidOperationException($"invalid callable import index {prototype.TargetId}");
        }

        return _metadata.Imports[prototype.TargetId];
    }

    private static PdVmValue NormalizeCallReturn(PdVmCallReturn values)
    {
        if (values.Values.Count > 1)
        {
            throw new InvalidOperationException("callable host import returned more than one value");
        }

        return values.Values.Count == 0 ? PdVmValue.Null() : values.Values[0];
    }

    private async ValueTask<PdVmValue> InvokeCallableCoreAsync(
        PdVmScriptCallable callable,
        IReadOnlyList<PdVmValue> args,
        IAsyncPdVmHost host,
        CancellationToken cancellationToken)
    {
        ValidateManagedHandle(callable);
        var prototype = GetPrototype(callable.Value.PrototypeId);
        if (prototype.TargetKind == PdVmRuntimeCallableTargetKind.HostImport)
        {
            return await InvokeManagedHostCallableAsync(prototype, args, host, cancellationToken);
        }

        var stackBase = _stack.Count;
        var localBase = _locals.Count;
        try
        {
            StartManagedCallable(callable.Value, args);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = RunStep(host);
                switch (status.Kind)
                {
                    case PdVmStatusKind.Halted:
                        return TakeManagedCallableResult() ??
                               throw new InvalidOperationException(
                                   "managed callable invocation completed without a result");
                    case PdVmStatusKind.Yielded:
                        continue;
                    case PdVmStatusKind.Waiting:
                        PdVmCallReturn values;
                        try
                        {
                            values = await host.WaitAsync(status.WaitingOpId, cancellationToken);
                        }
                        catch
                        {
                            host.CancelPending(status.WaitingOpId);
                            throw;
                        }

                        ResumePending(status.WaitingOpId, values);
                        continue;
                    default:
                        throw new InvalidOperationException($"unexpected status {status.Kind}");
                }
            }
        }
        catch
        {
            AbortManagedInvocation(stackBase, localBase);
            throw;
        }
    }

    private async Task DrainCallbackQueueAsync()
    {
        while (true)
        {
            QueuedCallbackWork? work;
            CancellationToken runnerCancellation;
            lock (_callbackQueueLock)
            {
                if (_callbackQueue.Count == 0)
                {
                    _callbackRunnerActive = false;
                    return;
                }

                work = _callbackQueue.Dequeue();
                runnerCancellation = _callbackRunnerCancellation.Token;
            }

            if (!ReferenceEquals(work.Generation, _generation))
            {
                work.Reject(new InvalidOperationException(
                    "script callback belongs to an invalid program generation"));
                continue;
            }

            if (!work.IsActive())
            {
                work.Reject(new ObjectDisposedException("script callback"));
                continue;
            }

            if (work.CancellationToken.IsCancellationRequested)
            {
                work.Reject(new OperationCanceledException(work.CancellationToken));
                continue;
            }

            try
            {
                if (work.SynchronizationContext is null)
                {
                    await work.Execute(runnerCancellation);
                }
                else
                {
                    await ExecuteCallbackOnContextAsync(
                        work.SynchronizationContext,
                        work.Execute,
                        runnerCancellation);
                }
            }
            catch (Exception exception)
            {
                work.Reject(exception);
            }
        }
    }

    private static Task ExecuteCallbackOnContextAsync(
        SynchronizationContext synchronizationContext,
        Func<CancellationToken, ValueTask> execute,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synchronizationContext.Post(
            async _ =>
            {
                try
                {
                    await execute(cancellationToken);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            null);
        return completion.Task;
    }

    private void InvalidateCallbackQueue(Exception exception)
    {
        QueuedCallbackWork[] rejected;
        CancellationTokenSource previousCancellation;
        lock (_callbackQueueLock)
        {
            previousCancellation = _callbackRunnerCancellation;
            _callbackRunnerCancellation = new CancellationTokenSource();
            rejected = _callbackQueue.ToArray();
            _callbackQueue.Clear();
        }

        previousCancellation.Cancel();
        previousCancellation.Dispose();
        foreach (var work in rejected)
        {
            work.Reject(exception);
        }
    }

    private void ValidateManagedHandle(PdVmScriptCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        if (!ReferenceEquals(callable.Owner, this) ||
            !ReferenceEquals(callable.Generation, _generation))
        {
            throw new InvalidOperationException("script callable belongs to another or invalid program generation");
        }

        ValidateCallableGeneration(callable.Value);
    }

    private void AbortManagedInvocation(int stackBase, int localBase)
    {
        CancelPendingHostOperation();
        if (_stack.Count > stackBase)
        {
            _stack.RemoveRange(stackBase, _stack.Count - stackBase);
        }

        foreach (var absolute in _captureCells.Keys.Where(index => index >= localBase).ToArray())
        {
            _captureCells.Remove(absolute);
        }
        foreach (var absolute in _mutableBorrowAliases.Keys.Where(index => index >= localBase).ToArray())
        {
            _mutableBorrowAliases.Remove(absolute);
        }
        _lastBorrowedCapture = null;

        if (_locals.Count > localBase)
        {
            _locals.RemoveRange(localBase, _locals.Count - localBase);
        }

        _executionFrames.Clear();
        _managedCallableResult = null;
        _lastStatus = PdVmStatus.Halted();
    }

    private void CancelPendingHostOperation()
    {
        if (_pendingOpId is ulong opId)
        {
            _pendingHost?.CancelPending(opId);
        }

        _pendingOpId = null;
        _pendingHost = null;
        _pendingNormalizeResult = false;
    }
}
