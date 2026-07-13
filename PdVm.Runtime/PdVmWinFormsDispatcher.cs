using System.Reflection;
using System.Runtime.InteropServices;

namespace PdVm.Runtime;

internal static class PdVmWinFormsDispatcher
{
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Ready = new(false);
    private static Thread? _thread;
    private static object? _invoker;
    private static Exception? _startupError;
    private static int _threadId;

    public static T Invoke<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureStarted();
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId))
        {
            return callback();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action work = () =>
        {
            try
            {
                completion.SetResult(callback());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        };

        var invoker = _invoker ?? throw new InvalidOperationException("WinForms dispatcher did not initialize");
        var beginInvoke = invoker.GetType().GetMethod(
            "BeginInvoke",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(Delegate)],
            modifiers: null) ?? throw new InvalidOperationException(
                $"{invoker.GetType().FullName} does not expose BeginInvoke(Delegate)");
        _ = beginInvoke.Invoke(invoker, [work]);
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void EnsureStarted()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WinForms dispatch is only available on Windows");
        }

        if (Volatile.Read(ref _thread) is null)
        {
            lock (Gate)
            {
                if (_thread is null)
                {
                    _thread = new Thread(RunMessageLoop)
                    {
                        IsBackground = true,
                        Name = "PdVm WinForms UI",
                    };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start();
                }
            }
        }

        Ready.Wait();
        if (_startupError is not null)
        {
            throw new InvalidOperationException("WinForms dispatcher failed to initialize", _startupError);
        }
    }

    private static void RunMessageLoop()
    {
        try
        {
            _ = SetThreadDpiAwarenessContext(new IntPtr(-4));
            _ = PdVmDotNetHost.InitializeWindowsFormsApplication();

            var formType = Type.GetType("System.Windows.Forms.Form, System.Windows.Forms", throwOnError: true)!;
            var invoker = Activator.CreateInstance(formType) ??
                throw new InvalidOperationException("unable to create the WinForms dispatcher window");
            formType.GetProperty("ShowInTaskbar", BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(invoker, false);
            _ = formType.GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance)!.GetValue(invoker);

            _invoker = invoker;
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
            Ready.Set();

            var application = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms", throwOnError: true)!;
            application.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)!
                .Invoke(null, null);
        }
        catch (Exception exception)
        {
            _startupError = exception;
            Ready.Set();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
