namespace PdVm.Runtime;

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

public sealed class PdVmScriptCallable
{
    internal PdVmScriptCallable(PdVmProgramBase owner, PdVmCallableValue value, object generation)
    {
        Owner = owner;
        Value = value;
        Generation = generation;
    }

    internal PdVmProgramBase Owner { get; }

    internal PdVmCallableValue Value { get; }

    internal object Generation { get; }

    public override string ToString() => "RSS callable";
}

public readonly record struct PdVmUnit
{
    public static PdVmUnit Value => default;
}

public interface IPdVmCallbackAdapter<in TArgs, out TResult>
{
    IReadOnlyList<PdVmValueType>? ArgumentTypes => null;

    PdVmValueType ResultType => PdVmValueType.Unknown;

    IReadOnlyList<PdVmValue> ToArguments(TArgs args);

    TResult FromResult(PdVmValue result);
}

public sealed class PdVmDelegateCallbackAdapter<TArgs, TResult> : IPdVmCallbackAdapter<TArgs, TResult>
{
    private readonly Func<TArgs, IReadOnlyList<PdVmValue>> _arguments;
    private readonly Func<PdVmValue, TResult> _result;
    private readonly IReadOnlyList<PdVmValueType>? _argumentTypes;
    private readonly PdVmValueType _resultType;

    public PdVmDelegateCallbackAdapter(
        Func<TArgs, IReadOnlyList<PdVmValue>> arguments,
        Func<PdVmValue, TResult> result,
        IReadOnlyList<PdVmValueType>? argumentTypes = null,
        PdVmValueType? resultType = null)
    {
        _arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _argumentTypes = argumentTypes;
        _resultType = resultType ?? PdVmCallbackAdapters.InferManagedType(typeof(TResult));
    }

    public IReadOnlyList<PdVmValueType>? ArgumentTypes => _argumentTypes;

    public PdVmValueType ResultType => _resultType;

    public IReadOnlyList<PdVmValue> ToArguments(TArgs args) => _arguments(args);

    public TResult FromResult(PdVmValue result) => _result(result);
}

public static class PdVmCallbackAdapters
{
    public static IPdVmCallbackAdapter<PdVmUnit, PdVmUnit> Unit { get; } =
        new PdVmDelegateCallbackAdapter<PdVmUnit, PdVmUnit>(
            _ => Array.Empty<PdVmValue>(),
            _ => PdVmUnit.Value,
            Array.Empty<PdVmValueType>(),
            PdVmValueType.Null);

    public static IPdVmCallbackAdapter<PdVmValue, PdVmValue> Value { get; } =
        new PdVmDelegateCallbackAdapter<PdVmValue, PdVmValue>(
            value => new[] { value },
            value => value,
            [PdVmValueType.Unknown],
            PdVmValueType.Unknown);

    public static IPdVmCallbackAdapter<TArgs, TResult> Create<TArgs, TResult>(
        Func<TArgs, IReadOnlyList<PdVmValue>> arguments,
        Func<PdVmValue, TResult> result,
        IReadOnlyList<PdVmValueType>? argumentTypes = null,
        PdVmValueType? resultType = null) =>
        new PdVmDelegateCallbackAdapter<TArgs, TResult>(arguments, result, argumentTypes, resultType);

    public static IPdVmCallbackAdapter<TArgs, TResult> Map<TArgs, TResult>(
        Func<TArgs, IEnumerable<KeyValuePair<PdVmValue, PdVmValue>>> payload,
        Func<PdVmValue, TResult> result) =>
        new PdVmDelegateCallbackAdapter<TArgs, TResult>(
            args => new[] { PdVmValue.FromMap(payload(args)) },
            result,
            [PdVmValueType.Map],
            InferManagedType(typeof(TResult)));

    internal static PdVmValueType InferManagedType(Type type)
    {
        if (type == typeof(PdVmUnit)) return PdVmValueType.Null;
        if (type == typeof(bool)) return PdVmValueType.Bool;
        if (type == typeof(string) || type == typeof(char)) return PdVmValueType.String;
        if (type == typeof(byte[])) return PdVmValueType.Bytes;
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return PdVmValueType.Float;
        if (type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong))
        {
            return PdVmValueType.Int;
        }

