using System.IO;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Rendering;

/// <summary>
/// Renders a <see cref="SwipeRenderModel"/> into a 32-bit BGRA frame (0xAARRGGBB per pixel,
/// top-down, stride = width * 4) entirely in managed memory: no HWND, no WPF, no screen capture.
/// This is what the virtual camera and the preview both show.
/// <para>
/// Shapes are rasterized from exact distances: a trail segment is a capsule (distance to the line
/// segment), the arrowhead a triangle, the re-center marker a ring. Per pixel the maximum coverage
/// over all shapes is kept (a distance-field union), so overlapping segments never double-blend,
/// and round joins/caps come for free.
/// </para>
/// <para>
/// Chroma-safe mode: the trail is opaque (fade = colour ramp towards the outline colour plus a
/// thickness taper) and the outline is binary (pixel centre inside or not). Any pixel with trail
/// coverage lies at least <see cref="SwipeRenderModel.OutlineWidth"/> inside the outline, so
/// anti-aliasing only ever blends trail with outline and the key colour is never mixed.
/// </para>
/// <para>
/// NV12 safety: cameras usually deliver NV12, where each 2x2 pixel block shares one chroma sample.
/// A block containing both key-coloured and outline pixels would get an averaged chroma, i.e. a
/// tinted fringe after keying. In chroma-safe mode every 2x2 block (aligned to even coordinates, as
/// NV12 is) that contains any shape pixel is therefore filled completely: key colour and swipe never
/// share a chroma block.
/// </para>
/// <para>
/// Hard (binary, 2x2) edges are only used where the swipe or a panel edge actually touches the key
/// colour. On a panel, the mouse box or a background picture everything is anti-aliased.
/// </para>
/// All buffers are persistent; rendering a frame does not allocate.
/// </summary>
public sealed partial class SoftwareRasterizer
{
    private const double ChromaSafeMinWidthRatio = 0.3;
    private const double NormalOutlineMaxAlpha = 0.55;

    private uint[] _frame = Array.Empty<uint>();
    private float[] _coreCov = Array.Empty<float>();
    private float[] _coreA = Array.Empty<float>();
    private float[] _outCov = Array.Empty<float>();
    private float[] _outA = Array.Empty<float>();
    private uint[] _coreColor = Array.Empty<uint>(); // per-pixel colour override (head dot), 0 = trail colour

    // Static layer = background + frame panel + keyboard. Cached: the panel is re-rendered only when
    // the style/size changes, the keys only when a key goes down/up.
    private uint[] _base = Array.Empty<uint>();
    private uint[] _static = Array.Empty<uint>();
    private (int W, int H, long Style, uint Background, bool ChromaSafe) _baseKey = (-1, -1, -1, 0, false);
    private (int W, int H, long Style, long Keys, uint Background, bool ChromaSafe) _staticKey = (-1, -1, -1, -1, 0, false);
    private readonly RectD[] _keyRects = new RectD[Input.KeyboardLayout.Keys.Count];
    private readonly GlyphCache _glyphs = new();
    private int _width;
    private int _height;
    private int _minX, _minY, _maxX, _maxY; // touched area of the coverage buffers (inclusive)
    private int _clipX0, _clipY0, _clipX1, _clipY1; // where swipe shapes may draw (inclusive)

    // Current frame style.
    private double _thickness;
    private double _outlineWidth;
    private bool _outline;
    private bool _chromaSafe;   // opaque colour-ramp look (chroma-safe setting)
    private bool _hardEdges;    // binary swipe edges + 2x2 blocks: only when the swipe is drawn on the key colour
    private bool _edgeSafe;     // static edges against the key colour are 2x2-aligned

    // Background picture (blurred, dimmed) and a more blurred copy behind glass. Loaded on style change only.
    private bool _imageActive;
    private bool _imageFillsCanvas; // no frame: the picture replaces the key colour everywhere
    private uint[] _image = Array.Empty<uint>();
    private uint[] _frosted = Array.Empty<uint>();
    private uint[] _scratch = Array.Empty<uint>();
    private uint[] _areaImage = Array.Empty<uint>();
    private uint[] _areaFrost = Array.Empty<uint>();
    private (string Path, DateTime Stamp, int X, int Y, int W, int H, int CW, int CH, double Blur, double Dim, double Frost,
        bool Adapt) _imageKey;
    private string? _imageFailure;
    private uint _glassTint = 0xFFFFFFFFu;

    // Crossfade between pictures (new Spotify cover): the previous static layer blends into the new one.
    private uint[] _fadeFrom = Array.Empty<uint>();
    private string? _imageIdentity;
    private bool _fading;
    private long _fadeStart;
    private long _fadeTicks;
    private double _fadeT;

    // Colours from the picture/cover and the effective colours of this frame (fade with the picture).
    private CoverPalette _palette;
    private OverlayStyle? _drawStyle;
    private uint _trailArgb, _dotArgb, _lastTrail, _lastDot, _trailFrom, _dotFrom;
    private uint _outlineArgb, _lastOutline, _outlineFrom;
    private double _swipeBackgroundLuminance = -1; // relative luminance under the mouse area, -1 = unknown

    public int Width => _width;

    public int Height => _height;

    /// <summary>The last rendered frame (BGRA, top-down, stride = Width).</summary>
    public ReadOnlySpan<uint> Pixels => _frame.AsSpan(0, _width * _height);

    /// <summary>Pixels whose colour was computed from shapes in the last frame (diagnostics).</summary>
    public int LastShadedPixels { get; private set; }

