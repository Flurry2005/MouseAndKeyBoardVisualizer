using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using MouseSwipeVisualizer.Input;
using MouseSwipeVisualizer.Settings;

namespace MouseSwipeVisualizer.Rendering;

/// <summary>Axis-aligned rectangle in canvas pixels.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectD Deflate(double amount) =>
        new(X + amount, Y + amount, Math.Max(0, Width - 2 * amount), Math.Max(0, Height - 2 * amount));
}

/// <summary>Style + geometry parameters of the frame panel and keyboard (from <see cref="AppSettings"/>).</summary>
public sealed record OverlayStyle(
    bool FrameEnabled,
    double FrameMargin,
    double FramePadding,
    double FrameBorderWidth,
    double FrameCornerRadius,
    uint FrameBackground,
    uint FrameBorder,
    bool KeyboardEnabled,
    KeyboardPosition KeyboardPosition,
    double KeyboardSplit,
    uint KeyFill,
    uint KeyBorder,
    uint KeyLabel,
    uint KeyPressedFill,
    uint KeyPressedLabel,
    bool SwipeBoxEnabled,
    uint SwipeBoxFill,
    uint SwipeBoxBorder,
    double SwipeBoxBorderWidth,
    double SwipeBoxCornerRadius,
    double SwipeBoxPadding,
    double SwipeBoxWidth = 1,
    double SwipeBoxHeight = 1,
    double FrameWidth = 1,
    double FrameHeight = 1,
    string BackgroundImage = "",
    double ImageBlur = 0,
    double ImageDim = 0,
    bool Glass = false,
    uint GlassTint = 0xFFFFFFFF,
    double GlassOpacity = 0.12,
    double GlassBlur = 0,
    bool Borders = true,
    double ImageFadeMs = 0,
    bool AccentTrail = false,
    bool AccentKeyBorders = false,
    bool AccentPressedKeys = false,
    bool AccentFrameBorders = false,
    bool AutoContrast = false)
{
    public static OverlayStyle From(AppSettings s)
    {
        static uint Argb(string text, System.Windows.Media.Color fallback) =>
            SwipeModelBuilder.ToArgb(AppSettings.ParseColorOrDefault(text, fallback), opaque: true);

        return new OverlayStyle(
            s.FrameEnabled, s.FrameMargin, s.FramePadding, s.BordersEnabled ? s.FrameBorderWidth : 0, s.FrameCornerRadius,
            Argb(s.FrameBackgroundColor, System.Windows.Media.Colors.Black),
            Argb(s.FrameBorderColor, System.Windows.Media.Colors.White),
            s.KeyboardEnabled, s.KeyboardPosition, s.KeyboardSplitPercent / 100.0,
            Argb(s.KeyFillColor, System.Windows.Media.Colors.Black),
            Argb(s.KeyBorderColor, System.Windows.Media.Colors.White),
            Argb(s.KeyLabelColor, System.Windows.Media.Colors.White),
            Argb(s.KeyPressedFillColor, System.Windows.Media.Colors.White),
            Argb(s.KeyPressedLabelColor, System.Windows.Media.Colors.Black),
            s.SwipeBoxEnabled,
            Argb(s.SwipeBoxFillColor, System.Windows.Media.Colors.Black),
            Argb(s.SwipeBoxBorderColor, System.Windows.Media.Colors.White),
            s.BordersEnabled ? s.SwipeBoxBorderWidth : 0, s.SwipeBoxCornerRadius, s.SwipeBoxPadding,
            s.SwipeBoxWidthPercent / 100.0, s.SwipeBoxHeightPercent / 100.0,
            s.FrameWidthPercent / 100.0, s.FrameHeightPercent / 100.0,
            s.BackgroundImagePath ?? string.Empty, s.BackgroundImageBlur, s.BackgroundImageDim / 100.0,
            s.GlassEnabled, Argb(s.GlassTintColor, System.Windows.Media.Colors.White), s.GlassOpacity / 100.0, s.GlassBlur,
            s.BordersEnabled, s.BackgroundFadeMs,
            s.CoverColorsEnabled && s.CoverAccentTrail,
            s.CoverColorsEnabled && s.CoverAccentKeyBorders,
            s.CoverColorsEnabled && s.CoverAccentPressedKeys,
            s.CoverColorsEnabled && s.CoverAccentFrameBorders,
            s.CoverColorsEnabled && s.CoverAutoContrast);
    }
}

