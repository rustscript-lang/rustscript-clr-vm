using System.Collections.Concurrent;

namespace PdVm.Runtime;

public sealed class PdVmDelegateHost : IAsyncPdVmHost
{
    private readonly Dictionary<string, Func<IReadOnlyList<PdVmValue>, PdVmCallOutcome>> _syncHandlers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IReadOnlyList<PdVmValue>, CancellationToken, ValueTask<PdVmCallReturn>>> _asyncHandlers =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, PendingOperation> _pendingOperations = new();
    private Func<string, IReadOnlyList<PdVmValue>, PdVmCallOutcome>? _fallback;
    private long _nextOpId;

    private sealed record PendingOperation(
        Task<PdVmCallReturn> Task,
        CancellationTokenSource Cancellation);

    public void Register(string name, Func<IReadOnlyList<PdVmValue>, PdVmCallOutcome> handler)
    {
        _syncHandlers[name] = handler;
    }

    public void RegisterReturn(string name, Func<IReadOnlyList<PdVmValue>, PdVmCallReturn> handler)
    {
        Register(name, args => PdVmCallOutcome.Returned(handler(args)));
    }

    public void RegisterValue(string name, Func<IReadOnlyList<PdVmValue>, PdVmValue> handler)
    {
        RegisterReturn(name, args => PdVmCallReturn.One(handler(args)));
    }

    public void RegisterAsync(
        string name,
        Func<IReadOnlyList<PdVmValue>, CancellationToken, ValueTask<PdVmCallReturn>> handler)
    {
        _asyncHandlers[name] = handler;
    }

    public void RegisterAsyncValue(
        string name,
        Func<IReadOnlyList<PdVmValue>, CancellationToken, ValueTask<PdVmValue>> handler)
    {
        RegisterAsync(
            name,
            async (args, cancellationToken) => PdVmCallReturn.One(await handler(args, cancellationToken)));
    }

    public void RegisterFallback(Func<string, IReadOnlyList<PdVmValue>, PdVmCallOutcome> handler)
    {
        _fallback = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public PdVmCallOutcome Call(string name, IReadOnlyList<PdVmValue> args)
    {
        if (_syncHandlers.TryGetValue(name, out var syncHandler))
        {
            return syncHandler(args);
        }

        if (_asyncHandlers.TryGetValue(name, out var asyncHandler))
        {
            var opId = (ulong)Interlocked.Increment(ref _nextOpId);
            var cancellation = new CancellationTokenSource();
            var operation = new PendingOperation(
                asyncHandler(args, cancellation.Token).AsTask(),
                cancellation);
            if (!_pendingOperations.TryAdd(opId, operation))
            {
                cancellation.Cancel();
                cancellation.Dispose();
                throw new InvalidOperationException($"duplicate pending host operation {opId}");
            }

            return PdVmCallOutcome.Pending(opId);
        }

        if (_fallback is not null)
        {
            return _fallback(name, args);
        }

        throw new InvalidOperationException($"unbound host import '{name}'");
    }

    public async ValueTask<PdVmCallReturn> WaitAsync(ulong opId, CancellationToken cancellationToken = default)
    {
        if (!_pendingOperations.TryGetValue(opId, out var operation))
        {
            throw new InvalidOperationException($"unknown pending host operation {opId}");
        }

        try
        {
            return await operation.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (_pendingOperations.TryRemove(opId, out var removed))
            {
                removed.Cancellation.Dispose();
            }
        }
    }

    public void CancelPending(ulong opId)
    {
        if (_pendingOperations.TryRemove(opId, out var operation))
        {
            operation.Cancellation.Cancel();
            operation.Cancellation.Dispose();
        }
    }
}
