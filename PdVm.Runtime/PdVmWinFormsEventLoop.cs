using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PdVm.Runtime;

/// <summary>
/// Thin Windows Forms bridge that queues named events from the dedicated UI thread.
/// RustScript owns all application behavior after <see cref="Wait"/> returns.
/// </summary>
public static class PdVmWinFormsEventLoop
{
    private sealed class FormState
    {
        public ConcurrentQueue<string> Events { get; } = new();

        public AutoResetEvent Signal { get; } = new(false);

        public bool AllowClose { get; set; }
    }

    private sealed class ClosingSubscription(FormState state, string action)
    {
        public void Handle(object? sender, object args)
        {
            if (state.AllowClose)
            {
                return;
            }

            args.GetType().GetProperty("Cancel", BindingFlags.Public | BindingFlags.Instance)!.SetValue(args, true);
            Enqueue(state, action);
        }
    }

    private static readonly ConditionalWeakTable<object, FormState> States = new();

    public static void Show(object form)
    {
        _ = GetState(form);
        Invoke(form, "Show");
    }

    public static void BindClick(object form, object control, string action)
    {
        var state = GetState(form);
        var click = control.GetType().GetEvent("Click", BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException($"{control.GetType().FullName} does not expose a Click event");
        click.AddEventHandler(control, new EventHandler((_, _) => Enqueue(state, action)));
    }

    public static void BindClosing(object form, string action)
    {
        var state = GetState(form);
        var closing = form.GetType().GetEvent("FormClosing", BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException($"{form.GetType().FullName} does not expose a FormClosing event");
        var subscription = new ClosingSubscription(state, action);
        var handler = CreateEventDelegate(closing.EventHandlerType!, subscription, nameof(ClosingSubscription.Handle));
        closing.AddEventHandler(form, handler);
    }

    public static void BindDialog(object form, object control, object dialog, string action)
    {
        BindClick(form, control, action);
    }

    public static string ShowDialog(object form, object dialog)
    {
        var ownerType = Type.GetType("System.Windows.Forms.IWin32Window, System.Windows.Forms", throwOnError: true)!;
        var show = dialog.GetType().GetMethod(
            "ShowDialog",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [ownerType],
            modifiers: null) ?? throw new InvalidOperationException(
                $"{dialog.GetType().FullName} does not expose ShowDialog(IWin32Window)");
        return show.Invoke(dialog, [form])?.ToString() ?? string.Empty;
    }

    public static string Wait(object form)
    {
        var state = GetState(form);
        while (true)
        {
            if (state.Events.TryDequeue(out var action))
            {
                return action;
            }
            state.Signal.WaitOne();
        }
    }

    public static void Close(object form)
    {
        GetState(form).AllowClose = true;
        Invoke(form, "Close");
    }

    private static FormState GetState(object form) => States.GetValue(form, static _ => new FormState());

    private static void Enqueue(FormState state, string action)
    {
        state.Events.Enqueue(action);
        state.Signal.Set();
    }

    private static Delegate CreateEventDelegate(Type delegateType, object target, string methodName)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!;
        var call = Expression.Call(
            Expression.Constant(target),
            method,
            parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
        return Expression.Lambda(delegateType, call, parameters).Compile();
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)!
            .Invoke(target, null);
}
