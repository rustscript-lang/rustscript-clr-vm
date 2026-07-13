using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PdVm.Runtime;

/// <summary>
/// Queues named Windows Forms events and manages form lifetime.
/// Presentation and rendering belong to separate adapters.
/// </summary>
[PdVmInteropType("System.Windows.EventLoop")]
public static class PdVmWinFormsEventLoop
{
    private sealed record PointerSnapshot(string Button, int X, int Y, int Clicks);

    private sealed record QueuedEvent(string Action, PointerSnapshot? Pointer);

    private sealed class FormState
    {
        public ConcurrentQueue<QueuedEvent> Events { get; } = new();

        public AutoResetEvent Signal { get; } = new(false);

        public bool AllowClose { get; set; }

        public PointerSnapshot Pointer = new(string.Empty, -1, -1, 0);
    }

    private sealed class ClosingSubscription(FormState state, string action)
    {
        public void Handle(object? sender, object args)
        {
            if (state.AllowClose)
            {
                return;
            }

            args.GetType().GetProperty(
                "Cancel",
                BindingFlags.Public | BindingFlags.Instance)!.SetValue(args, true);
            Enqueue(state, action);
        }
    }

    private sealed class PointerSubscription(FormState state, string prefix, string kind)
    {
        public void Handle(object? sender, object args)
        {
            if (string.Equals(kind, "leave", StringComparison.Ordinal))
            {
                Enqueue(
                    state,
                    $"{prefix}_leave",
                    new PointerSnapshot(string.Empty, -1, -1, 0));
                return;
            }
            var type = args.GetType();
            var button = type.GetProperty("Button")!.GetValue(args)?.ToString() ?? string.Empty;
            var x = (int)type.GetProperty("X")!.GetValue(args)!;
            var y = (int)type.GetProperty("Y")!.GetValue(args)!;
            var clicks = (int)type.GetProperty("Clicks")!.GetValue(args)!;
            Enqueue(state, $"{prefix}_{kind}", new PointerSnapshot(button, x, y, clicks));
        }
    }

    private static readonly ConditionalWeakTable<object, FormState> States = new();

    public static void Show(object form)
    {
        _ = GetState(form);
        PdVmWinFormsDispatcher.Invoke(() =>
        {
            Invoke(form, "Show");
            return true;
        });
    }

    public static void BindClick(object form, object control, string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var state = GetState(form);
        var click = control.GetType().GetEvent(
            "Click",
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"{control.GetType().FullName} does not expose a Click event");
        click.AddEventHandler(control, new EventHandler((_, _) => Enqueue(state, action)));
    }

    public static void BindPointer(object form, object control, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Contains('_'))
        {
            throw new ArgumentException("pointer event prefix cannot contain '_'", nameof(prefix));
        }
        var state = GetState(form);
        BindPointerEvent(control, "MouseDown", state, prefix, "down");
        BindPointerEvent(control, "MouseUp", state, prefix, "up");
        BindPointerEvent(control, "MouseDoubleClick", state, prefix, "double");
        BindPointerEvent(control, "MouseLeave", state, prefix, "leave");
    }

    public static void BindClosing(object form, string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var state = GetState(form);
        var closing = form.GetType().GetEvent(
            "FormClosing",
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"{form.GetType().FullName} does not expose a FormClosing event");
        var subscription = new ClosingSubscription(state, action);
        var handler = CreateEventDelegate(
            closing.EventHandlerType!,
            subscription,
            nameof(ClosingSubscription.Handle));
        closing.AddEventHandler(form, handler);
    }

    public static void BindDialog(object form, object control, object dialog, string action) =>
        BindClick(form, control, action);

    public static string ShowDialog(object form, object dialog)
    {
        var ownerType = Type.GetType(
            "System.Windows.Forms.IWin32Window, System.Windows.Forms",
            throwOnError: true)!;
        var show = dialog.GetType().GetMethod(
            "ShowDialog",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [ownerType],
            modifiers: null) ?? throw new InvalidOperationException(
                $"{dialog.GetType().FullName} does not expose ShowDialog(IWin32Window)");
        return PdVmWinFormsDispatcher.Invoke(
            () => show.Invoke(dialog, [form])?.ToString() ?? string.Empty);
    }

    public static string Wait(object form)
    {
        var state = GetState(form);
        while (true)
        {
            if (TryDequeue(state, out var action))
            {
                return action;
            }
            state.Signal.WaitOne();
        }
    }

    public static string WaitTimeout(object form, long milliseconds)
    {
        if (milliseconds < 0 || milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        var state = GetState(form);
        if (TryDequeue(state, out var action))
        {
            return action;
        }
        if (!state.Signal.WaitOne((int)milliseconds))
        {
            return string.Empty;
        }
        return TryDequeue(state, out action) ? action : string.Empty;
    }

    public static string GetPointerButton(object form) =>
        Volatile.Read(ref GetState(form).Pointer).Button;

    public static long GetPointerX(object form) =>
        Volatile.Read(ref GetState(form).Pointer).X;

    public static long GetPointerY(object form) =>
        Volatile.Read(ref GetState(form).Pointer).Y;

    public static long GetPointerClicks(object form) =>
        Volatile.Read(ref GetState(form).Pointer).Clicks;

    public static void Close(object form)
    {
        GetState(form).AllowClose = true;
        PdVmWinFormsDispatcher.Invoke(() =>
        {
            Invoke(form, "Close");
            return true;
        });
    }

    private static void BindPointerEvent(
        object control,
        string eventName,
        FormState state,
        string prefix,
        string kind)
    {
        var eventInfo = control.GetType().GetEvent(
            eventName,
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"{control.GetType().FullName} does not expose {eventName}");
        var subscription = new PointerSubscription(state, prefix, kind);
        eventInfo.AddEventHandler(
            control,
            CreateEventDelegate(
                eventInfo.EventHandlerType!,
                subscription,
                nameof(PointerSubscription.Handle)));
    }

    private static FormState GetState(object form) =>
        States.GetValue(form, static _ => new FormState());

    private static bool TryDequeue(FormState state, out string action)
    {
        if (!state.Events.TryDequeue(out var item))
        {
            action = string.Empty;
            return false;
        }
        if (item.Pointer is not null)
        {
            Volatile.Write(ref state.Pointer, item.Pointer);
        }
        action = item.Action;
        return true;
    }

    private static void Enqueue(
        FormState state,
        string action,
        PointerSnapshot? pointer = null)
    {
        state.Events.Enqueue(new QueuedEvent(action, pointer));
        state.Signal.Set();
    }

    private static Delegate CreateEventDelegate(
        Type delegateType,
        object target,
        string methodName)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance)!;
        var call = Expression.Call(
            Expression.Constant(target),
            method,
            parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
        return Expression.Lambda(delegateType, call, parameters).Compile();
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(
            method,
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)!.Invoke(target, null);
}
