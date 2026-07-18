using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PdVm.Runtime;

/// <summary>
/// Generic Windows Forms presentation helpers used by RustScript UI code.
/// </summary>
[PdVmInteropType("System.Windows.Visuals")]
public static class PdVmWinFormsVisuals
{
    private sealed class ControlBitmapState
    {
        public IDisposable? Bitmap { get; set; }
    }

    private sealed class SpriteText(
        object control,
        object bitmap,
        int glyphWidth,
        int glyphHeight,
        string glyphCharacters,
        string initialText) : IDisposable
    {
        private readonly Type _rectangleType = bitmap.GetType().Assembly.GetType(
            "System.Drawing.Rectangle",
            throwOnError: true)!;
        private readonly MethodInfo _drawImage = ResolveDrawImage(bitmap.GetType().Assembly);
        private readonly object _pixelUnit = ResolvePixelUnit(bitmap.GetType().Assembly);
        private string _text = ValidateText(initialText, glyphCharacters);

        public void SetText(string text) => _text = ValidateText(text, glyphCharacters);

        public void HandlePaint(object? sender, object args)
        {
            var graphics = args.GetType().GetProperty(
                "Graphics",
                BindingFlags.Public | BindingFlags.Instance)!.GetValue(args)!;
            var client = control.GetType().GetProperty(
                "ClientRectangle",
                BindingFlags.Public | BindingFlags.Instance)!.GetValue(control)!;
            var width = (int)_rectangleType.GetProperty("Width")!.GetValue(client)!;
            var height = (int)_rectangleType.GetProperty("Height")!.GetValue(client)!;
            var left = Math.Max(0, (width - _text.Length * glyphWidth) / 2);
            var top = Math.Max(0, (height - glyphHeight) / 2);
            for (var index = 0; index < _text.Length; index++)
            {
                var glyph = glyphCharacters.IndexOf(_text[index]);
                var destination = Activator.CreateInstance(
                    _rectangleType,
                    left + index * glyphWidth,
                    top,
                    glyphWidth,
                    glyphHeight)!;
                var source = Activator.CreateInstance(
                    _rectangleType,
                    glyph * glyphWidth,
                    0,
                    glyphWidth,
                    glyphHeight)!;
                _drawImage.Invoke(graphics, [bitmap, destination, source, _pixelUnit]);
            }
        }

        public void HandleDisposed(object? sender, object args) => Dispose();

        public void Dispose() => (bitmap as IDisposable)?.Dispose();

        private static string ValidateText(string text, string glyphCharacters)
        {
            ArgumentNullException.ThrowIfNull(text);
            for (var index = 0; index < text.Length; index++)
            {
                if (glyphCharacters.IndexOf(text[index]) < 0)
                {
                    throw new ArgumentException(
                        $"sprite text contains unconfigured glyph '{text[index]}'",
                        nameof(text));
                }
            }
            return text;
        }

        private static MethodInfo ResolveDrawImage(Assembly drawingAssembly)
        {
            var graphicsType = drawingAssembly.GetType("System.Drawing.Graphics", throwOnError: true)!;
            var imageType = drawingAssembly.GetType("System.Drawing.Image", throwOnError: true)!;
            var rectangleType = drawingAssembly.GetType("System.Drawing.Rectangle", throwOnError: true)!;
            var graphicsUnitType = drawingAssembly.GetType(
                "System.Drawing.GraphicsUnit",
                throwOnError: true)!;
            return graphicsType.GetMethod(
                "DrawImage",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [imageType, rectangleType, rectangleType, graphicsUnitType],
                modifiers: null)!;
        }

        private static object ResolvePixelUnit(Assembly drawingAssembly)
        {
            var graphicsUnitType = drawingAssembly.GetType(
                "System.Drawing.GraphicsUnit",
                throwOnError: true)!;
            return Enum.Parse(graphicsUnitType, "Pixel", ignoreCase: false);
        }
    }

    private static readonly ConditionalWeakTable<object, ControlBitmapState> ControlBitmaps = new();
    private static readonly ConditionalWeakTable<object, SpriteText> SpriteTexts = new();

    public static void SetApplicationVisualStyleState(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var application = Type.GetType(
            "System.Windows.Forms.Application, System.Windows.Forms",
            throwOnError: true)!;
        var property = application.GetProperty(
            "VisualStyleState",
            BindingFlags.Public | BindingFlags.Static) ??
            throw new InvalidOperationException(
                "System.Windows.Forms.Application does not expose VisualStyleState");
        property.SetValue(null, Enum.Parse(property.PropertyType, state, ignoreCase: false));
    }

    public static void SetClientSize(object form, long width, long height)
    {
        if (width <= 0 || width > int.MaxValue || height <= 0 || height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "client dimensions are out of range");
        }

