using System.Reflection;

namespace PdVm.Runtime;

public static class PdVmWinFormsApplication
{
    private static readonly AsyncLocal<PdVmWinFormsApplicationSession?> CurrentSession = new();

    public static PdVmWinFormsApplicationSession Attach(
        IPdVmCallableProgram program,
        IAsyncPdVmHost host)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(host);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Forms is only available on Windows");
        }
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Windows Forms programs must be attached on an STA thread");
        }
        if (CurrentSession.Value is not null)
        {
            throw new InvalidOperationException("a Windows Forms program is already attached to this context");
        }

        var session = new PdVmWinFormsApplicationSession(program, host, ClearCurrentSession);
        CurrentSession.Value = session;
        return session;
    }

    internal static PdVmWinFormsApplicationSession RequireCurrent() =>
        CurrentSession.Value ?? throw new InvalidOperationException(
            "Windows Forms callable binding requires PdVmWinFormsApplication.Attach");

    private static void ClearCurrentSession(PdVmWinFormsApplicationSession session)
    {
        if (ReferenceEquals(CurrentSession.Value, session))
        {
            CurrentSession.Value = null;
        }
    }
}

public sealed class PdVmWinFormsApplicationSession : IDisposable
{
    private readonly IPdVmCallableProgram _program;
    private readonly IAsyncPdVmHost _host;
    private readonly Action<PdVmWinFormsApplicationSession> _onDispose;
    private readonly List<IDisposable> _resources = new();
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private object? _mainForm;
    private int _disposed;

    internal PdVmWinFormsApplicationSession(
        IPdVmCallableProgram program,
        IAsyncPdVmHost host,
        Action<PdVmWinFormsApplicationSession> onDispose)
    {
        _program = program;
        _host = host;
        _onDispose = onDispose;
    }

    public bool HasMainForm => _mainForm is not null;

    public void RunMessageLoop()
    {
        EnsureUiThread();
        var form = _mainForm ?? throw new InvalidOperationException(
            "the RSS program did not register a main form with EventLoop::Show");
        var formType = Type.GetType(
            "System.Windows.Forms.Form, System.Windows.Forms",
            throwOnError: true)!;
        if (!formType.IsInstanceOfType(form))
        {
            throw new InvalidOperationException("EventLoop::Show requires a Windows Forms Form");
        }

        var application = Type.GetType(
            "System.Windows.Forms.Application, System.Windows.Forms",
            throwOnError: true)!;
        var run = application.GetMethod(
            "Run",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [formType],
            modifiers: null) ?? throw new InvalidOperationException(
            "System.Windows.Forms.Application does not expose Run(Form)");
        _ = run.Invoke(null, [form]);
    }

    internal void RegisterMainForm(object form)
    {
        ArgumentNullException.ThrowIfNull(form);
        EnsureUiThread();
        if (_mainForm is not null && !ReferenceEquals(_mainForm, form))
        {
            throw new InvalidOperationException("a Windows Forms program can register only one main form");
        }
        _ = form.GetType().GetProperty(
            "Handle",
            BindingFlags.Public | BindingFlags.Instance)?.GetValue(form) ??
            throw new InvalidOperationException(
                $"{form.GetType().FullName} does not expose a window handle");
        _mainForm = form;
    }

    internal PdVmScriptCallback<PdVmUnit, PdVmUnit> CreateUnitCallback(
        object form,
        PdVmCallableValue callable) =>
        CreateCallback(form, callable, PdVmCallbackAdapters.Unit);

    internal PdVmScriptCallback<TArgs, TResult> CreateCallback<TArgs, TResult>(
        object form,
        PdVmCallableValue callable,
        IPdVmCallbackAdapter<TArgs, TResult> adapter)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(adapter);
        EnsureUiThread();
        var handle = _program.CreateCallable(callable);
        var callback = _program
            .CreateCallback(handle, adapter, _host)
            .ScheduleOn(new ControlSynchronizationContext(form, _uiThreadId));
        Track(callback);
        return callback;
    }

    internal void Track(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureUiThread();
        _resources.Add(resource);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            _resources[index].Dispose();
        }
        _resources.Clear();
        _onDispose(this);
    }

    private void EnsureUiThread()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException("Windows Forms application state belongs to its STA thread");
        }
    }

    private sealed class ControlSynchronizationContext(object control, int uiThreadId)
        : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            Action work = () => callback(state);
            var beginInvoke = control.GetType().GetMethod(
                "BeginInvoke",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [typeof(Delegate)],
                modifiers: null) ?? throw new InvalidOperationException(
                $"{control.GetType().FullName} does not expose BeginInvoke(Delegate)");
            _ = beginInvoke.Invoke(control, [work]);
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (Environment.CurrentManagedThreadId == uiThreadId)
            {
                callback(state);
                return;
            }

            Exception? failure = null;
            using var completed = new ManualResetEventSlim();
            Post(
                value =>
                {
                    try
                    {
                        callback(value);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        completed.Set();
                    }
                },
                state);
            completed.Wait();
            if (failure is not null)
            {
                throw new InvalidOperationException("Windows Forms callback dispatch failed", failure);
            }
        }
    }
}