        return PdVmValueType.Unknown;
    }
}

public sealed class PdVmScriptCallback<TArgs, TResult> : IDisposable
{
    private readonly PdVmProgramBase _program;
    private readonly PdVmScriptCallable _callable;
    private readonly IPdVmCallbackAdapter<TArgs, TResult> _adapter;
    private readonly IAsyncPdVmHost _host;
    private readonly object _subscriptionLock = new();
    private readonly List<Action> _unsubscribeActions = new();
    private SynchronizationContext? _synchronizationContext;
    private int _active = 1;

    internal PdVmScriptCallback(
        PdVmProgramBase program,
        PdVmScriptCallable callable,
        IPdVmCallbackAdapter<TArgs, TResult> adapter,
        IAsyncPdVmHost host)
    {
        _program = program;
        _callable = callable;
        _adapter = adapter;
        _host = host;
    }

    public ValueTask<TResult> InvokeAsync(TArgs args, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return _program.EnqueueCallbackAsync(
            _callable,
            _adapter.ToArguments(args),
            _adapter.FromResult,
            _host,
            IsActive,
            _synchronizationContext,
            cancellationToken);
    }

    public PdVmScriptCallback<TArgs, TResult> ScheduleOn(
        SynchronizationContext synchronizationContext)
    {
        ArgumentNullException.ThrowIfNull(synchronizationContext);
        _synchronizationContext = synchronizationContext;
        return this;
    }

    public void Post(TArgs args)
    {
        EnsureActive();
        _ = ObservePostAsync(InvokeAsync(args));
    }

    public EventHandler AsEventHandler(Func<object?, EventArgs, TArgs>? eventAdapter = null)
    {
        EnsureActive();
        return (sender, eventArgs) =>
        {
            if (!IsActive())
            {
                return;
            }

            try
            {
                Post(eventAdapter is null
                    ? ConvertEventArgs(eventArgs)
                    : eventAdapter(sender, eventArgs));
            }
            catch (Exception exception)
            {
                _program.ReportCallbackError(exception);
            }
        };
    }

    public EventHandler Subscribe(
        Action<EventHandler> subscribe,
        Action<EventHandler> unsubscribe,
        Func<object?, EventArgs, TArgs>? eventAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        var handler = AsEventHandler(eventAdapter);
        subscribe(handler);
        lock (_subscriptionLock)
        {
            if (!IsActive())
            {
                unsubscribe(handler);
                throw new ObjectDisposedException(GetType().FullName);
            }

            _unsubscribeActions.Add(() => unsubscribe(handler));
        }

        return handler;
    }

    public void Unsubscribe(Action<EventHandler> unsubscribe, EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(unsubscribe);
        ArgumentNullException.ThrowIfNull(handler);
        unsubscribe(handler);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        Action[] actions;
        lock (_subscriptionLock)
        {
            actions = _unsubscribeActions.ToArray();
            _unsubscribeActions.Clear();
        }

        foreach (var action in actions)
        {
            action();
        }
    }

    private bool IsActive() => Volatile.Read(ref _active) != 0;

    private void EnsureActive()
    {
        if (!IsActive())
        {
            throw new ObjectDisposedException(GetType().FullName, "script callback is unsubscribed");
        }
    }

    private static TArgs ConvertEventArgs(EventArgs eventArgs)
    {
        if (typeof(TArgs) == typeof(PdVmUnit))
        {
            return (TArgs)(object)PdVmUnit.Value;
        }

        if (eventArgs is TArgs converted)
        {
            return converted;
        }

        throw new InvalidOperationException(
            $"event arguments of type {eventArgs.GetType().FullName} require a callback event adapter");
    }

    private async Task ObservePostAsync(ValueTask<TResult> invocation)
    {
        try
        {
            _ = await invocation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _program.ReportCallbackError(exception);
        }
    }
}
