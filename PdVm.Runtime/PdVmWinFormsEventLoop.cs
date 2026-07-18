using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PdVm.Runtime;

/// <summary>
/// Binds Windows Forms events directly to RSS callable values owned by the current application session.
/// </summary>
[PdVmInteropType("System.Windows.EventLoop")]
public static class PdVmWinFormsEventLoop
{
    private sealed record EventSnapshot(string Action, string Button, int X, int Y, int Clicks);

    private sealed class FormState
    {
        public bool AllowClose { get; set; }
    }

    private sealed class ClosingSubscription(
        FormState state,
        PdVmScriptCallback<PdVmUnit, PdVmUnit> callback)
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
            callback.Post(PdVmUnit.Value);
        }
    }

    private sealed class PointerSubscription(
        string action,
        bool hasCoordinates,
        PdVmScriptCallback<EventSnapshot, PdVmUnit> callback)
    {
        public void Handle(object? sender, object args)
        {
            if (!hasCoordinates)
            {
                callback.Post(new EventSnapshot(action, string.Empty, -1, -1, 0));
                return;
            }

            var type = args.GetType();
            callback.Post(
                new EventSnapshot(
                    action,
                    type.GetProperty("Button")!.GetValue(args)?.ToString() ?? string.Empty,
                    (int)type.GetProperty("X")!.GetValue(args)!,
                    (int)type.GetProperty("Y")!.GetValue(args)!,
                    (int)type.GetProperty("Clicks")!.GetValue(args)!));
        }
    }

    private sealed class ActionClosingSubscription(
        FormState state,
        string action,
        PdVmScriptCallback<EventSnapshot, PdVmUnit> callback)
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
            callback.Post(new EventSnapshot(action, string.Empty, -1, -1, 0));
        }
    }

    private sealed class EventSubscription(
        object source,
        EventInfo eventInfo,
        Delegate handler) : IDisposable
    {
        public void Dispose() => eventInfo.RemoveEventHandler(source, handler);
    }

    private static readonly ConditionalWeakTable<object, FormState> States = new();

    public static void Show(object form) =>
        PdVmWinFormsApplication.RequireCurrent().RegisterMainForm(form);

    public static void BindClick(
        object form,
        object control,
        [PdVmInteropSchema("fn() -> null")] PdVmCallableValue callable)
    {
        var session = PdVmWinFormsApplication.RequireCurrent();
        var callback = session.CreateUnitCallback(form, callable);
        var click = GetEvent(control, "Click");
        var handler = callback.AsEventHandler();
        click.AddEventHandler(control, handler);
        session.Track(new EventSubscription(control, click, handler));
    }

    public static void BindDialog(
        object form,
        object control,
        object dialog,
        [PdVmInteropSchema("fn() -> null")] PdVmCallableValue callable) =>
        BindClick(form, control, callable);

    public static void BindAction(
        object form,
        object control,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable,
        string action) =>
        BindEventAction(form, control, "Click", callable, action);

    public static void BindShown(
        object form,
        [PdVmInteropSchema("fn() -> null")] PdVmCallableValue callable)
    {
        var session = PdVmWinFormsApplication.RequireCurrent();
        var callback = session.CreateUnitCallback(form, callable);
        var shown = GetEvent(form, "Shown");
        var handler = callback.AsEventHandler();
        shown.AddEventHandler(form, handler);
        session.Track(new EventSubscription(form, shown, handler));
    }

    public static void BindShownAction(
        object form,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable,
        string action) =>
        BindEventAction(form, form, "Shown", callable, action);

    private static void BindEventAction(
        object form,
        object control,
        string eventName,
        PdVmCallableValue callable,
        string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var session = PdVmWinFormsApplication.RequireCurrent();
        var callback = session.CreateCallback(form, callable, CreateEventAdapter());
        var eventInfo = GetEvent(control, eventName);
        var handler = callback.AsEventHandler(
            (_, _) => new EventSnapshot(action, string.Empty, -1, -1, 0));
        eventInfo.AddEventHandler(control, handler);
        session.Track(new EventSubscription(control, eventInfo, handler));
    }

    public static void BindMouseDown(
        object form,
        object control,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable) =>
        BindPointerEvent(form, control, "MouseDown", callable, hasCoordinates: true, action: string.Empty);

    public static void BindMouseUp(
        object form,
        object control,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable) =>
        BindPointerEvent(form, control, "MouseUp", callable, hasCoordinates: true, action: string.Empty);

    public static void BindMouseDoubleClick(
        object form,
        object control,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable) =>
        BindPointerEvent(form, control, "MouseDoubleClick", callable, hasCoordinates: true, action: string.Empty);

    public static void BindMouseLeave(
        object form,
        object control,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable) =>
        BindPointerEvent(form, control, "MouseLeave", callable, hasCoordinates: false, action: string.Empty);

    public static void BindPointer(
        object form,
        object control,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable)
    {
        BindPointerEvent(form, control, "MouseDown", callable, hasCoordinates: true, action: "board_down");
        BindPointerEvent(form, control, "MouseUp", callable, hasCoordinates: true, action: "board_up");
        BindPointerEvent(form, control, "MouseDoubleClick", callable, hasCoordinates: true, action: "board_double");
        BindPointerEvent(form, control, "MouseLeave", callable, hasCoordinates: false, action: "board_leave");
    }

    public static void BindClosing(
        object form,
        [PdVmInteropSchema("fn() -> null")] PdVmCallableValue callable)
    {
        var session = PdVmWinFormsApplication.RequireCurrent();
        var state = GetState(form);
        var callback = session.CreateUnitCallback(form, callable);
        var closing = GetEvent(form, "FormClosing");
        var subscription = new ClosingSubscription(state, callback);
        var handler = CreateEventDelegate(
            closing.EventHandlerType!,
            subscription,
            nameof(ClosingSubscription.Handle));
        closing.AddEventHandler(form, handler);
        session.Track(new EventSubscription(form, closing, handler));
    }

    public static void BindClosingAction(
        object form,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable,
        string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var session = PdVmWinFormsApplication.RequireCurrent();
        var state = GetState(form);
        var callback = session.CreateCallback(form, callable, CreateEventAdapter());
        var closing = GetEvent(form, "FormClosing");
        var subscription = new ActionClosingSubscription(state, action, callback);
        var handler = CreateEventDelegate(
            closing.EventHandlerType!,
            subscription,
            nameof(ActionClosingSubscription.Handle));
        closing.AddEventHandler(form, handler);
        session.Track(new EventSubscription(form, closing, handler));
    }

    public static void BindTimer(
        object form,
        long intervalMilliseconds,
        [PdVmInteropSchema("fn() -> null")] PdVmCallableValue callable)
    {
        BindTimerCore(form, intervalMilliseconds, callable, action: null);
    }

    public static void BindTimerAction(
        object form,
        long intervalMilliseconds,
        [PdVmInteropSchema("fn(map<string>) -> null")] PdVmCallableValue callable,
        string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BindTimerCore(form, intervalMilliseconds, callable, action);
    }

    private static void BindTimerCore(
        object form,
        long intervalMilliseconds,
        PdVmCallableValue callable,
        string? action)
    {
        if (intervalMilliseconds <= 0 || intervalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        }

        var session = PdVmWinFormsApplication.RequireCurrent();
        var timerType = Type.GetType(
            "System.Windows.Forms.Timer, System.Windows.Forms",
            throwOnError: true)!;
        var timer = timerType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        timerType.GetProperty("Interval")!.SetValue(timer, checked((int)intervalMilliseconds));
        var tick = GetEvent(timer, "Tick");
        var handler = action is null
            ? session.CreateUnitCallback(form, callable).AsEventHandler()
            : session.CreateCallback(form, callable, CreateEventAdapter()).AsEventHandler(
                (_, _) => new EventSnapshot(action, string.Empty, -1, -1, 0));
        tick.AddEventHandler(timer, handler);
        session.Track(new EventSubscription(timer, tick, handler));
        session.Track((IDisposable)timer);
        Invoke(timer, "Start");
    }

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
        return show.Invoke(dialog, [form])?.ToString() ?? string.Empty;
    }

    public static void Close(object form)
    {
        GetState(form).AllowClose = true;
        Invoke(form, "Close");
    }

    private static void BindPointerEvent(
        object form,
        object control,
        string eventName,
        PdVmCallableValue callable,
        bool hasCoordinates,
        string action)
    {
        var session = PdVmWinFormsApplication.RequireCurrent();
        var callback = session.CreateCallback(form, callable, CreateEventAdapter());
        var eventInfo = GetEvent(control, eventName);
        var subscription = new PointerSubscription(action, hasCoordinates, callback);
        var handler = CreateEventDelegate(
            eventInfo.EventHandlerType!,
            subscription,
            nameof(PointerSubscription.Handle));
        eventInfo.AddEventHandler(control, handler);
        session.Track(new EventSubscription(control, eventInfo, handler));
    }

    private static IPdVmCallbackAdapter<EventSnapshot, PdVmUnit> CreateEventAdapter() =>
        PdVmCallbackAdapters.Map<EventSnapshot, PdVmUnit>(
            snapshot =>
            [
                Pair("action", snapshot.Action),
                Pair("button", snapshot.Button),
                Pair("x", snapshot.X.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Pair("y", snapshot.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Pair("clicks", snapshot.Clicks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            _ => PdVmUnit.Value);

    private static KeyValuePair<PdVmValue, PdVmValue> Pair(string key, string value) =>
        new(PdVmValue.FromString(key), PdVmValue.FromString(value));

    private static EventInfo GetEvent(object source, string eventName) =>
        source.GetType().GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance) ??
        throw new InvalidOperationException(
            $"{source.GetType().FullName} does not expose a {eventName} event");

    private static FormState GetState(object form) =>
        States.GetValue(form, static _ => new FormState());

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
