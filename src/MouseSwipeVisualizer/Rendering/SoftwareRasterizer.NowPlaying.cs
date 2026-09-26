using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Rendering;

/// <summary>
/// The now-playing card under the keyboard and mouse area: cover (square or spinning vinyl), a title/artist
/// panel, a time + visualizer panel and a progress bar. Colours follow the cover's palette ("magic colors":
/// panels DarkMuted, text LightVibrant, bars/progress Vibrant, track DarkVibrant) or a fixed tint.
/// Drawn every frame on top of the static layer while music plays (progress, bars and vinyl move).
/// </summary>
public sealed partial class SoftwareRasterizer
{
    private static readonly double[] BarPattern = { 0.7, 0.8, 0.9, 1, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.5, 1.4, 1.3, 1.2, 1.1, 1, 0.9, 0.8, 0.7 };
    private const double VinylDegreesPerSecond = 42; // like Amuse's vinyl skin
    private const uint NoColorPanel = 0xFF15171Cu, NoColorTrack = 0xFF3A3D46u;

    private uint[] _npCover = Array.Empty<uint>(), _npCoverPrev = Array.Empty<uint>();
    private int _npCoverSize, _npPrevSize;
    private string? _npCoverPath;
    private bool _npCoverLoaded, _npHasCover, _npPrevHasCover;
    private CoverSwatches _npSwatches = CoverSwatches.Default, _npSwatchesPrev = CoverSwatches.Default;
    private bool _npFading;
    private long _npFadeStart, _npFadeTicks;
    private double _vinylAngle;
    private long _vinylLastNow;
    private bool _npAnimating;

    /// <summary>Palette of the now-playing cover (self-test).</summary>
    public CoverSwatches NowPlayingSwatches => _npSwatches;