        var property = form.GetType().GetProperty(
            "ClientSize",
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"{form.GetType().FullName} does not expose ClientSize");
        var size = Activator.CreateInstance(
            property.PropertyType,
            checked((int)width),
            checked((int)height)) ??
            throw new InvalidOperationException("unable to create a WinForms client size");
        property.SetValue(form, size);
    }

    public static void SetControlBounds(
        object control,
        long left,
        long top,
        long width,
        long height)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (left < int.MinValue || left > int.MaxValue ||
            top < int.MinValue || top > int.MaxValue ||
            width < 0 || width > int.MaxValue ||
            height < 0 || height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "control bounds are outside the supported WinForms range");
        }

        SetProperty(control, "Left", checked((int)left));
        SetProperty(control, "Top", checked((int)top));
        SetProperty(control, "Width", checked((int)width));
        SetProperty(control, "Height", checked((int)height));
    }

    public static void SetControlText(object control, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        SetProperty(control, "Text", text);
    }

    public static void SetControlEnabled(object control, bool enabled)
    {
        SetProperty(control, "Enabled", enabled);
    }

    public static void SetControlBitmap(object control, byte[] encodedBitmap)
    {
        ValidateBitmap(encodedBitmap, nameof(encodedBitmap));
        var imageProperty = control.GetType().GetProperty(
            "Image",
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"{control.GetType().FullName} does not expose Image");
        var bitmap = DecodeBitmap(imageProperty.PropertyType, encodedBitmap);
        var state = ControlBitmaps.GetValue(control, static _ => new ControlBitmapState());
        imageProperty.SetValue(control, bitmap);
        state.Bitmap?.Dispose();
        state.Bitmap = bitmap as IDisposable;
    }

    public static void ConfigureSpriteText(
        object control,
        byte[] spriteSheet,
        long glyphWidth,
        long glyphHeight,
        string glyphCharacters,
        string initialText)
    {
        ValidateBitmap(spriteSheet, nameof(spriteSheet));
        if (glyphWidth <= 0 || glyphWidth > 256 || glyphHeight <= 0 || glyphHeight > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(glyphWidth));
        }
        ArgumentException.ThrowIfNullOrEmpty(glyphCharacters);
        if (glyphCharacters.Distinct().Count() != glyphCharacters.Length)
        {
            throw new ArgumentException("sprite glyph characters must be unique", nameof(glyphCharacters));
        }

        if (SpriteTexts.TryGetValue(control, out _))
        {
            throw new InvalidOperationException("control is already configured for sprite text");
        }
        var imageType = Type.GetType(
            "System.Drawing.Image, System.Drawing.Common",
            throwOnError: true)!;
        var bitmap = DecodeBitmap(imageType, spriteSheet);
        var width = (int)imageType.GetProperty("Width")!.GetValue(bitmap)!;
        var height = (int)imageType.GetProperty("Height")!.GetValue(bitmap)!;
        var expectedWidth = checked((int)glyphWidth * glyphCharacters.Length);
        if (width != expectedWidth || height != glyphHeight)
        {
            (bitmap as IDisposable)?.Dispose();
            throw new ArgumentException(
                $"sprite sheet must be {expectedWidth}x{glyphHeight} pixels",
                nameof(spriteSheet));
        }

        var spriteText = new SpriteText(
            control,
            bitmap,
            checked((int)glyphWidth),
            checked((int)glyphHeight),
            glyphCharacters,
            initialText);
        SetProperty(control, "Text", string.Empty);
        BindEvent(control, "Paint", spriteText, nameof(SpriteText.HandlePaint));
        BindEvent(control, "Disposed", spriteText, nameof(SpriteText.HandleDisposed));
        SpriteTexts.Add(control, spriteText);
        Invoke(control, "Invalidate");
        Invoke(control, "Update");
    }

    public static void SetSpriteText(object control, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!SpriteTexts.TryGetValue(control, out var spriteText))
        {
            throw new InvalidOperationException("control is not configured for sprite text");
        }

        spriteText.SetText(text);
        Invoke(control, "Invalidate");
        Invoke(control, "Update");
    }

    public static void ShowMessage(object owner, string text, string caption)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(caption);
        var messageBox = Type.GetType(
            "System.Windows.Forms.MessageBox, System.Windows.Forms",
            throwOnError: true)!;
        var ownerType = Type.GetType(
            "System.Windows.Forms.IWin32Window, System.Windows.Forms",
            throwOnError: true)!;
        var show = messageBox.GetMethod(
            "Show",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [ownerType, typeof(string), typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException(
                "System.Windows.Forms.MessageBox does not expose Show(owner, text, caption)");
        _ = show.Invoke(null, [owner, text, caption]);
    }

    private static object DecodeBitmap(Type imageType, byte[] encodedBitmap)
    {
        using var stream = new MemoryStream(encodedBitmap, writable: false);
        using var decoded = (IDisposable)(imageType.GetMethod(
            "FromStream",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(Stream)],
            modifiers: null)!.Invoke(null, [stream]) ??
            throw new InvalidOperationException("unable to decode bitmap"));
        var bitmapType = imageType.Assembly.GetType("System.Drawing.Bitmap", throwOnError: true)!;
        return bitmapType.GetConstructor([imageType])!.Invoke([decoded]);
    }

    private static void ValidateBitmap(byte[] encodedBitmap, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(encodedBitmap);
        if (encodedBitmap.Length == 0 || encodedBitmap.Length > 1024 * 1024)
        {
            throw new ArgumentException(
                "bitmap must contain between 1 byte and 1 MiB",
                parameterName);
        }
    }

    private static void BindEvent(object control, string eventName, object target, string methodName)
    {
        var eventInfo = control.GetType().GetEvent(
            eventName,
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"{control.GetType().FullName} does not expose {eventName}");
        eventInfo.AddEventHandler(
            control,
            CreateEventDelegate(eventInfo.EventHandlerType!, target, methodName));
    }

    private static Delegate CreateEventDelegate(Type delegateType, object target, string methodName)
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

    private static void SetProperty(object target, string propertyName, object value) =>
        target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(target, value);
}
