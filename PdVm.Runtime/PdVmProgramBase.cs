namespace PdVm.Runtime;

public abstract class PdVmProgramBase : IPdVmProgram
{
    private readonly List<PdVmValue> _stack = new();
    private readonly PdVmValue[] _locals;
    private PdVmStatus _lastStatus = PdVmStatus.Halted();
    private ulong? _pendingOpId;
    private long _executedInstructionCount;

    protected PdVmProgramBase(int localCount)
    {
        if (localCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localCount));
        }

        _locals = Enumerable.Range(0, localCount).Select(_ => PdVmValue.Null()).ToArray();
    }

    public IReadOnlyList<PdVmValue> Stack => _stack;

    public IReadOnlyList<PdVmValue> Locals => _locals;

    public int InstructionPointer { get; private set; }

    public long ExecutedInstructionCount => _executedInstructionCount;

    public PdVmStatus RunStep(IPdVmHost host) => RunStep(host, int.MaxValue);

    public abstract PdVmStatus RunStep(IPdVmHost host, int instructionBudget);

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
        PushReturn(returnValues);
    }

    protected void EnsureReadyToRunStep()
    {
        if (_pendingOpId.HasValue)
        {
            throw new InvalidOperationException(
                $"program is waiting on host op {_pendingOpId.Value}; call ResumePending first");
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

    protected void SetLocalValue(byte index, PdVmValue value)
    {
        if (index >= _locals.Length)
        {
            throw new InvalidOperationException($"invalid local {index}");
        }

        _locals[index] = value;
    }

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

            outcome = PdVmBuiltins.Dispatch(callIndex, args);
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

        switch (outcome.Kind)
        {
            case PdVmCallOutcomeKind.Return:
                PushReturn(outcome.ReturnValues);
                InstructionPointer = nextIp;
                return false;
            case PdVmCallOutcomeKind.Halt:
                InstructionPointer = callIp;
                _lastStatus = PdVmStatus.Halted();
                return true;
            case PdVmCallOutcomeKind.Yield:
                foreach (var arg in args)
                {
                    _stack.Add(arg);
                }
                InstructionPointer = callIp;
                _lastStatus = PdVmStatus.Yielded();
                return true;
            case PdVmCallOutcomeKind.Pending:
                InstructionPointer = nextIp;
                _pendingOpId = outcome.PendingOpId;
                _lastStatus = PdVmStatus.Waiting(outcome.PendingOpId);
                return true;
            default:
                throw new InvalidOperationException($"unexpected call outcome {outcome.Kind}");
        }
    }

    private PdVmValue[] PopArgs(int argc)
    {
        if (argc < 0 || argc > _stack.Count)
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
}