    private void DrawNowPlaying(SwipeRenderModel model)
    {
        _npAnimating = false;
        OverlayStyle? style = model.Style;
        RectD area = model.Layout.NowPlaying;
        if (style is not { NowPlayingEnabled: true } || area.Width < 60 || area.Height < 24)
        {
            return;
        }

        NowPlayingInfo? info = model.NowPlaying;
        bool playing = info is { IsPlaying: true };
        long now = model.Now;

        // Geometry (compact skin): cover | [title panel / time panel], progress bar underneath.
        double barH = Math.Max(4, Math.Round(area.Height * 0.07));
        double barGap = Math.Max(3, Math.Round(area.Height * 0.06));
        double top = area.Height - barH - barGap;
        double gap = Math.Max(3, Math.Round(top * 0.06));
        bool coverSlot = style.NowPlayingCover != NowPlayingCoverStyle.None;
        double coverSize = coverSlot ? Math.Floor(top) : 0;
        var cover = new RectD(area.X, area.Y, coverSize, coverSize);
        double panelX = area.X + (coverSlot ? coverSize + gap : 0);
        double panelH = (top - gap) / 2;
        var p1 = new RectD(panelX, area.Y, area.Right - panelX, panelH);
        var p2 = new RectD(panelX, area.Y + panelH + gap, area.Right - panelX, panelH);
        double radius = Math.Max(3, top * 0.09);

        UpdateNowPlayingCover(info?.CoverPath, (int)coverSize, style, now);
        double fadeT = 1;
        if (_npFading)
        {
            double linear = Math.Clamp((double)(now - _npFadeStart) / _npFadeTicks, 0, 1);
            fadeT = linear * linear * (3 - 2 * linear);
            _npFading = linear < 1;
        }

        CoverSwatches sw = fadeT < 1 ? CoverSwatches.Lerp(_npSwatchesPrev, _npSwatches, fadeT) : _npSwatches;
        bool magic = style.NowPlayingMagicColors && (_npHasCover || _npPrevHasCover);
        uint panel = magic ? sw.DarkMuted : NoColorPanel;
        uint text = magic ? sw.LightVibrant : 0xFFFFFFFFu;
        uint accent = magic ? sw.Vibrant : style.NowPlayingTint | 0xFF000000u;
        uint track = magic ? sw.DarkVibrant : NoColorTrack;
        double glass = GlassLevel(style, 2.2);
        bool block = _edgeSafe && !style.FrameEnabled;
        FillRoundedRect(_frame, p1, radius, panel, 0, 0, block, glass);
        FillRoundedRect(_frame, p2, radius, panel, 0, 0, block, glass);

        // Keep text, bars and progress readable on what the panels actually look like (solid or glass).
        double panelLum = MeasureLuminance(_frame, p1.Deflate(2));
        if (panelLum < 0)
        {
            panelLum = CoverPalette.RelativeLuminance(panel);
        }

        text = CoverPalette.EnsureContrast(text, panelLum, 4.5);
        accent = CoverPalette.EnsureContrast(accent, panelLum, 3.0);
        uint subText = CoverPalette.EnsureContrast(Lerp(text, panel, 0.3), panelLum, 3.0);
        if (coverSlot && coverSize >= 8)
        {
            DrawNowPlayingCover(cover, style, playing, now, fadeT, accent, panel);
        }

        double seconds = MonotonicClock.TicksToMs(now) / 1000.0;
        double pad = Math.Max(6, Math.Round(panelH * 0.24));

        // Title / artist.
        string title = info?.Title is { Length: > 0 } t ? t : "Nothing Playing";
        string artist = info == null ? "Get the music started" : info.Artist;
        DrawText(title, (int)Math.Round(panelH * 0.30), TextFont.Bold, p1.X + pad, p1.Y + panelH * 0.37, p1.Width - 2 * pad, text, seconds);
        DrawText(artist, (int)Math.Round(panelH * 0.23), TextFont.Regular, p1.X + pad, p1.Y + panelH * 0.68, p1.Width - 2 * pad, subText, seconds);

        // Time, visualizer, length.
        long duration = Math.Max(0, info?.DurationMs ?? 0);
        long position = info == null ? 0 : info.ProgressMs + (playing ? (long)MonotonicClock.TicksToMs(now - info.SnapshotTicks) : 0);
        position = Math.Clamp(position, 0, duration > 0 ? duration : Math.Max(0, position));
        int timePx = (int)Math.Round(panelH * 0.26);
        GlyphMask posGlyph = _glyphs.GetText(FormatTime(position), timePx, TextFont.Semibold);
        GlyphMask durGlyph = _glyphs.GetText(FormatTime(duration), timePx, TextFont.Semibold);
        DrawGlyphAt(posGlyph, p2.X + pad, p2.CenterY, p2.X + pad, p2.Right - pad, text, 0);
        DrawGlyphAt(durGlyph, p2.Right - pad - durGlyph.Width, p2.CenterY, p2.X + pad, p2.Right - pad, text, 0);
        DrawVisualizer(new RectD(p2.X + pad + posGlyph.Width + pad, p2.Y, p2.Width - 4 * pad - posGlyph.Width - durGlyph.Width, p2.Height),
            playing, seconds, accent);

        // Progress bar.
        var trackRect = new RectD(area.X, area.Bottom - barH, area.Width, barH);
        FillRoundedRect(_frame, trackRect, barH / 2, track, 0, 0, block, 0);
        double fraction = duration > 0 ? Math.Clamp((double)position / duration, 0, 1) : 0;
        double fillW = Math.Max(barH * 1.5, trackRect.Width * fraction);
        FillRoundedRect(_frame, new RectD(trackRect.X, trackRect.Y, fillW, barH), barH / 2, accent, 0, 0, block, 0);

        _npAnimating = playing || _npFading;
    }

