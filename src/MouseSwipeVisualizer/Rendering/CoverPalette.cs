namespace MouseSwipeVisualizer.Rendering;

/// <summary>
/// Colours taken from the background picture / Spotify cover. Spotify's Web API returns no colours, so they
/// are extracted from the downloaded image: the most prominent saturated hue (accent) and whether the
/// picture is light or dark overall (for contrast).
/// </summary>
public readonly record struct CoverPalette(bool HasAccent, uint Accent, uint AccentText, bool Light)
{
    private const int Buckets = 36; // 10° hue buckets

    /// <summary>
    /// Samples every <paramref name="step"/>-th pixel. The accent is the colour of the strongest hue range
    /// (weighted by saturation x brightness), adjusted to stand out: brighter on dark pictures, darker on light
    /// ones (when <paramref name="adaptToBackground"/>). Mostly grey pictures have no accent.
    /// </summary>
    public static CoverPalette Extract(ReadOnlySpan<uint> pixels, int step, bool adaptToBackground)
    {
        Span<double> score = stackalloc double[Buckets];
        Span<double> sr = stackalloc double[Buckets];
        Span<double> sg = stackalloc double[Buckets];
        Span<double> sb = stackalloc double[Buckets];
        score.Clear();
        sr.Clear();
        sg.Clear();
        sb.Clear();
        double luminance = 0;
        int samples = 0;
        for (int i = 0; i < pixels.Length; i += Math.Max(1, step))
        {
            uint p = pixels[i];
            double r = ((p >> 16) & 0xFF) / 255.0, g = ((p >> 8) & 0xFF) / 255.0, b = (p & 0xFF) / 255.0;
            luminance += 0.2126 * r + 0.7152 * g + 0.0722 * b;
            samples++;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double s = max <= 0 ? 0 : (max - min) / max;
            if (s < 0.25 || max < 0.15)
            {
                continue;
            }

            int bucket = (int)(Hue(r, g, b, max, min) / (360.0 / Buckets)) % Buckets;
            double w = s * max;
            score[bucket] += w;
            sr[bucket] += r * w;
            sg[bucket] += g * w;
            sb[bucket] += b * w;
        }

        if (samples == 0)
        {
            return default;
        }

        bool light = luminance / samples > 0.55;
        int best = -1;
        double bestScore = 0;
        for (int k = 0; k < Buckets; k++)
        {
            double around = score[(k + Buckets - 1) % Buckets] + score[k] + score[(k + 1) % Buckets];
            if (around > bestScore)
            {
                bestScore = around;
                best = k;
            }
        }

        // Needs a real colour presence (about 3 % of the picture fully saturated), else it is a grey cover.
        if (best < 0 || bestScore < samples * 0.03)
        {
            return new CoverPalette(false, 0, 0, light);
        }

        double tr = 0, tg = 0, tb = 0, tw = 0;
        for (int d = -1; d <= 1; d++)
        {
            int k = (best + d + Buckets) % Buckets;
            tr += sr[k];
            tg += sg[k];
            tb += sb[k];
            tw += score[k];
        }

        ToHsv(tr / tw, tg / tw, tb / tw, out double h, out double sat, out double val);
        sat = Math.Max(sat, 0.55);
        val = adaptToBackground && light ? Math.Min(val, 0.5) : Math.Max(val, 0.9);
        uint accent = FromHsv(h, sat, val);
        return new CoverPalette(true, accent, Luminance(accent) > 0.5 ? 0xFF141418u : 0xFFFFFFFFu, light);
    }

    /// <summary>WCAG relative luminance (linear light), 0..1.</summary>
    public static double RelativeLuminance(uint c)
    {
        static double Lin(uint v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin((c >> 16) & 0xFF) + 0.7152 * Lin((c >> 8) & 0xFF) + 0.0722 * Lin(c & 0xFF);
    }

    /// <summary>WCAG contrast ratio between two relative luminances (1..21).</summary>
    public static double ContrastRatio(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);

    /// <summary>
    /// Returns <paramref name="color"/> (alpha kept) made darker on a light background or lighter on a dark one,
    /// keeping its hue, until it reaches <paramref name="minRatio"/> contrast against
    /// <paramref name="backgroundLuminance"/> (relative luminance). Unchanged if it already contrasts enough.
    /// </summary>
    public static uint EnsureContrast(uint color, double backgroundLuminance, double minRatio = 4.5)
    {
        if (ContrastRatio(RelativeLuminance(color), backgroundLuminance) >= minRatio)
        {
            return color;
        }

        uint alpha = color & 0xFF000000u;
        bool darken = backgroundLuminance > 0.18; // mid-grey or lighter: go dark
        uint target = darken ? 0xFF000000u : 0xFFFFFFFFu;
        uint best = target;
        // Move towards black/white in small steps; the first step with enough contrast keeps most of the hue.
        for (int step = 1; step <= 20; step++)
        {
            double t = step / 20.0;
            uint c = Mix(color, target, t);
            if (ContrastRatio(RelativeLuminance(c), backgroundLuminance) >= minRatio)
            {
                best = c;
                break;
            }
        }

        return alpha | (best & 0x00FFFFFFu);
    }

    private static uint Mix(uint a, uint b, double t)
    {
        uint Channel(int shift) =>
            (uint)Math.Clamp(Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t), 0, 255) << shift;
        return 0xFF000000u | Channel(16) | Channel(8) | Channel(0);
    }

    public static double Luminance(uint c) =>
        (0.2126 * ((c >> 16) & 0xFF) + 0.7152 * ((c >> 8) & 0xFF) + 0.0722 * (c & 0xFF)) / 255.0;

    private static double Hue(double r, double g, double b, double max, double min)
    {
        double d = max - min;
        if (d <= 0)
        {
            return 0;
        }

        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        h = Hue(r, g, b, max, min);
        s = max <= 0 ? 0 : (max - min) / max;
        v = max;
    }

    private static uint FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static uint B(double f) => (uint)Math.Clamp(Math.Round(f * 255), 0, 255);
        return 0xFF000000u | (B(r + m) << 16) | (B(g + m) << 8) | B(b + m);
    }
}