/// <summary>
/// Where frame, keyboard and swipe go on a canvas. <see cref="SwipeBox"/> is the box drawn around the
/// mouse area (empty when off), <see cref="Swipe"/> the area the swipe is drawn in and
/// <see cref="SwipeClip"/> (empty = none) where swipe pixels may land (inside the box border).
/// <see cref="SwipeRadius"/> (swipe scale: centre → edge at sensitivity 1) and <see cref="KeyUnit"/>
/// (key size) come from the full-size layout, so a smaller frame/box only shrinks its content once the
/// edge actually reaches it.
/// </summary>
public readonly record struct OverlayLayout(
    RectD Frame,
    RectD Keyboard,
    RectD Swipe,
    RectD SwipeBox = default,
    RectD SwipeClip = default,
    double SwipeRadius = 0,
    double KeyUnit = 0)
{
    public static OverlayLayout Compute(int width, int height, OverlayStyle style)
    {
        OverlayLayout actual = ComputeRects(width, height, style);
        OverlayLayout natural = style is { FrameWidth: >= 1, FrameHeight: >= 1, SwipeBoxWidth: >= 1, SwipeBoxHeight: >= 1 }
            ? actual
            : ComputeRects(width, height, style with { FrameWidth = 1, FrameHeight = 1, SwipeBoxWidth = 1, SwipeBoxHeight = 1 });
        return actual with
        {
            SwipeRadius = Math.Min(HalfMin(natural.Swipe), HalfMin(actual.Swipe)),
            KeyUnit = Math.Min(FitUnit(natural.Keyboard), FitUnit(actual.Keyboard)),
        };
    }

    private static double HalfMin(RectD r) => Math.Max(Math.Min(r.Width, r.Height) / 2, 0);

    /// <summary>Largest key unit whose block still fits the area.</summary>
    private static double FitUnit(RectD area) =>
        area.IsEmpty ? 0 : Math.Min(area.Width / KeyboardLayout.WidthUnits, area.Height / KeyboardLayout.HeightUnits);

    private static double Even(double v) => Math.Round(v / 2) * 2;

    /// <summary>Rectangle of <paramref name="fx"/> x <paramref name="fy"/> the size of <paramref name="r"/>, same centre, even pixels.</summary>
    private static RectD Scaled(RectD r, double fx, double fy)
    {
        if (fx >= 1 && fy >= 1)
        {
            return r;
        }

        double w = Even(r.Width * Math.Clamp(fx, 0.05, 1));
        double h = Even(r.Height * Math.Clamp(fy, 0.05, 1));
        return new RectD(Even(r.CenterX - w / 2), Even(r.CenterY - h / 2), w, h);
    }

    /// <summary>
    /// Frame = canvas minus margin (optionally smaller, centred); content = frame minus border and
    /// padding; the keyboard takes <see cref="OverlayStyle.KeyboardSplit"/> of the content (default 40 %)
    /// on the chosen side and the swipe gets the rest. Rectangles are snapped to even pixels so edges
    /// line up with NV12's 2x2 blocks.
    /// </summary>
    private static OverlayLayout ComputeRects(int width, int height, OverlayStyle style)
    {
        var canvas = new RectD(0, 0, width, height);
        RectD frame = style.FrameEnabled ? canvas.Deflate(style.FrameMargin) : canvas;
        frame = new RectD(Even(frame.X), Even(frame.Y), Even(frame.Width), Even(frame.Height));
        frame = Scaled(frame, style.FrameWidth, style.FrameHeight);
        RectD content = frame.Deflate((style.FrameEnabled ? style.FrameBorderWidth : 0) + style.FramePadding);
        if (!style.KeyboardEnabled || content.IsEmpty)
        {
            return WithSwipeBox(new OverlayLayout(frame, default, content), style);
        }

        double gap = Math.Max(8, style.FramePadding);
        bool horizontal = style.KeyboardPosition is KeyboardPosition.Left or KeyboardPosition.Right;
        double total = horizontal ? content.Width : content.Height;
        double keyboardSize = Even(total * style.KeyboardSplit);
        double swipeSize = Math.Max(0, total - keyboardSize - gap);
        RectD keyboard, swipe;
        switch (style.KeyboardPosition)
        {
            case KeyboardPosition.Right:
                swipe = new RectD(content.X, content.Y, swipeSize, content.Height);
                keyboard = new RectD(content.Right - keyboardSize, content.Y, keyboardSize, content.Height);
                break;
            case KeyboardPosition.Top:
                keyboard = new RectD(content.X, content.Y, content.Width, keyboardSize);
                swipe = new RectD(content.X, content.Bottom - swipeSize, content.Width, swipeSize);
                break;
            case KeyboardPosition.Bottom:
                swipe = new RectD(content.X, content.Y, content.Width, swipeSize);
                keyboard = new RectD(content.X, content.Bottom - keyboardSize, content.Width, keyboardSize);
                break;
            default:
                keyboard = new RectD(content.X, content.Y, keyboardSize, content.Height);
                swipe = new RectD(content.Right - swipeSize, content.Y, swipeSize, content.Height);
                break;
        }

        return WithSwipeBox(new OverlayLayout(frame, keyboard, swipe), style);
    }

    /// <summary>The swipe area becomes the box (optionally smaller, centred); the swipe is drawn inside its border + padding.</summary>
    private static OverlayLayout WithSwipeBox(OverlayLayout layout, OverlayStyle style)
    {
        if (!style.SwipeBoxEnabled || layout.Swipe.IsEmpty)
        {
            return layout;
        }

        RectD s = layout.Swipe;
        var full = new RectD(Even(s.X), Even(s.Y), Even(s.Right) - Even(s.X), Even(s.Bottom) - Even(s.Y));
        RectD box = Scaled(full, style.SwipeBoxWidth, style.SwipeBoxHeight);

        // Clip = inside of the border, on even pixels so chroma-safe 2x2 blocks never cross it.
        double inset = Math.Ceiling(style.SwipeBoxBorderWidth / 2) * 2;
        RectD clip = box.Deflate(inset);
        return layout with
        {
            SwipeBox = box,
            Swipe = box.Deflate(style.SwipeBoxBorderWidth + style.SwipeBoxPadding),
            SwipeClip = clip.IsEmpty ? new RectD(box.CenterX, box.CenterY, 0, 0) : clip,
        };
    }

    /// <summary>
    /// Key rectangles (same order as <see cref="KeyboardLayout.Keys"/>) centred in the keyboard area, at
    /// <paramref name="maxUnit"/> (<see cref="KeyUnit"/>) or smaller if the area is too small.
    /// </summary>
    public static void ComputeKeys(RectD area, Span<RectD> keys, out double unit, double maxUnit = double.MaxValue)
    {
        unit = Math.Min(FitUnit(area), maxUnit > 0 ? maxUnit : double.MaxValue);
        double left = area.X + (area.Width - unit * KeyboardLayout.WidthUnits) / 2;
        double top = area.Y + (area.Height - unit * KeyboardLayout.HeightUnits) / 2;
        double gap = KeyboardLayout.Gap * unit;
        double x = left;
        int row = -1;
        for (int i = 0; i < KeyboardLayout.Keys.Count; i++)
        {
            KeyDefinition key = KeyboardLayout.Keys[i];
            if (key.Row != row)
            {
                row = key.Row;
                x = left;
            }

            double y = top + row * (unit + gap);
            keys[i] = new RectD(x, y, key.Width * unit, unit);
            x += key.Width * unit + gap;
        }
    }
}

