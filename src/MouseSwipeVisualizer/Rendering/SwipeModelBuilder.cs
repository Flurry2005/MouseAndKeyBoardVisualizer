using System.Windows.Media;
using MouseSwipeVisualizer.Input;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Swipe;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Rendering;

/// <summary>
/// Turns the <see cref="SwipeTracker"/> state into a <see cref="SwipeRenderModel"/> for a given
/// canvas size. This is the geometry half of the former WPF renderer, unchanged in behaviour:
/// <code>
/// raw points → TrailSmoother → stroke view transform (scale + pan) → centripetal Catmull-Rom
/// subdivision of long segments → per-vertex fade → polylines + arrowhead + re-center ring
/// </code>
/// Not thread-safe; used by the <see cref="Engine.SwipeEngine"/> thread only.
/// </summary>
public sealed class SwipeModelBuilder
{
    /// <summary>Normal (alpha) mode border width, as in the WPF version.</summary>
    public const double OutlineWidthNormal = 1.5;

    /// <summary>
    /// Chroma-safe border width. 2 px guarantees that every 2x2 NV12 chroma block touching an
    /// anti-aliased trail pixel lies completely inside the opaque outline, so chroma subsampling
    /// in the camera can't mix the key colour into the trail either.
    /// </summary>
    public const double OutlineWidthChromaSafe = 2.0;

    public const double RecenterMarkerMs = 300.0;

    /// <summary>Segments fainter than this are skipped (the WPF version's lowest alpha level).</summary>
    public const double MinVisibleAlpha = 1.0 / 46.0;

    private const double MaxSegmentLengthPx = 5.0;
    private const int MaxSubdivisions = 10;
    private const double ArrowLengthPerThickness = 3.2;
    private const double MinArrowLength = 10.0;
    private const double ArrowHalfWidthRatio = 0.62;
    private const double ArrowTipAhead = 0.55;
    private const double MinDirectionLengthPx = 12.0;
    private const double EdgePaddingPx = 2.0;
    private const double RecenterMarkerMaxAlpha = 0.8;

    public static readonly Color ChromaSafeOutlineColor = Color.FromRgb(0x14, 0x14, 0x18);

    private double[] _rawX = new double[256];
    private double[] _rawY = new double[256];
    private long[] _rawT = new long[256];
    private double[] _smoothX = new double[256];
    private double[] _smoothY = new double[256];
    private double[] _outX = new double[1024];
    private double[] _outY = new double[1024];
    private double[] _outA = new double[1024];
    private int _outCount;

    private double _thickness = AppSettings.DefaultTrailThickness;
    private bool _outlineEnabled = true;
    private bool _chromaSafe = true;
    private HeadStyle _headStyle = HeadStyle.Arrow;
    private double _dotSize = AppSettings.DefaultDotSize;
    private uint _dotColor = 0xFFFFFFFF;
    private uint _outlineColor = 0xFF141418;
    private OverlayStyle _style = OverlayStyle.From(new AppSettings());
    private long _styleVersion;
    private long _smoothingHalfWindowTicks;
    private uint _trailColor = 0xFFFFFFFF;
    private uint _backgroundColor = 0xFF00FF00;

    public SwipeModelBuilder()
    {
        Configure(new AppSettings());
    }

    public double ArrowLength => Math.Max(MinArrowLength, _thickness * ArrowLengthPerThickness);

    public double OutlineWidth => _chromaSafe ? OutlineWidthChromaSafe : OutlineWidthNormal;

    /// <summary>How far drawing can extend beyond a point's centre (keeps strokes inside the canvas).</summary>
    public double EdgeMarginPx =>
        _thickness / 2 + (_outlineEnabled ? OutlineWidth : 0) + HeadReach + EdgePaddingPx;

    private double HeadReach => _headStyle switch
    {
        HeadStyle.Arrow => ArrowLength * ArrowTipAhead,
        HeadStyle.Dot => Math.Max(0, _dotSize / 2 - _thickness / 2),
        _ => 0,
    };

    public OverlayStyle Style => _style;