    /// <summary>Accent colour taken from the current picture/cover, if it has one (diagnostics, self-test).</summary>
    public uint? CoverAccent => _imageActive && _palette.HasAccent ? _palette.Accent : null;

    /// <summary>The current picture/cover is light overall.</summary>
    public bool CoverIsLight => _imageActive && _palette.Light;

    /// <summary>True while the picture crossfade runs: the caller must keep rendering frames until it ends.</summary>
    public bool IsAnimating => _fading || _npAnimating;

    /// <summary>How often the background + frame layer was rebuilt (diagnostics; style/size changes only).</summary>
    public int BaseLayerBuilds { get; private set; }

    /// <summary>How often the keyboard layer was redrawn (diagnostics; key changes only).</summary>
    public int KeyboardLayerBuilds { get; private set; }

    public void Render(SwipeRenderModel model)
    {
        EnsureSize(model.Width, model.Height);
        _chromaSafe = model.ChromaSafe;
        PrepareStaticLayer(model);
        ComposeStatic(model.Now);
        UpdateSwipeColors(model);
        DrawNowPlaying(model);
        _hardEdges = _chromaSafe && !_imageFillsCanvas && model.Style is not ({ FrameEnabled: true } or { SwipeBoxEnabled: true });
        LastShadedPixels = 0;
        if (model.IsEmpty || _width == 0 || _height == 0)
        {
            return;
        }

        _thickness = model.Thickness;
        _outlineWidth = model.OutlineWidth;
        _outline = model.OutlineEnabled;
        _chromaSafe = model.ChromaSafe;
        _minX = int.MaxValue;
        _minY = int.MaxValue;
        SetClip(model);
        _maxX = -1;
        _maxY = -1;

        ReadOnlySpan<double> xs = model.X;
        ReadOnlySpan<double> ys = model.Y;
        ReadOnlySpan<double> alphas = model.Alpha;
        for (int p = 0; p < model.PolylineCount; p++)
        {
            int start = model.PolylineStart(p);
            int end = model.PolylineEnd(p);
            for (int i = start; i < end - 1; i++)
            {
                RasterSegment(xs[i], ys[i], alphas[i], xs[i + 1], ys[i + 1], alphas[i + 1]);
            }
        }

        ReadOnlySpan<double> rings = model.Rings;
        for (int i = 0; i < rings.Length; i += SwipeRenderModel.RingStride)
        {
            RasterRing(rings[i], rings[i + 1], rings[i + 2], rings[i + 3]);
        }

        ReadOnlySpan<double> dots = model.Dots;
        for (int i = 0; i < dots.Length; i += SwipeRenderModel.DotStride)
        {
            RasterDot(dots[i], dots[i + 1], dots[i + 2], dots[i + 3], _dotArgb);
        }

        ReadOnlySpan<double> arrows = model.Arrows;
        for (int i = 0; i < arrows.Length; i += SwipeRenderModel.ArrowStride)
        {
            RasterTriangle(arrows[i], arrows[i + 1], arrows[i + 2], arrows[i + 3], arrows[i + 4], arrows[i + 5], arrows[i + 6]);
        }

        Composite(model);
    }

    private void EnsureSize(int width, int height)
    {
        width = Math.Max(width, 0);
        height = Math.Max(height, 0);
        if (width == _width && height == _height)
        {
            return;
        }

        int n = width * height;
        if (_frame.Length < n)
        {
            _frame = new uint[n];
            _coreCov = new float[n];
            _coreA = new float[n];
            _outCov = new float[n];
            _outA = new float[n];
            _coreColor = new uint[n];
            _base = new uint[n];
            _static = new uint[n];
        }
        else
        {
            // Coverage buffers must be zero everywhere (invariant); a resize may expose stale data.
            Array.Clear(_coreCov);
            Array.Clear(_coreA);
            Array.Clear(_outCov);
            Array.Clear(_outA);
            Array.Clear(_coreColor);
            _fading = false; // a crossfade never survives a size change
        }

        _width = width;
        _height = height;
    }

    /// <summary>Trail half width for a given fade factor.</summary>
    private double CoreRadius(double a) =>
        _chromaSafe ? _thickness * (ChromaSafeMinWidthRatio + (1 - ChromaSafeMinWidthRatio) * a) / 2 : _thickness / 2;

    /// <summary>Whole canvas, or the inside of the mouse area box (even-aligned: see <see cref="OverlayLayout"/>).</summary>
    private void SetClip(SwipeRenderModel model)
    {
        _clipX0 = 0;
        _clipY0 = 0;
        _clipX1 = _width - 1;
        _clipY1 = _height - 1;
        RectD clip = model.Layout.SwipeClip;
        if (model.Style is { SwipeBoxEnabled: true } && clip.Width >= 0 && clip.Height >= 0 && clip != default)
        {
            _clipX0 = Math.Max(0, (int)Math.Ceiling(clip.X));
            _clipY0 = Math.Max(0, (int)Math.Ceiling(clip.Y));
            _clipX1 = Math.Min(_width - 1, (int)Math.Floor(clip.Right) - 1);
            _clipY1 = Math.Min(_height - 1, (int)Math.Floor(clip.Bottom) - 1);
        }
    }