/// <summary>Anti-aliased label/glyph coverage mask (0-255).</summary>
public sealed record GlyphMask(int Width, int Height, byte[] Alpha);

/// <summary>
/// Renders key labels once per (text, size) with GDI+ into alpha masks and caches them, so drawing a
/// keyboard frame only blits bytes. Used on the engine thread only.
/// </summary>
public sealed class GlyphCache
{
    private const int MaxEntries = 256;
    private const string ShiftArrow = "⇧";
    private readonly Dictionary<(string Text, int Size, bool Symbol), GlyphMask> _cache = new();

    public GlyphMask Get(string text, int pixelSize, bool symbol)
    {
        if (_cache.TryGetValue((text, pixelSize, symbol), out GlyphMask? mask))
        {
            return mask;
        }

        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        mask = Render(text, Math.Max(4, pixelSize), symbol);
        _cache[(text, pixelSize, symbol)] = mask;
        return mask;
    }

    private static GlyphMask Render(string text, int pixelSize, bool symbol)
    {
        if (text.Length == 0)
        {
            return new GlyphMask(0, 0, Array.Empty<byte>());
        }

        int width = pixelSize * (text.Length + 2) + 8;
        int height = pixelSize * 2 + 8;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (text == ShiftArrow)
            {
                // Font arrows (⇧ is an outline, ⬆ is a thin sliver): draw a solid Shift arrow instead.
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float s = pixelSize, aw = s * 0.95f;
                PointF P(float x, float y) => new(4 + x * aw, 4 + y * s);
                g.FillPolygon(Brushes.White, new[]
                {
                    P(0.5f, 0), P(1, 0.56f), P(0.73f, 0.56f), P(0.73f, 1), P(0.27f, 1), P(0.27f, 0.56f), P(0, 0.56f),
                });
            }
            else
            {
                using var font = new Font(symbol ? "Segoe UI Symbol" : "Segoe UI Semibold", pixelSize, GraphicsUnit.Pixel);
                g.DrawString(text, font, Brushes.White, 4, 4, StringFormat.GenericTypographic);
            }
        }

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width, width);
        }

        bitmap.UnlockBits(data);

        // Trim to the ink so the label can be centred exactly.
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if ((pixels[y * width + x] >>> 24) > 8)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < 0)
        {
            return new GlyphMask(0, 0, Array.Empty<byte>());
        }

        int w = maxX - minX + 1, h = maxY - minY + 1;
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                alpha[y * w + x] = (byte)(pixels[(y + minY) * width + x + minX] >>> 24);
            }
        }

        return new GlyphMask(w, h, alpha);
    }
}