    public void Configure(AppSettings settings)
    {
        _headStyle = settings.HeadStyle;
        _dotSize = settings.DotSize;
        _dotColor = ToArgb(AppSettings.ParseColorOrDefault(settings.DotColor, Colors.White), opaque: true);
        _outlineColor = ToArgb(AppSettings.ParseColorOrDefault(settings.OutlineColor, ChromaSafeOutlineColor), opaque: true);
        _style = OverlayStyle.From(settings);
        _styleVersion++;
        _thickness = settings.TrailThickness;
        _outlineEnabled = settings.OutlineEnabled;
        _chromaSafe = settings.UsesChromaSafeRendering;
        _smoothingHalfWindowTicks = MonotonicClock.MsToTicks(settings.SmoothingStrength * TrailSmoother.MaxHalfWindowMs);
        _trailColor = ToArgb(AppSettings.ParseColorOrDefault(settings.TrailColor, Colors.White), opaque: false);
        _backgroundColor = ToArgb(settings.GetCaptureBackgroundColor(), opaque: true);
    }

    public static uint ToArgb(Color c, bool opaque) =>
        ((uint)(opaque ? (byte)255 : c.A) << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>Usable half extents (in tracker units) for <see cref="SwipeTracker.Update"/> at this canvas size.</summary>
    public (double HalfX, double HalfY) GetHalfExtents(int width, int height)
    {
        OverlayLayout layout = OverlayLayout.Compute(width, height, _style);
        RectD swipe = layout.Swipe;
        double radius = Math.Max(layout.SwipeRadius, 1);
        double margin = EdgeMarginPx / radius;
        return (swipe.Width / 2.0 / radius - margin, swipe.Height / 2.0 / radius - margin);
    }

    /// <param name="keyboard">Current key state for the keyboard panel (null = nothing pressed).</param>
    public void Build(SwipeTracker tracker, int width, int height, long now, SwipeRenderModel model, KeyboardState? keyboard = null)
    {
        model.Width = width;
        model.Height = height;
        model.Thickness = _thickness;
        model.OutlineWidth = OutlineWidth;
        model.OutlineEnabled = _outlineEnabled;
        model.ChromaSafe = _chromaSafe;
        model.TrailColor = _trailColor;
        model.OutlineColor = _outlineColor;
        model.DotColor = _dotColor;
        model.Style = _style;
        model.StyleVersion = _styleVersion;
        OverlayLayout layout = OverlayLayout.Compute(Math.Max(width, 0), Math.Max(height, 0), _style);
        model.Layout = layout;
        model.KeyboardVersion = keyboard?.Version ?? 0;
        Span<bool> pressed = model.KeyPressed;
        for (int k = 0; k < pressed.Length; k++)
        {
            pressed[k] = keyboard != null && keyboard.IsDown(k);
        }
        model.BackgroundColor = _backgroundColor;
        model.ClearGeometry();

        if (width <= 1 || height <= 1)
        {
            return;
        }

        // The swipe lives in its own area (60 % next to the keyboard by default).
        RectD area = layout.Swipe;
        if (area.Width <= 1 || area.Height <= 1)
        {
            return;
        }

        double cx = area.CenterX;
        double cy = area.CenterY;
        double radius = Math.Max(layout.SwipeRadius, 1); // fixed scale until the box edge reaches it
        double invLifetime = 1.0 / Math.Max(tracker.LifetimeTicks, 1);

        if (tracker.LastLiftTimestamp != 0 && now - tracker.LastLiftTimestamp < MonotonicClock.MsToTicks(RecenterMarkerMs))
        {
            AddRecenterMarker(model, cx, cy, now - tracker.LastLiftTimestamp);
        }

        int cursor = 0;
        for (int s = 0; s < tracker.StrokeCount; s++)
        {
            SwipeStroke stroke = tracker.GetStroke(s);
            int n = stroke.PointCount;
            EnsureInputCapacity(n);
            for (int i = 0; i < n; i++)
            {
                ref readonly SwipePoint p = ref tracker.GetPoint(cursor + i);
                _rawX[i] = p.X;
                _rawY[i] = p.Y;
                _rawT[i] = p.Timestamp;
            }

            cursor += n;
            if (n < 2)
            {
                continue;
            }

            TrailSmoother.Smooth(
                _rawX.AsSpan(0, n), _rawY.AsSpan(0, n), _rawT.AsSpan(0, n),
                _smoothX.AsSpan(0, n), _smoothY.AsSpan(0, n), _smoothingHalfWindowTicks);

            double scale = stroke.ViewScale * radius;
            double ox = cx + stroke.OffsetX * radius;
            double oy = cy + stroke.OffsetY * radius;
            for (int i = 0; i < n; i++)
            {
                _smoothX[i] = ox + _smoothX[i] * scale;
                _smoothY[i] = oy + _smoothY[i] * scale;
            }

            BuildOutput(n, now, invLifetime);
            EmitStroke(model);
        }
    }

    /// <summary>Smoothstep fade by age: stays bright, then fades out softly (same curve as before).</summary>
    public static double FadeAlpha(double ageTicks, double invLifetime)
    {
        double a = 1.0 - ageTicks * invLifetime;
        if (a <= 0)
        {
            return 0;
        }

        if (a >= 1)
        {
            return 1;
        }

        return a * a * (3 - 2 * a);
    }

    private void BuildOutput(int n, long now, double invLifetime)
    {
        _outCount = 0;
        AddOut(_smoothX[0], _smoothY[0], FadeAlpha(now - _rawT[0], invLifetime));

        for (int i = 0; i < n - 1; i++)
        {
            double x1 = _smoothX[i], y1 = _smoothY[i];
            double x2 = _smoothX[i + 1], y2 = _smoothY[i + 1];
            double segLength = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            double alpha2 = FadeAlpha(now - _rawT[i + 1], invLifetime);

            if (segLength > MaxSegmentLengthPx)
            {
                double alpha1 = FadeAlpha(now - _rawT[i], invLifetime);
                double x0, y0, x3, y3;
                if (i > 0)
                {
                    x0 = _smoothX[i - 1];
                    y0 = _smoothY[i - 1];
                }
                else
                {
                    x0 = 2 * x1 - x2;
                    y0 = 2 * y1 - y2;
                }

                if (i + 2 < n)
                {
                    x3 = _smoothX[i + 2];
                    y3 = _smoothY[i + 2];
                }
                else
                {
                    x3 = 2 * x2 - x1;
                    y3 = 2 * y2 - y1;
                }

                int pieces = Math.Min(MaxSubdivisions, (int)Math.Ceiling(segLength / MaxSegmentLengthPx));
                for (int k = 1; k < pieces; k++)
                {
                    double f = (double)k / pieces;
                    CentripetalCatmullRom(x0, y0, x1, y1, x2, y2, x3, y3, f, out double px, out double py);
                    AddOut(px, py, alpha1 + (alpha2 - alpha1) * f);
                }
            }

            AddOut(x2, y2, alpha2);
        }
    }

    /// <summary>Centripetal Catmull-Rom (Barry-Goldman form): no loops or cusps with uneven spacing.</summary>
    private static void CentripetalCatmullRom(
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3,
        double f, out double x, out double y)
    {
        const double MinKnotStep = 1e-4;
        double t0 = 0;
        double t1 = t0 + Math.Max(Math.Sqrt(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0))), MinKnotStep);
        double t2 = t1 + Math.Max(Math.Sqrt(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1))), MinKnotStep);
        double t3 = t2 + Math.Max(Math.Sqrt(Math.Sqrt((x3 - x2) * (x3 - x2) + (y3 - y2) * (y3 - y2))), MinKnotStep);
        double u = t1 + (t2 - t1) * f;

        double a1x = ((t1 - u) * x0 + (u - t0) * x1) / (t1 - t0);
        double a1y = ((t1 - u) * y0 + (u - t0) * y1) / (t1 - t0);
        double a2x = ((t2 - u) * x1 + (u - t1) * x2) / (t2 - t1);
        double a2y = ((t2 - u) * y1 + (u - t1) * y2) / (t2 - t1);
        double a3x = ((t3 - u) * x2 + (u - t2) * x3) / (t3 - t2);
        double a3y = ((t3 - u) * y2 + (u - t2) * y3) / (t3 - t2);
        double b1x = ((t2 - u) * a1x + (u - t0) * a2x) / (t2 - t0);
        double b1y = ((t2 - u) * a1y + (u - t0) * a2y) / (t2 - t0);
        double b2x = ((t3 - u) * a2x + (u - t1) * a3x) / (t3 - t1);
        double b2y = ((t3 - u) * a2y + (u - t1) * a3y) / (t3 - t1);
        x = ((t2 - u) * b1x + (u - t1) * b2x) / (t2 - t1);
        y = ((t2 - u) * b1y + (u - t1) * b2y) / (t2 - t1);
    }

    /// <summary>Emits the visible runs of the output polyline and the arrowhead.</summary>
    private void EmitStroke(SwipeRenderModel model)
    {
        int m = _outCount;
        if (m < 2)
        {
            return;
        }

        int firstVisible = -1;
        int lastVisible = -1;
        bool inRun = false;
        for (int i = 0; i < m - 1; i++)
        {
            bool visible = (_outA[i] + _outA[i + 1]) * 0.5 >= MinVisibleAlpha;
            if (!visible)
            {
                inRun = false;
                continue;
            }

            if (!inRun)
            {
                model.BeginPolyline();
                model.AddVertex(_outX[i], _outY[i], _outA[i]);
                inRun = true;
            }

            model.AddVertex(_outX[i + 1], _outY[i + 1], _outA[i + 1]);
            if (firstVisible < 0)
            {
                firstVisible = i;
            }

            lastVisible = i + 1;
        }

        if (firstVisible < 0 || lastVisible != m - 1 || _outA[m - 1] < MinVisibleAlpha || _headStyle == HeadStyle.None)
        {
            return;
        }

        if (_headStyle == HeadStyle.Dot)
        {
            model.AddDot(_outX[m - 1], _outY[m - 1], _dotSize / 2, _outA[m - 1]);
            return;
        }

        TryAddArrow(model, m, firstVisible);
    }

    /// <summary>Arrowhead aligned with the direction over the last few pixels (stable during tiny corrections).</summary>
    private void TryAddArrow(SwipeRenderModel model, int m, int firstVisible)
    {
        double arrowLength = ArrowLength;
        double directionLength = Math.Max(arrowLength * 1.5, MinDirectionLengthPx);
        double hx = _outX[m - 1];
        double hy = _outY[m - 1];

        double walked = 0;
        double bx = hx, by = hy;
        bool reached = false;
        for (int i = m - 1; i > firstVisible; i--)
        {
            double sx = _outX[i - 1] - _outX[i];
            double sy = _outY[i - 1] - _outY[i];
            double len = Math.Sqrt(sx * sx + sy * sy);
            if (walked + len >= directionLength)
            {
                double f = (directionLength - walked) / len;
                bx = _outX[i] + sx * f;
                by = _outY[i] + sy * f;
                reached = true;
                break;
            }

            walked += len;
            bx = _outX[i - 1];
            by = _outY[i - 1];
        }

        if (!reached && walked < arrowLength * 1.2)
        {
            return;
        }

        double dx = hx - bx;
        double dy = hy - by;
        double dl = Math.Sqrt(dx * dx + dy * dy);
        if (dl < 1e-6)
        {
            return;
        }

        dx /= dl;
        dy /= dl;
        double halfWidth = arrowLength * ArrowHalfWidthRatio;
        double tipX = hx + dx * arrowLength * ArrowTipAhead;
        double tipY = hy + dy * arrowLength * ArrowTipAhead;
        double baseX = tipX - dx * arrowLength;
        double baseY = tipY - dy * arrowLength;
        model.AddArrow(tipX, tipY,
            baseX - dy * halfWidth, baseY + dx * halfWidth,
            baseX + dy * halfWidth, baseY - dx * halfWidth,
            _outA[m - 1]);
    }

    /// <summary>Ring pulse at the centre after a lift: shrinks and fades over <see cref="RecenterMarkerMs"/>.</summary>
    private void AddRecenterMarker(SwipeRenderModel model, double cx, double cy, long ageTicks)
    {
        double t = Math.Clamp(MonotonicClock.TicksToMs(ageTicks) / RecenterMarkerMs, 0, 1);
        double alpha = (1 - t) * RecenterMarkerMaxAlpha;
        if (alpha < MinVisibleAlpha)
        {
            return;
        }

        double r = Math.Max(_thickness * 2.5, 8) * (1.4 - 0.6 * t);
        model.AddRing(cx, cy, r, alpha);
    }

    private void AddOut(double x, double y, double alpha)
    {
        if (_outCount == _outX.Length)
        {
            int size = _outX.Length * 2;
            Array.Resize(ref _outX, size);
            Array.Resize(ref _outY, size);
            Array.Resize(ref _outA, size);
        }

        _outX[_outCount] = x;
        _outY[_outCount] = y;
        _outA[_outCount] = alpha;
        _outCount++;
    }

    private void EnsureInputCapacity(int n)
    {
        if (_rawX.Length >= n)
        {
            return;
        }

        int size = Math.Max(n, _rawX.Length * 2);
        _rawX = new double[size];
        _rawY = new double[size];
        _rawT = new long[size];
        _smoothX = new double[size];
        _smoothY = new double[size];
    }
}