    private bool ClipBox(double x0, double y0, double x1, double y1, out int bx0, out int by0, out int bx1, out int by1)
    {
        bx0 = Math.Max(_clipX0, (int)Math.Floor(x0));
        by0 = Math.Max(_clipY0, (int)Math.Floor(y0));
        bx1 = Math.Min(_clipX1, (int)Math.Ceiling(x1));
        by1 = Math.Min(_clipY1, (int)Math.Ceiling(y1));
        if (bx0 > bx1 || by0 > by1)
        {
            return false;
        }

        if (bx0 < _minX) _minX = bx0;
        if (by0 < _minY) _minY = by0;
        if (bx1 > _maxX) _maxX = bx1;
        if (by1 > _maxY) _maxY = by1;
        return true;
    }

    /// <summary>Records coverage of one shape at one pixel from its signed distance to the shape's core edge.</summary>
    private void Accumulate(int index, double distanceOutsideCore, double a, uint colorOverride = 0)
    {
        // Core: anti-aliased, except in chroma-safe mode without an outline (then it is the key edge).
        double core = _hardEdges && !_outline
            ? (distanceOutsideCore <= 0 ? 1 : 0)
            : Math.Clamp(0.5 - distanceOutsideCore, 0, 1);
        if (core > 0)
        {
            float c = (float)core;
            if (c > _coreCov[index] || (c == _coreCov[index] && a > _coreA[index]))
            {
                _coreCov[index] = c;
                _coreA[index] = (float)a;
                _coreColor[index] = colorOverride;
            }
        }

        if (_outline)
        {
            double d = distanceOutsideCore - _outlineWidth;
            double o = _hardEdges ? (d <= 0 ? 1 : 0) : Math.Clamp(0.5 - d, 0, 1);
            if (o > 0)
            {
                float oc = (float)o;
                if (oc > _outCov[index] || (oc == _outCov[index] && a > _outA[index]))
                {
                    _outCov[index] = oc;
                    _outA[index] = (float)a;
                }
            }
        }
    }

    private void RasterSegment(double x0, double y0, double a0, double x1, double y1, double a1)
    {
        if ((a0 + a1) * 0.5 < SwipeModelBuilder.MinVisibleAlpha)
        {
            return;
        }

        double reach = Math.Max(CoreRadius(a0), CoreRadius(a1)) + (_outline ? _outlineWidth : 0) + 1;
        if (!ClipBox(Math.Min(x0, x1) - reach, Math.Min(y0, y1) - reach, Math.Max(x0, x1) + reach, Math.Max(y0, y1) + reach,
                out int bx0, out int by0, out int bx1, out int by1))
        {
            return;
        }

        double dx = x1 - x0;
        double dy = y1 - y0;
        double len2 = dx * dx + dy * dy;
        double inv = len2 > 1e-12 ? 1.0 / len2 : 0;
        for (int py = by0; py <= by1; py++)
        {
            double cy = py + 0.5;
            int row = py * _width;
            for (int px = bx0; px <= bx1; px++)
            {
                double cx = px + 0.5;
                double u = ((cx - x0) * dx + (cy - y0) * dy) * inv;
                u = u < 0 ? 0 : (u > 1 ? 1 : u);
                double qx = x0 + dx * u - cx;
                double qy = y0 + dy * u - cy;
                double dist = Math.Sqrt(qx * qx + qy * qy);
                double a = a0 + (a1 - a0) * u;
                double outside = dist - CoreRadius(a);
                if (outside > (_outline ? _outlineWidth : 0) + 0.5)
                {
                    continue;
                }

                Accumulate(row + px, outside, a);
            }
        }
    }

