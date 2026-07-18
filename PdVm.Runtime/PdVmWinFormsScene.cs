using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PdVm.Runtime;

/// <summary>
/// A generic retained scene for efficiently drawing rectangular UI nodes on a WinForms control.
/// Layout, style selection, input interpretation, and animation state belong to the caller.
/// </summary>
[PdVmInteropType("System.Windows.Scene")]
public static class PdVmWinFormsScene
{
    private sealed record ImageResource(object Image, int Width, int Height) : IDisposable
    {
        public void Dispose() => (Image as IDisposable)?.Dispose();
    }

    private sealed class VisualStyle : IDisposable
    {
        private IntPtr _backgroundBrush;
        private IntPtr _borderBrush;

        public VisualStyle(
            int borderKind,
            IntPtr backgroundBrush,
            IntPtr borderBrush,
            uint foregroundColor,
            string text,
            int imageId)
        {
            BorderKind = borderKind;
            _backgroundBrush = backgroundBrush;
            _borderBrush = borderBrush;
            ForegroundColor = foregroundColor;
            Text = text;
            ImageId = imageId;
        }

        public int BorderKind { get; }
        public IntPtr BackgroundBrush => _backgroundBrush;
        public IntPtr BorderBrush => _borderBrush;
        public uint ForegroundColor { get; }
        public string Text { get; }
        public int ImageId { get; }

        public void Dispose()
        {
            DeleteGdiObject(ref _backgroundBrush);
            DeleteGdiObject(ref _borderBrush);
        }
    }

    private readonly record struct Node(int Left, int Top, int Width, int Height, int StyleIndex)
    {
        public bool Intersects(int left, int top, int right, int bottom) =>
            Left < right && Left + Width > left && Top < bottom && Top + Height > top;
    }

    private sealed class Surface : IDisposable
    {
        private readonly object _control;
        private readonly Type _rectangleType;
        private readonly MethodInfo _invalidateRectangle;
        private readonly MethodInfo _getHdc;
        private readonly MethodInfo _releaseHdc;
        private readonly MethodInfo _drawImageUnscaled;
        private readonly Type _imageType;
        private readonly Type _bitmapType;
        private readonly Dictionary<int, ImageResource> _images = [];
        private VisualStyle[] _styles;
        private Node[] _nodes = [];
        private IntPtr _fontHandle;
        private bool _disposed;

        public Surface(object control, object font)
        {
            _control = control;
            var drawingAssembly = font.GetType().Assembly;
            _rectangleType = drawingAssembly.GetType("System.Drawing.Rectangle", throwOnError: true)!;
            var graphicsType = drawingAssembly.GetType("System.Drawing.Graphics", throwOnError: true)!;
            _imageType = drawingAssembly.GetType("System.Drawing.Image", throwOnError: true)!;
            _bitmapType = drawingAssembly.GetType("System.Drawing.Bitmap", throwOnError: true)!;
            _invalidateRectangle = control.GetType().GetMethod(
                "Invalidate",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [_rectangleType],
                modifiers: null) ?? throw new InvalidOperationException(
                $"{control.GetType().FullName} does not expose Invalidate(Rectangle)");
            _getHdc = graphicsType.GetMethod(
                "GetHdc",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)!;
            _releaseHdc = graphicsType.GetMethod(
                "ReleaseHdc",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [typeof(IntPtr)],
                modifiers: null)!;
            _drawImageUnscaled = graphicsType.GetMethod(
                "DrawImageUnscaled",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [_imageType, typeof(int), typeof(int)],
                modifiers: null)!;
            _fontHandle = (IntPtr)font.GetType().GetMethod(
                "ToHfont",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)!.Invoke(font, null)!;

            var background = control.GetType().GetProperty("BackColor")!.GetValue(control)!;
            var foreground = control.GetType().GetProperty("ForeColor")!.GetValue(control)!;
            _styles = [CreateStyle(0, background, foreground, foreground, string.Empty, -1)];
        }