    private static string FormatTime(long ms)
    {
        long total = Math.Max(0, ms) / 1000;
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60:00}:{total % 60:00}";
    }

    private void DrawVisualizer(RectD area, bool playing, double seconds, uint color)
    {
        double barW = Math.Max(2, Math.Round(area.Height * 0.07));
        double spacing = Math.Round(barW * 1.3);
        int count = Math.Min(BarPattern.Length, (int)((area.Width + spacing) / (barW + spacing)));
        if (count < 3)
        {
            return;
        }

        double total = count * barW + (count - 1) * spacing;
        double x = Math.Round(area.CenterX - total / 2);
        double maxH = area.Height * 0.62;
        long step = (long)Math.Floor(seconds / 0.7);
        double f = seconds / 0.7 - step;
        double ease = f * f * (3 - 2 * f);
        for (int i = 0; i < count; i++)
        {
            double pattern = BarPattern[(int)Math.Round(i * (BarPattern.Length - 1) / (double)Math.Max(1, count - 1))];
            double m = playing
                ? pattern * 2 * (Hash(i, step) * (1 - ease) + Hash(i, step + 1) * ease)
                : 0.3;
            double h = Math.Max(barW, Math.Clamp(m / 2.4, 0.1, 1) * maxH);
            FillRoundedRect(_frame, new RectD(x, area.CenterY - h / 2, barW, h), barW / 2, color, 0, 0, false, 0);
            x += barW + spacing;
        }
    }

    private static double Hash(int i, long step)
    {
        ulong h = (ulong)(i * 73856093) ^ (ulong)(step * 19349663L);
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        return (h % 10000) / 10000.0;
    }

    /// <summary>Loads the cover at the card's size when the song (or size) changes; crossfades from the old one.</summary>
    private void UpdateNowPlayingCover(string? path, int size, OverlayStyle style, long now)
    {
        if (_npCoverLoaded && path == _npCoverPath && size == _npCoverSize)
        {
            return;
        }

        bool fade = _npCoverLoaded && size == _npCoverSize && path != _npCoverPath && style.ImageFadeMs > 0;
        (_npCoverPrev, _npCover) = (_npCover, _npCoverPrev);
        _npPrevHasCover = _npHasCover;
        _npPrevSize = _npCoverSize;
        _npSwatchesPrev = _npSwatches;

        _npCoverLoaded = true;
        _npCoverPath = path;
        _npCoverSize = size;
        _npHasCover = false;
        if (path != null && size >= 8)
        {
            if (_npCover.Length < size * size)
            {
                _npCover = new uint[size * size];
            }

            _npHasCover = BackgroundImage.TryLoadCover(path, size, size, _npCover, out _);
        }

        _npSwatches = _npHasCover ? CoverSwatches.Extract(_npCover.AsSpan(0, size * size), 2) : CoverSwatches.Default;
        _npFading = fade;
        _npFadeStart = now;
        _npFadeTicks = Math.Max(1, MonotonicClock.MsToTicks(style.ImageFadeMs));
    }

    private uint SampleCover(uint[] image, int size, double x, double y)
    {
        x = Math.Clamp(x, 0, size - 1.001);
        y = Math.Clamp(y, 0, size - 1.001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        uint a = image[y0 * size + x0], b = image[y0 * size + x0 + 1], c = image[(y0 + 1) * size + x0], d = image[(y0 + 1) * size + x0 + 1];
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
    }

    private void DrawNowPlayingCover(RectD rect, OverlayStyle style, bool playing, long now, double fadeT, uint accent, uint panel)
    {
        int size = _npCoverSize;
        bool vinyl = style.NowPlayingCover == NowPlayingCoverStyle.Vinyl;
        if (_vinylLastNow != 0 && playing && vinyl)
        {
            _vinylAngle = (_vinylAngle + MonotonicClock.TicksToMs(now - _vinylLastNow) / 1000.0 * VinylDegreesPerSecond) % 360;
        }

        _vinylLastNow = now;
        uint placeholder = Lerp(panel, 0xFFFFFFFFu, 0.12);
        double r = size / 2.0, cx = rect.X + r, cy = rect.Y + r;
        double angle = _vinylAngle * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        double labelR = r * 0.2, holeR = r * 0.055, cornerR = Math.Max(3, size * 0.09);
        int x0 = Math.Max(0, (int)rect.X), y0 = Math.Max(0, (int)rect.Y);
        int x1 = Math.Min(_width - 1, (int)Math.Ceiling(rect.Right)), y1 = Math.Min(_height - 1, (int)Math.Ceiling(rect.Bottom));

        uint CoverAt(double u, double v)
        {
            uint current = _npHasCover ? SampleCover(_npCover, size, u, v) : placeholder;
            if (fadeT >= 1)
            {
                return current;
            }

            uint previous = _npPrevHasCover && _npPrevSize == size ? SampleCover(_npCoverPrev, size, u, v) : placeholder;
            return Lerp(previous, current, fadeT);
        }

        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                double coverage;
                uint color;
                if (vinyl)
                {
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    coverage = Math.Clamp(r - d + 0.5, 0, 1) * Math.Clamp(d - holeR + 0.5, 0, 1); // round, hollow centre
                    if (coverage <= 0)
                    {
                        continue;
                    }

                    // Rotate the cover with the record.
                    double u = cos * dx + sin * dy + r - 0.5, v = -sin * dx + cos * dy + r - 0.5;
                    color = CoverAt(u, v);
                    if (d > labelR * 1.15 && d < r - 1)
                    {
                        double groove = 0.9 + 0.1 * (0.5 + 0.5 * Math.Sin(d * 1.3));
                        color = Lerp(0xFF000000u, color, groove);
                    }

                    color = Lerp(color, accent, Math.Clamp(labelR - d + 0.5, 0, 1)); // accent centre label
                }
                else
                {
                    double qx = Math.Abs(dx) - (r - cornerR), qy = Math.Abs(dy) - (r - cornerR);
                    double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                    double sd = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - cornerR;
                    coverage = Math.Clamp(0.5 - sd, 0, 1);
                    if (coverage <= 0)
                    {
                        continue;
                    }

                    color = CoverAt(px - rect.X, py - rect.Y);
                }

                int i = py * _width + px;
                _frame[i] = Lerp(_frame[i], color, coverage);
            }
        }

        if (!_npHasCover && fadeT >= 1)
        {
            GlyphMask note = _glyphs.GetText("♪", (int)Math.Round(size * 0.4), TextFont.Symbol);
            DrawGlyphAt(note, cx - note.Width / 2.0, cy, rect.X, rect.Right, Lerp(panel, 0xFFFFFFFFu, 0.6), 0);
        }
    }

    /// <summary>Text on one line, left-aligned, vertically centred; too long → slow back-and-forth scroll.</summary>
    private void DrawText(string text, int pixelSize, TextFont font, double x, double centerY, double maxWidth, uint color, double seconds)
    {
        if (text.Length == 0 || pixelSize < 5 || maxWidth < 8)
        {
            return;
        }

        GlyphMask glyph = _glyphs.GetText(text, pixelSize, font);
        double offset = 0;
        double overflow = glyph.Width - maxWidth;
        if (overflow > 0)
        {
            const double Speed = 30, Pause = 1.5;
            double travel = overflow / Speed, cycle = 2 * (travel + Pause), t = seconds % cycle;
            offset = t < Pause ? 0 : t < Pause + travel ? (t - Pause) * Speed : t < 2 * Pause + travel ? overflow : overflow - (t - 2 * Pause - travel) * Speed;
        }

        DrawGlyphAt(glyph, x, centerY, x, x + maxWidth, color, offset);
    }

    private void DrawGlyphAt(GlyphMask glyph, double x, double centerY, double clipLeft, double clipRight, uint color, double scroll)
    {
        if (glyph.Width == 0)
        {
            return;
        }

        int left = (int)Math.Round(x - scroll);
        int top = (int)Math.Round(centerY - glyph.Height / 2.0);
        int cl = Math.Max(0, (int)Math.Floor(clipLeft)), cr = Math.Min(_width, (int)Math.Ceiling(clipRight));
        color |= 0xFF000000u;
        for (int y = 0; y < glyph.Height; y++)
        {
            int py = top + y;
            if ((uint)py >= (uint)_height)
            {
                continue;
            }

            for (int gx = 0; gx < glyph.Width; gx++)
            {
                int px = left + gx;
                byte a = glyph.Alpha[y * glyph.Width + gx];
                if (a == 0 || px < cl || px >= cr)
                {
                    continue;
                }

                int i = py * _width + px;
                _frame[i] = Lerp(_frame[i], color, a / 255.0);
            }
        }
    }
}