    private void RasterRing(double centerX, double centerY, double radius, double a)
    {
        double half = CoreRadius(a);
        double reach = radius + half + (_outline ? _outlineWidth : 0) + 1;
        if (!ClipBox(centerX - reach, centerY - reach, centerX + reach, centerY + reach, out int bx0, out int by0, out int bx1, out int by1))
        {
            return;
        }

        for (int py = by0; py <= by1; py++)
        {
            double dy = py + 0.5 - centerY;
            int row = py * _width;
            for (int px = bx0; px <= bx1; px++)
            {
                double dx = px + 0.5 - centerX;
                double outside = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius) - half;
                if (outside > (_outline ? _outlineWidth : 0) + 0.5)
                {
                    continue;
                }

                Accumulate(row + px, outside, a);
            }
        }
    }

    private void RasterTriangle(double tx, double ty, double lx, double ly, double rx, double ry, double a)
    {
        double reach = (_outline ? _outlineWidth : 0) + 1;
        if (!ClipBox(Math.Min(tx, Math.Min(lx, rx)) - reach, Math.Min(ty, Math.Min(ly, ry)) - reach,
                Math.Max(tx, Math.Max(lx, rx)) + reach, Math.Max(ty, Math.Max(ly, ry)) + reach,
                out int bx0, out int by0, out int bx1, out int by1))
        {
            return;
        }

        // Outward unit normals (orientation independent): signed distance = max over the three edges.
        double orient = (lx - tx) * (ry - ty) - (ly - ty) * (rx - tx) >= 0 ? 1 : -1;
        EdgeNormal(tx, ty, lx, ly, orient, out double n0x, out double n0y);
        EdgeNormal(lx, ly, rx, ry, orient, out double n1x, out double n1y);
        EdgeNormal(rx, ry, tx, ty, orient, out double n2x, out double n2y);

        for (int py = by0; py <= by1; py++)
        {
            double cy = py + 0.5;
            int row = py * _width;
            for (int px = bx0; px <= bx1; px++)
            {
                double cx = px + 0.5;
                double d0 = (cx - tx) * n0x + (cy - ty) * n0y;
                double d1 = (cx - lx) * n1x + (cy - ly) * n1y;
                double d2 = (cx - rx) * n2x + (cy - ry) * n2y;
                double outside = Math.Max(d0, Math.Max(d1, d2));
                if (outside > (_outline ? _outlineWidth : 0) + 0.5)
                {
                    continue;
                }

                Accumulate(row + px, outside, a);
            }
        }
    }

    private void RasterDot(double centerX, double centerY, double radius, double a, uint color)
    {
        double reach = radius + (_outline ? _outlineWidth : 0) + 1;
        if (!ClipBox(centerX - reach, centerY - reach, centerX + reach, centerY + reach, out int bx0, out int by0, out int bx1, out int by1))
        {
            return;
        }

        for (int py = by0; py <= by1; py++)
        {
            double dy = py + 0.5 - centerY;
            int row = py * _width;
            for (int px = bx0; px <= bx1; px++)
            {
                double dx = px + 0.5 - centerX;
                double outside = Math.Sqrt(dx * dx + dy * dy) - radius;
                if (outside > (_outline ? _outlineWidth : 0) + 0.5)
                {
                    continue;
                }

                Accumulate(row + px, outside, a, color);
            }
        }
    }

    // ------------------------------------------------------------------ static layer: frame, mouse box, keyboard

    private void PrepareStaticLayer(SwipeRenderModel model)
    {
        uint background = model.BackgroundColor | 0xFF000000u;
        OverlayStyle? style = model.Style;
        var baseKey = (_width, _height, model.StyleVersion, background, _chromaSafe);
        if (baseKey != _baseKey)
        {
            int n = _width * _height;
            bool hadFrame = _staticKey.W == _width && _staticKey.H == _height; // something is on screen to fade from
            // The picture fills the frame panel (replacing its colour); without a frame, the whole image.
            bool framed = style is { FrameEnabled: true } && !model.Layout.Frame.IsEmpty;
            RectD imageArea = framed ? model.Layout.Frame : new RectD(0, 0, _width, _height);
            _imageActive = style != null && UpdateImage(style, imageArea);
            _imageFillsCanvas = _imageActive && !framed;
            string identity = _imageActive ? _imageKey.Path : string.Empty;
            if (identity != _imageIdentity)
            {
                if (hadFrame && _imageIdentity != null && style is { ImageFadeMs: > 0 })
                {
                    StartFade(model.Now, style.ImageFadeMs);
                }

                _imageIdentity = identity;
            }
            _edgeSafe = _chromaSafe && !_imageFillsCanvas; // no key colour left: nothing to protect
            _drawStyle = ApplyPalette(style);
            style = _drawStyle;
            _glassTint = (style?.GlassTint ?? 0xFFFFFFFFu) | 0xFF000000u;
            if (_imageFillsCanvas)
            {
                _image.AsSpan(0, n).CopyTo(_base);
            }
            else
            {
                _base.AsSpan(0, n).Fill(background);
            }

            if (style is { FrameEnabled: true } && !model.Layout.Frame.IsEmpty)
            {
                // The panel edge touches the key colour: hard, 2x2-aligned in chroma-safe mode.
                FillRoundedRect(_base, model.Layout.Frame, style.FrameCornerRadius, style.FrameBackground,
                    style.FrameBorder, style.FrameBorderWidth, blockAligned: _edgeSafe, GlassLevel(style, 1), usePicture: true);
            }

            if (style is { SwipeBoxEnabled: true } && !model.Layout.SwipeBox.IsEmpty)
            {
                // Box around the mouse area; only touches the key colour when there is no panel.
                FillRoundedRect(_base, model.Layout.SwipeBox, style.SwipeBoxCornerRadius, style.SwipeBoxFill,
                    style.SwipeBoxBorder, style.SwipeBoxBorderWidth, blockAligned: _edgeSafe && !style.FrameEnabled, GlassLevel(style, 1.5),
                    usePicture: true);
            }

            _baseKey = baseKey;
            _staticKey = default;
            BaseLayerBuilds++;
        }

        long keys = style is { KeyboardEnabled: true } ? model.KeyboardVersion : 0;
        var staticKey = (_width, _height, model.StyleVersion, keys, background, _chromaSafe);
        if (staticKey == _staticKey)
        {
            return;
        }

        _base.AsSpan(0, _width * _height).CopyTo(_static);
        OverlayStyle? keyStyle = _drawStyle ?? style;
        if (keyStyle is { AutoContrast: true, KeyboardEnabled: true } && _imageActive)
        {
            // Key outlines must stand out from the picture around the keys (3:1, as for any UI outline).
            double around = MeasureLuminance(_static, model.Layout.Keyboard);
            if (around >= 0)
            {
                keyStyle = keyStyle with { KeyBorder = CoverPalette.EnsureContrast(keyStyle.KeyBorder, around, 3.0) };
            }
        }

        if (keyStyle is { KeyboardEnabled: true } && !model.Layout.Keyboard.IsEmpty)
        {
            DrawKeyboard(model, keyStyle);
        }

        _swipeBackgroundLuminance = MeasureLuminance(_static, model.Layout.Swipe);
        _staticKey = staticKey;
        KeyboardLayerBuilds++;
    }

    /// <summary>Style with the picture's colours applied (accent on the chosen parts, contrast-safe labels).</summary>
    private OverlayStyle? ApplyPalette(OverlayStyle? style)
    {
        if (style == null || !_imageActive)
        {
            return style;
        }

        OverlayStyle s = style;
        if (_palette.HasAccent)
        {
            if (s.AccentKeyBorders)
            {
                s = s with { KeyBorder = _palette.Accent };
            }

            if (s.AccentPressedKeys)
            {
                s = s with { KeyPressedFill = _palette.Accent, KeyPressedLabel = _palette.AccentText };
            }

            if (s.AccentFrameBorders)
            {
                s = s with { FrameBorder = _palette.Accent, SwipeBoxBorder = _palette.Accent };
            }
        }

        if (s is { AutoContrast: true, Glass: true } && _palette.Light)
        {
            s = s with { KeyLabel = 0xFF141418u }; // white labels would vanish on light glass
        }

        return s;
    }

    /// <summary>Stroke/dot colours for this frame: accent from the cover if enabled, crossfaded with the picture.</summary>
    private void UpdateSwipeColors(SwipeRenderModel model)
    {
        uint trail = model.TrailColor, dot = model.DotColor | 0xFF000000u, outline = model.OutlineColor | 0xFF000000u;
        if (_imageActive && _palette.HasAccent && model.Style is { AccentTrail: true })
        {
            trail = _palette.Accent;
            dot = _palette.Accent;
        }

        // Auto contrast against what is actually under the mouse area (picture, glass, box colour).
        if (_imageActive && model.Style is { AutoContrast: true } && _swipeBackgroundLuminance >= 0)
        {
            trail = CoverPalette.EnsureContrast(trail, _swipeBackgroundLuminance);
            dot = CoverPalette.EnsureContrast(dot, _swipeBackgroundLuminance);
            // A dark stroke needs a light outline to stay separated from its surroundings, and vice versa.
            double strokeLum = CoverPalette.RelativeLuminance(trail);
            if (CoverPalette.ContrastRatio(CoverPalette.RelativeLuminance(outline), strokeLum) < 2)
            {
                outline = strokeLum < 0.2 ? 0xFFF4F4F6u : 0xFF141418u;
            }
        }

        if (_fading)
        {
            trail = LerpArgb(_trailFrom, trail, _fadeT);
            dot = LerpArgb(_dotFrom, dot, _fadeT);
            outline = LerpArgb(_outlineFrom, outline, _fadeT);
        }

        _trailArgb = trail;
        _dotArgb = dot;
        _outlineArgb = outline;
        _lastTrail = trail;
        _lastDot = dot;
        _lastOutline = outline;
    }

    /// <summary>Average relative luminance of <paramref name="area"/> (sampled), or -1 if empty.</summary>
    private double MeasureLuminance(uint[] pixels, RectD area)
    {
        int x0 = Math.Clamp((int)area.X, 0, _width), x1 = Math.Clamp((int)area.Right, 0, _width);
        int y0 = Math.Clamp((int)area.Y, 0, _height), y1 = Math.Clamp((int)area.Bottom, 0, _height);
        if (x1 - x0 < 2 || y1 - y0 < 2)
        {
            return -1;
        }

        double sum = 0;
        int n = 0;
        for (int y = y0; y < y1; y += 4)
        {
            for (int x = x0; x < x1; x += 4)
            {
                sum += CoverPalette.RelativeLuminance(pixels[y * _width + x]);
                n++;
            }
        }

        return n == 0 ? -1 : sum / n;
    }

    /// <summary>Effective stroke colour of the last frame (after cover accent, contrast and fade; self-test).</summary>
    public uint EffectiveTrailColor => _trailArgb;

    /// <summary>Effective outline colour of the last frame.</summary>
    public uint EffectiveOutlineColor => _outlineArgb;

    /// <summary>Relative luminance under the mouse area (self-test), -1 unknown.</summary>
    public double SwipeBackgroundLuminance => _swipeBackgroundLuminance;

    private static uint LerpArgb(uint from, uint to, double t)
    {
        uint Channel(int shift) =>
            (uint)Math.Clamp(Math.Round(((from >> shift) & 0xFF) * (1 - t) + ((to >> shift) & 0xFF) * t), 0, 255) << shift;
        return Channel(24) | Channel(16) | Channel(8) | Channel(0);
    }

    /// <summary>Remembers what is on screen now (mid-fade: the current blend) as the start of a new crossfade.</summary>
    private void StartFade(long now, double ms)
    {
        int n = _width * _height;
        if (_fadeFrom.Length < n)
        {
            _fadeFrom = new uint[n];
        }

        if (_fading)
        {
            BlendInto(_fadeFrom, _fadeT);
        }
        else
        {
            _static.AsSpan(0, n).CopyTo(_fadeFrom);
        }

        _trailFrom = _lastTrail;
        _outlineFrom = _lastOutline;
        _dotFrom = _lastDot;
        _fading = true;
        _fadeStart = now;
        _fadeTicks = Math.Max(1, Utilities.MonotonicClock.MsToTicks(ms));
        _fadeT = 0;
    }

    /// <summary>Static layer into the frame; during a crossfade blended from the previous picture (smoothstep).</summary>
    private void ComposeStatic(long now)
    {
        int n = _width * _height;
        if (_fading)
        {
            double linear = Math.Clamp((double)(now - _fadeStart) / _fadeTicks, 0, 1);
            if (linear < 1)
            {
                _fadeT = linear * linear * (3 - 2 * linear);
                BlendInto(_frame, _fadeT);
                return;
            }

            _fading = false;
        }

        _static.AsSpan(0, n).CopyTo(_frame);
    }

    private void BlendInto(uint[] target, double t)
    {
        int n = _width * _height;
        uint a = (uint)Math.Clamp(Math.Round(t * 256), 0, 256), ia = 256 - a;
        uint[] from = _fadeFrom, to = _static;
        for (int i = 0; i < n; i++)
        {
            uint p = from[i], q = to[i];
            if (p == q)
            {
                target[i] = q;
                continue;
            }

            uint rb = ((((p & 0xFF00FFu) * ia) + ((q & 0xFF00FFu) * a)) >> 8) & 0xFF00FFu;
            uint g = ((((p >> 8) & 0xFFu) * ia) + (((q >> 8) & 0xFFu) * a)) >> 8;
            target[i] = 0xFF000000u | rb | (g << 8);
        }
    }

    /// <summary>Glass tint strength for a layer (0 = solid colours); keys are tinted more than the panel.</summary>
    private static double GlassLevel(OverlayStyle style, double layer) =>
        style.Glass ? Math.Clamp(style.GlassOpacity * layer, 0.01, 0.9) : 0;

    /// <summary>
    /// Loads/caches the background picture, cover-fitted to <paramref name="area"/> (the frame panel) and
    /// placed there in <see cref="_image"/>/<see cref="_frosted"/>. Blurring stays inside the area, so no
    /// colour from outside the frame bleeds in. False = none or failed (then the plain colours).
    /// </summary>
    private bool UpdateImage(OverlayStyle style, RectD area)
    {
        string path = style.BackgroundImage.Trim().Trim('"');
        int ax = Math.Clamp((int)Math.Floor(area.X), 0, _width), ay = Math.Clamp((int)Math.Floor(area.Y), 0, _height);
        int aw = Math.Clamp((int)Math.Ceiling(area.Right), ax, _width) - ax, ah = Math.Clamp((int)Math.Ceiling(area.Bottom), ay, _height) - ay;
        if (path.Length == 0 || aw < 2 || ah < 2)
        {
            _imageKey = default;
            _imageFailure = null;
            _palette = default;
            return false;
        }

        var key = (path, File.GetLastWriteTimeUtc(path), ax, ay, aw, ah, _width, _height, style.ImageBlur, style.ImageDim, style.GlassBlur,
            style.AutoContrast);
        if (key == _imageKey)
        {
            return _imageFailure == null;
        }

        _imageKey = key;
        int n = _width * _height, an = aw * ah;
        if (_image.Length < n)
        {
            _image = new uint[n];
            _frosted = new uint[n];
        }

        if (_areaImage.Length < an)
        {
            _areaImage = new uint[an];
            _areaFrost = new uint[an];
            _scratch = new uint[an];
        }

        if (!BackgroundImage.TryLoadCover(path, aw, ah, _areaImage, out string? error))
        {
            if (error != _imageFailure)
            {
                Logger.Warn($"Background image '{path}' could not be loaded ({error}); using the plain background.");
            }

            _imageFailure = error ?? "unknown error";
            _palette = default;
            return false;
        }

        _imageFailure = null;
        BackgroundImage.Blur(_areaImage, _scratch, aw, ah, style.ImageBlur);
        BackgroundImage.Dim(_areaImage, an, style.ImageDim);
        _palette = CoverPalette.Extract(_areaImage.AsSpan(0, an), 7, adaptToBackground: style.AutoContrast);
        _areaImage.AsSpan(0, an).CopyTo(_areaFrost);
        BackgroundImage.Blur(_areaFrost, _scratch, aw, ah, style.GlassBlur);
        for (int y = 0; y < ah; y++)
        {
            _areaImage.AsSpan(y * aw, aw).CopyTo(_image.AsSpan((ay + y) * _width + ax, aw));
            _areaFrost.AsSpan(y * aw, aw).CopyTo(_frosted.AsSpan((ay + y) * _width + ax, aw));
        }

        return true;
    }

    private void DrawKeyboard(SwipeRenderModel model, OverlayStyle style)
    {
        OverlayLayout.ComputeKeys(model.Layout.Keyboard, _keyRects, out double unit, model.Layout.KeyUnit);
        if (unit < 4)
        {
            return;
        }

        // Keys sit on the opaque panel (anti-aliased); without a panel they touch the key colour.
        bool blockAligned = _edgeSafe && !style.FrameEnabled;
        double radius = unit * 0.2;
        double border = style.Borders ? Math.Max(1, unit * 0.05) : 0;
        double glass = GlassLevel(style, 2.2);
        ReadOnlySpan<bool> pressed = model.KeyPressed;
        for (int i = 0; i < _keyRects.Length; i++)
        {
            Input.KeyDefinition key = Input.KeyboardLayout.Keys[i];
            bool down = pressed[i];
            // Pressed keys stay solid so they read clearly on glass.
            FillRoundedRect(_static, _keyRects[i], radius, down ? style.KeyPressedFill : style.KeyFill, style.KeyBorder, border, blockAligned,
                down ? 0 : glass);
            if (key.Label.Length > 0)
            {
                double factor = key.IsSymbol ? 0.42 : key.Label.Length > 1 ? 0.28 : 0.34;
                GlyphMask glyph = _glyphs.Get(key.Label, (int)Math.Round(unit * factor), key.IsSymbol);
                DrawGlyph(_static, glyph, _keyRects[i], down ? style.KeyPressedLabel : style.KeyLabel);
            }
        }
    }

    /// <summary>
    /// Rounded rectangle with border via its signed distance. Anti-aliased, or (blockAligned) decided per
    /// 2x2 block so an edge against the chroma key never shares an NV12 chroma sample with it.
    /// <paramref name="usePicture"/>: the background picture replaces <paramref name="fill"/> (frame, mouse box).
    /// <paramref name="glass"/> &gt; 0: frosted glass, i.e. the more blurred picture (or <paramref name="fill"/>
    /// without one) tinted by that amount; the border is half see-through.
    /// </summary>
    private void FillRoundedRect(uint[] target, RectD rect, double radius, uint fill, uint border, double borderWidth, bool blockAligned,
        double glass = 0, bool usePicture = false)
    {
        radius = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
        double hw = rect.Width / 2, hh = rect.Height / 2, cx = rect.CenterX, cy = rect.CenterY;
        int x0 = Math.Max(0, (int)Math.Floor(rect.X)), y0 = Math.Max(0, (int)Math.Floor(rect.Y));
        int x1 = Math.Min(_width - 1, (int)Math.Ceiling(rect.Right)), y1 = Math.Min(_height - 1, (int)Math.Ceiling(rect.Bottom));
        fill |= 0xFF000000u;
        border |= 0xFF000000u;
        bool isGlass = glass > 0;
        uint[] frosted = _frosted, picture = _image;
        bool image = _imageActive;
        bool plain = !isGlass && !(image && usePicture);
        uint tint = _glassTint;

        uint Under(int i) => !image ? fill : isGlass ? frosted[i] : usePicture ? picture[i] : fill;
        uint FillAt(int i) => isGlass ? Lerp(Under(i), tint, glass) : Under(i);
        uint BorderAt(int i) => isGlass ? Lerp(FillAt(i), border, 0.5) : border;

        double SignedDistance(double px, double py)
        {
            double qx = Math.Abs(px - cx) - (hw - radius);
            double qy = Math.Abs(py - cy) - (hh - radius);
            double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
        }

        if (blockAligned)
        {
            for (int by = y0 & ~1; by <= y1; by += 2)
            {
                for (int bx = x0 & ~1; bx <= x1; bx += 2)
                {
                    double d = SignedDistance(bx + 1, by + 1);
                    if (d > 0)
                    {
                        continue;
                    }

                    bool inside = d + borderWidth <= 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int px = bx + (k & 1), py = by + (k >> 1);
                        if (px < _width && py < _height)
                        {
                            int i = py * _width + px;
                            target[i] = inside ? FillAt(i) : BorderAt(i);
                        }
                    }
                }
            }

            return;
        }

        double interiorX = hw - borderWidth - 1, interiorY = hh - borderWidth - 1;
        for (int py = y0; py <= y1; py++)
        {
            double pyc = py + 0.5;
            double dy = Math.Abs(pyc - cy);
            int row = py * _width;
            for (int px = x0; px <= x1; px++)
            {
                double pxc = px + 0.5;
                double dx = Math.Abs(pxc - cx);
                // Fast path: well inside the straight part, no distance maths needed.
                if ((dx <= interiorX && dy <= interiorY - radius) || (dy <= interiorY && dx <= interiorX - radius))
                {
                    target[row + px] = plain ? fill : FillAt(row + px);
                    continue;
                }

                double d = SignedDistance(pxc, pyc);
                double outer = Math.Clamp(0.5 - d, 0, 1);
                if (outer <= 0)
                {
                    continue;
                }

                uint color = target[row + px];
                if (borderWidth > 0)
                {
                    color = Lerp(color, BorderAt(row + px), outer);
                    color = Lerp(color, FillAt(row + px), Math.Clamp(0.5 - (d + borderWidth), 0, 1));
                }
                else
                {
                    color = Lerp(color, FillAt(row + px), outer);
                }

                target[row + px] = color;
            }
        }
    }

    private void DrawGlyph(uint[] target, GlyphMask glyph, RectD rect, uint color)
    {
        if (glyph.Width == 0)
        {
            return;
        }

        int left = (int)Math.Round(rect.CenterX - glyph.Width / 2.0);
        int top = (int)Math.Round(rect.CenterY - glyph.Height / 2.0);
        color |= 0xFF000000u;
        for (int y = 0; y < glyph.Height; y++)
        {
            int py = top + y;
            if ((uint)py >= (uint)_height)
            {
                continue;
            }

            for (int x = 0; x < glyph.Width; x++)
            {
                int px = left + x;
                byte a = glyph.Alpha[y * glyph.Width + x];
                if (a == 0 || (uint)px >= (uint)_width)
                {
                    continue;
                }

                int i = py * _width + px;
                target[i] = Lerp(target[i], color, a / 255.0);
            }
        }
    }

    private static void EdgeNormal(double ax, double ay, double bx, double by, double orient, out double nx, out double ny)
    {
        double ex = bx - ax;
        double ey = by - ay;
        double len = Math.Sqrt(ex * ex + ey * ey);
        if (len < 1e-12)
        {
            nx = ny = 0;
            return;
        }

        // orient = sign(cross(l - t, r - t)). E.g. t=(0,0), l=(1,0), r=(0,1): orient = +1 and edge t→l
        // needs the outward normal (0,-1) = (orient*ey, -orient*ex)/len.
        nx = orient * ey / len;
        ny = -orient * ex / len;
    }

    private void Composite(SwipeRenderModel model)
    {
        if (_maxX < 0)
        {
            return;
        }

        if (_hardEdges)
        {
            DilateToChromaBlocks();
        }

        uint outlineColor = _outlineArgb | 0xFF000000u;
        uint trail = _trailArgb;
        double trailAlpha = (trail >> 24) / 255.0;
        int shaded = 0;

        for (int py = _minY; py <= _maxY; py++)
        {
            int row = py * _width;
            for (int px = _minX; px <= _maxX; px++)
            {
                int i = row + px;
                float oc = _outCov[i];
                float cc = _coreCov[i];
                if (oc <= 0 && cc <= 0)
                {
                    continue;
                }

                uint color = _frame[i]; // background, frame panel or keyboard underneath
                if (_chromaSafe)
                {
                    if (oc > 0)
                    {
                        // Binary coverage on the key colour; anti-aliased on panels/pictures.
                        color = oc >= 1 ? outlineColor : Lerp(color, outlineColor, oc);
                    }

                    if (cc > 0)
                    {
                        uint target = _coreColor[i] != 0 ? _coreColor[i] : trail | 0xFF000000u;
                        uint core = Lerp(outlineColor, target, _coreA[i]);
                        color = cc >= 1 ? core : Lerp(color, core, cc);
                    }
                }
                else
                {
                    if (oc > 0)
                    {
                        double a = _outA[i];
                        color = Lerp(color, outlineColor & 0xFF000000u, oc * NormalOutlineMaxAlpha * a * a);
                    }

                    if (cc > 0)
                    {
                        color = _coreColor[i] != 0
                            ? Lerp(color, _coreColor[i], cc * _coreA[i])
                            : Lerp(color, trail | 0xFF000000u, cc * _coreA[i] * trailAlpha);
                    }
                }

                _frame[i] = color;
                shaded++;

                // Restore the all-zero invariant for the next frame.
                _outCov[i] = 0;
                _coreCov[i] = 0;
                _outA[i] = 0;
                _coreA[i] = 0;
                _coreColor[i] = 0;
            }
        }

        LastShadedPixels = shaded;
    }

    /// <summary>
    /// Chroma-safe mode: grows the shape to whole 2x2 blocks. Uncovered pixels of a touched block get
    /// outline coverage (or, without outline, the core of the strongest pixel in the block), so the
    /// key colour never shares an NV12 chroma sample with the swipe.
    /// </summary>
    private void DilateToChromaBlocks()
    {
        int x0 = _minX & ~1;
        int y0 = _minY & ~1;
        int x1 = Math.Min(_width - 1, _maxX | 1);
        int y1 = Math.Min(_height - 1, _maxY | 1);
        for (int by = y0; by <= y1; by += 2)
        {
            int by1 = Math.Min(by + 1, _height - 1);
            for (int bx = x0; bx <= x1; bx += 2)
            {
                int bx1 = Math.Min(bx + 1, _width - 1);
                int i00 = by * _width + bx, i01 = by * _width + bx1, i10 = by1 * _width + bx, i11 = by1 * _width + bx1;
                bool any = _outCov[i00] > 0 || _outCov[i01] > 0 || _outCov[i10] > 0 || _outCov[i11] > 0
                           || _coreCov[i00] > 0 || _coreCov[i01] > 0 || _coreCov[i10] > 0 || _coreCov[i11] > 0;
                if (!any)
                {
                    continue;
                }

                if (_outline)
                {
                    FillOutline(i00);
                    FillOutline(i01);
                    FillOutline(i10);
                    FillOutline(i11);
                }
                else
                {
                    // Pick the strongest core pixel of the block and extend it (binary coverage here).
                    int best = i00;
                    if (_coreA[i01] > _coreA[best]) best = i01;
                    if (_coreA[i10] > _coreA[best]) best = i10;
                    if (_coreA[i11] > _coreA[best]) best = i11;
                    float a = _coreA[best];
                    FillCore(i00, a);
                    FillCore(i01, a);
                    FillCore(i10, a);
                    FillCore(i11, a);
                }
            }
        }

        _minX = x0;
        _minY = y0;
        _maxX = x1;
        _maxY = y1;
    }

    private void FillOutline(int i)
    {
        if (_outCov[i] < 1)
        {
            _outCov[i] = 1;
        }
    }

    private void FillCore(int i, float a)
    {
        if (_coreCov[i] < 1)
        {
            _coreCov[i] = 1;
            _coreA[i] = a;
        }
    }

    /// <summary>Per-channel linear interpolation of two opaque colours; result is opaque.</summary>
    private static uint Lerp(uint from, uint to, double t)
    {
        if (t <= 0)
        {
            return from | 0xFF000000u;
        }

        if (t >= 1)
        {
            return to | 0xFF000000u;
        }

        int w = (int)(t * 256 + 0.5);
        int iw = 256 - w;
        uint r = (uint)((((from >> 16) & 0xFF) * iw + ((to >> 16) & 0xFF) * w + 128) >> 8);
        uint g = (uint)((((from >> 8) & 0xFF) * iw + ((to >> 8) & 0xFF) * w + 128) >> 8);
        uint b = (uint)(((from & 0xFF) * iw + (to & 0xFF) * w + 128) >> 8);
        return 0xFF000000u | (Math.Min(r, 255u) << 16) | (Math.Min(g, 255u) << 8) | Math.Min(b, 255u);
    }
}