        public void ConfigureStyles(
            long[] borderKinds,
            object[] backgrounds,
            object[] borderColors,
            object[] foregrounds,
            string[] texts,
            long[] imageIds)
        {
            var count = borderKinds.Length;
            if (count == 0 ||
                backgrounds.Length != count ||
                borderColors.Length != count ||
                foregrounds.Length != count ||
                texts.Length != count ||
                imageIds.Length != count)
            {
                throw new ArgumentException("scene style arrays must have the same non-zero length");
            }

            var styles = new VisualStyle[count];
            try
            {
                for (var index = 0; index < count; index++)
                {
                    if (borderKinds[index] is < 0 or > 3)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(borderKinds),
                            "border kind must be 0 (none), 1 (line), 2 (raised), or 3 (sunken)");
                    }
                    styles[index] = CreateStyle(
                        checked((int)borderKinds[index]),
                        backgrounds[index],
                        borderColors[index],
                        foregrounds[index],
                        texts[index],
                        checked((int)imageIds[index]));
                }
            }
            catch
            {
                foreach (var style in styles)
                {
                    style?.Dispose();
                }
                throw;
            }

            var previous = _styles;
            _styles = styles;
            foreach (var style in previous)
            {
                style.Dispose();
            }
            InvalidateAll();
        }

        public void SetImage(int id, byte[] encodedImage)
        {
            var image = DecodeImage(encodedImage);
            if (_images.Remove(id, out var previous))
            {
                previous.Dispose();
            }
            _images[id] = image;
            InvalidateAll();
        }

        public void SetNodes(long[] lefts, long[] tops, long[] widths, long[] heights)
        {
            var count = lefts.Length;
            if (tops.Length != count || widths.Length != count || heights.Length != count)
            {
                throw new ArgumentException("scene node geometry arrays must have the same length");
            }

            var nodes = new Node[count];
            for (var index = 0; index < count; index++)
            {
                if (widths[index] <= 0 || heights[index] <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(widths), "scene node dimensions must be positive");
                }
                nodes[index] = new Node(
                    checked((int)lefts[index]),
                    checked((int)tops[index]),
                    checked((int)widths[index]),
                    checked((int)heights[index]),
                    0);
            }
            _nodes = nodes;
            InvalidateAll();
        }

        public void Present(long[] styleIndices)
        {
            if (styleIndices.Length != _nodes.Length)
            {
                throw new ArgumentException("style index count must match the scene node count");
            }

            var changed = false;
            var dirtyLeft = int.MaxValue;
            var dirtyTop = int.MaxValue;
            var dirtyRight = int.MinValue;
            var dirtyBottom = int.MinValue;
            for (var index = 0; index < styleIndices.Length; index++)
            {
                var styleIndex = styleIndices[index];
                if (styleIndex < 0 || styleIndex >= _styles.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(styleIndices));
                }
                var node = _nodes[index];
                if (node.StyleIndex == styleIndex)
                {
                    continue;
                }
                _nodes[index] = node with { StyleIndex = checked((int)styleIndex) };
                dirtyLeft = Math.Min(dirtyLeft, node.Left);
                dirtyTop = Math.Min(dirtyTop, node.Top);
                dirtyRight = Math.Max(dirtyRight, checked(node.Left + node.Width));
                dirtyBottom = Math.Max(dirtyBottom, checked(node.Top + node.Height));
                changed = true;
            }
            if (changed)
            {
                Invalidate(dirtyLeft, dirtyTop, dirtyRight - dirtyLeft, dirtyBottom - dirtyTop);
                Invoke(_control, "Update");
            }
        }

        public void HandlePaint(object? sender, object args)
        {
            var graphics = args.GetType().GetProperty("Graphics")!.GetValue(args)!;
            var clip = args.GetType().GetProperty("ClipRectangle")!.GetValue(args)!;
            var clipType = clip.GetType();
            var clipLeft = (int)clipType.GetProperty("Left")!.GetValue(clip)!;
            var clipTop = (int)clipType.GetProperty("Top")!.GetValue(clip)!;
            var clipRight = (int)clipType.GetProperty("Right")!.GetValue(clip)!;
            var clipBottom = (int)clipType.GetProperty("Bottom")!.GetValue(clip)!;
            var hdc = (IntPtr)_getHdc.Invoke(graphics, null)!;
            var previousFont = NativeDrawing.SelectObject(hdc, _fontHandle);
            var previousMode = NativeDrawing.SetBkMode(hdc, NativeDrawing.Transparent);
            var images = new List<(ImageResource Image, int X, int Y)>();
            try
            {
                foreach (var node in _nodes)
                {
                    if (!node.Intersects(clipLeft, clipTop, clipRight, clipBottom))
                    {
                        continue;
                    }
                    var style = _styles[node.StyleIndex];
                    var rectangle = new NativeDrawing.Rect(
                        node.Left,
                        node.Top,
                        checked(node.Left + node.Width),
                        checked(node.Top + node.Height));
                    _ = NativeDrawing.FillRect(hdc, ref rectangle, style.BackgroundBrush);
                    var border = rectangle;
                    if (style.BorderKind == 1)
                    {
                        _ = NativeDrawing.FrameRect(hdc, ref border, style.BorderBrush);
                    }
                    else if (style.BorderKind is 2 or 3)
                    {
                        _ = NativeDrawing.DrawEdge(
                            hdc,
                            ref border,
                            style.BorderKind == 2 ? NativeDrawing.EdgeRaised : NativeDrawing.EdgeSunken,
                            NativeDrawing.BorderRectangle);
                    }
                    if (style.Text.Length != 0)
                    {
                        _ = NativeDrawing.SetTextColor(hdc, style.ForegroundColor);
                        var textRectangle = rectangle;
                        _ = NativeDrawing.DrawText(
                            hdc,
                            style.Text,
                            style.Text.Length,
                            ref textRectangle,
                            NativeDrawing.CenteredSingleLine);
                    }
                    if (style.ImageId >= 0 && _images.TryGetValue(style.ImageId, out var image))
                    {
                        images.Add((
                            image,
                            node.Left + (node.Width - image.Width) / 2,
                            node.Top + (node.Height - image.Height) / 2));
                    }
                }
            }
            finally
            {
                _ = NativeDrawing.SetBkMode(hdc, previousMode);
                _ = NativeDrawing.SelectObject(hdc, previousFont);
                _releaseHdc.Invoke(graphics, [hdc]);
            }

            foreach (var item in images)
            {
                _drawImageUnscaled.Invoke(graphics, [item.Image.Image, item.X, item.Y]);
            }
        }

        public void HandleDisposed(object? sender, object args) => Dispose();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DeleteGdiObject(ref _fontHandle);
            foreach (var style in _styles)
            {
                style.Dispose();
            }
            foreach (var image in _images.Values)
            {
                image.Dispose();
            }
            _images.Clear();
        }

        private VisualStyle CreateStyle(
            int borderKind,
            object background,
            object borderColor,
            object foreground,
            string text,
            int imageId)
        {
            var backgroundBrush = NativeDrawing.CreateSolidBrush(ToColorRef(background));
            var borderBrush = IntPtr.Zero;
            try
            {
                borderBrush = NativeDrawing.CreateSolidBrush(ToColorRef(borderColor));
                return new VisualStyle(
                    borderKind,
                    backgroundBrush,
                    borderBrush,
                    ToColorRef(foreground),
                    text,
                    imageId);
            }
            catch
            {
                DeleteGdiObject(ref backgroundBrush);
                DeleteGdiObject(ref borderBrush);
                throw;
            }
        }

        private ImageResource DecodeImage(byte[] encodedImage)
        {
            ArgumentNullException.ThrowIfNull(encodedImage);
            if (encodedImage.Length == 0)
            {
                throw new ArgumentException("encoded image cannot be empty", nameof(encodedImage));
            }
            using var stream = new MemoryStream(encodedImage, writable: false);
            using var decoded = (IDisposable)(_imageType.GetMethod(
                "FromStream",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(Stream)],
                modifiers: null)!.Invoke(null, [stream]) ??
                throw new InvalidOperationException("unable to decode image"));
            var bitmap = _bitmapType.GetConstructor([_imageType])!.Invoke([decoded]);
            var width = (int)_imageType.GetProperty("Width")!.GetValue(bitmap)!;
            var height = (int)_imageType.GetProperty("Height")!.GetValue(bitmap)!;
            return new ImageResource(bitmap, width, height);
        }

        private void InvalidateAll()
        {
            Invoke(_control, "Invalidate");
            Invoke(_control, "Update");
        }

        private void Invalidate(int left, int top, int width, int height)
        {
            var rectangle = Activator.CreateInstance(
                _rectangleType,
                left,
                top,
                width,
                height)!;
            _invalidateRectangle.Invoke(_control, [rectangle]);
        }
    }

    private static readonly ConditionalWeakTable<object, Surface> Surfaces = new();

    public static object Create(object control, object font)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(font);
        var surface = new Surface(control, font);
        BindEvent(control, "Paint", surface, nameof(Surface.HandlePaint));
        BindEvent(control, "Disposed", surface, nameof(Surface.HandleDisposed));
        Surfaces.Add(control, surface);
        return control;
    }

    public static void ConfigureStyles(
        object handle,
        long[] borderKinds,
        object[] backgrounds,
        object[] borderColors,
        object[] foregrounds,
        string[] texts,
        long[] imageIds)
    {
        var surface = GetSurface(handle);
        surface.ConfigureStyles(borderKinds, backgrounds, borderColors, foregrounds, texts, imageIds);
    }

    public static void SetImage(object handle, long id, byte[] encodedImage)
    {
        var surface = GetSurface(handle);
        surface.SetImage(checked((int)id), encodedImage);
    }

    public static void SetNodes(
        object handle,
        long[] lefts,
        long[] tops,
        long[] widths,
        long[] heights)
    {
        var surface = GetSurface(handle);
        surface.SetNodes(lefts, tops, widths, heights);
    }

    public static long[] Present(object handle, long[] styleIndices)
    {
        var surface = GetSurface(handle);
        surface.Present(styleIndices);
        return styleIndices;
    }

    private static Surface GetSurface(object handle) =>
        Surfaces.TryGetValue(handle, out var surface)
            ? surface
            : throw new InvalidOperationException("control is not a configured scene surface");

    private static void BindEvent(object control, string eventName, object target, string methodName)
    {
        var eventInfo = control.GetType().GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException($"{control.GetType().FullName} does not expose {eventName}");
        eventInfo.AddEventHandler(control, CreateEventDelegate(eventInfo.EventHandlerType!, target, methodName));
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

    private static uint ToColorRef(object color)
    {
        var type = color.GetType();
        var red = (byte)type.GetProperty("R")!.GetValue(color)!;
        var green = (byte)type.GetProperty("G")!.GetValue(color)!;
        var blue = (byte)type.GetProperty("B")!.GetValue(color)!;
        return red | ((uint)green << 8) | ((uint)blue << 16);
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(
            method,
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)!.Invoke(target, null);

    private static void DeleteGdiObject(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        _ = NativeDrawing.DeleteObject(handle);
        handle = IntPtr.Zero;
    }

    private static class NativeDrawing
    {
        public const int Transparent = 1;
        public const uint EdgeRaised = 0x0005;
        public const uint EdgeSunken = 0x000A;
        public const uint BorderRectangle = 0x000F;
        public const uint CenteredSingleLine = 0x0825;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect(int left, int top, int right, int bottom)
        {
            public int Left = left;
            public int Top = top;
            public int Right = right;
            public int Bottom = bottom;
        }

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr hdc, uint color);

        [DllImport("user32.dll")]
        public static extern int FillRect(IntPtr hdc, ref Rect rectangle, IntPtr brush);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DrawEdge(IntPtr hdc, ref Rect rectangle, uint edge, uint flags);

        [DllImport("user32.dll")]
        public static extern int FrameRect(IntPtr hdc, ref Rect rectangle, IntPtr brush);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int DrawText(
            IntPtr hdc,
            string text,
            int length,
            ref Rect rectangle,
            uint format);
    }
}
