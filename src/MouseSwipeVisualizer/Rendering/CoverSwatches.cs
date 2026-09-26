namespace MouseSwipeVisualizer.Rendering;

/// <summary>
/// What is playing, for the now-playing card. <see cref="ProgressMs"/> is Spotify's position at
/// <see cref="SnapshotTicks"/> (<see cref="Utilities.MonotonicClock"/>); the renderer advances it locally
/// while <see cref="IsPlaying"/>, so the progress bar moves smoothly without extra requests.
/// </summary>
public sealed record NowPlayingInfo(string Title, string Artist, bool IsPlaying, long ProgressMs, long DurationMs,
    string? CoverPath, long SnapshotTicks);

/// <summary>
/// Six colours from a cover, the way Android's Palette / node-vibrant (used by e.g. Amuse's
/// "magic colors") picks them: colours are quantized, then each swatch takes the colour closest to its
/// target saturation and lightness (weights: saturation 3, lightness 6, population 1).
/// </summary>
public readonly record struct CoverSwatches(uint Vibrant, uint DarkVibrant, uint LightVibrant, uint Muted, uint DarkMuted, uint LightMuted)
{
    /// <summary>Used when there is no cover or no colour for a swatch.</summary>
    public static readonly CoverSwatches Default = new(0xFFFFFFFFu, 0xFF535353u, 0xFFFFFFFFu, 0xFF535353u, 0xFF535353u, 0xFFB3B3B3u);

    private readonly record struct Target(double MinL, double TargetL, double MaxL, double MinS, double TargetS, double MaxS);

    private static readonly Target VibrantT = new(0.3, 0.5, 0.7, 0.35, 1, 1);
    private static readonly Target LightVibrantT = new(0.55, 0.74, 1, 0.35, 1, 1);
    private static readonly Target DarkVibrantT = new(0, 0.26, 0.45, 0.35, 1, 1);
    private static readonly Target MutedT = new(0.3, 0.5, 0.7, 0, 0.3, 0.4);
    private static readonly Target LightMutedT = new(0.55, 0.74, 1, 0, 0.3, 0.4);
    private static readonly Target DarkMutedT = new(0, 0.26, 0.45, 0, 0.3, 0.4);

    public static CoverSwatches Extract(ReadOnlySpan<uint> pixels, int step)
    {
        // Quantize to 5 bits per channel; each bucket's colour is the average of its pixels.
        const int Buckets = 32 * 32 * 32;
        var population = new int[Buckets];
        var sumR = new long[Buckets];
        var sumG = new long[Buckets];
        var sumB = new long[Buckets];
        for (int i = 0; i < pixels.Length; i += Math.Max(1, step))
        {
            uint p = pixels[i];
            int r = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
            if (r > 250 && g > 250 && b > 250)
            {
                continue; // node-vibrant's default filter drops white
            }

            int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            population[key]++;
            sumR[key] += r;
            sumG[key] += g;
            sumB[key] += b;
        }

        var colors = new List<(uint Color, int Pop, double H, double S, double L)>();
        int maxPop = 0;
        for (int k = 0; k < Buckets; k++)
        {
            int n = population[k];
            if (n == 0)
            {
                continue;
            }

            uint c = 0xFF000000u | ((uint)(sumR[k] / n) << 16) | ((uint)(sumG[k] / n) << 8) | (uint)(sumB[k] / n);
            ToHsl(c, out double h, out double s, out double l);
            colors.Add((c, n, h, s, l));
            maxPop = Math.Max(maxPop, n);
        }

        var used = new HashSet<uint>();
        uint? Pick(Target t)
        {
            double best = double.MinValue;
            uint? pick = null;
            foreach ((uint c, int pop, _, double s, double l) in colors)
            {
                if (used.Contains(c) || s < t.MinS || s > t.MaxS || l < t.MinL || l > t.MaxL)
                {
                    continue;
                }

                double score = ((1 - Math.Abs(s - t.TargetS)) * 3 + (1 - Math.Abs(l - t.TargetL)) * 6 + (double)pop / Math.Max(1, maxPop)) / 10;
                if (score > best)
                {
                    best = score;
                    pick = c;
                }
            }

            if (pick is uint chosen)
            {
                used.Add(chosen);
            }

            return pick;
        }

        uint? vibrant = Pick(VibrantT), lightVibrant = Pick(LightVibrantT), darkVibrant = Pick(DarkVibrantT);
        uint? muted = Pick(MutedT), lightMuted = Pick(LightMutedT), darkMuted = Pick(DarkMutedT);

        // Generate missing vibrant swatches from the ones found (as node-vibrant does).
        if (vibrant == null && darkVibrant is uint dv)
        {
            vibrant = WithLightness(dv, 0.5);
        }
        else if (vibrant == null && lightVibrant is uint lv)
        {
            vibrant = WithLightness(lv, 0.5);
        }

        if (darkVibrant == null && vibrant is uint v1)
        {
            darkVibrant = WithLightness(v1, 0.26);
        }

        if (lightVibrant == null && vibrant is uint v2)
        {
            lightVibrant = WithLightness(v2, 0.74);
        }

        CoverSwatches d = Default;
        return new CoverSwatches(vibrant ?? d.Vibrant, darkVibrant ?? d.DarkVibrant, lightVibrant ?? d.LightVibrant,
            muted ?? d.Muted, darkMuted ?? d.DarkMuted, lightMuted ?? d.LightMuted);
    }

    public static CoverSwatches Lerp(CoverSwatches a, CoverSwatches b, double t)
    {
        static uint L(uint x, uint y, double t)
        {
            uint C(int s) => (uint)Math.Clamp(Math.Round(((x >> s) & 0xFF) * (1 - t) + ((y >> s) & 0xFF) * t), 0, 255) << s;
            return 0xFF000000u | C(16) | C(8) | C(0);
        }

        return new CoverSwatches(L(a.Vibrant, b.Vibrant, t), L(a.DarkVibrant, b.DarkVibrant, t), L(a.LightVibrant, b.LightVibrant, t),
            L(a.Muted, b.Muted, t), L(a.DarkMuted, b.DarkMuted, t), L(a.LightMuted, b.LightMuted, t));
    }

    private static uint WithLightness(uint c, double l)
    {
        ToHsl(c, out double h, out double s, out _);
        return FromHsl(h, s, l);
    }

    public static void ToHsl(uint c, out double h, out double s, out double l)
    {
        double r = ((c >> 16) & 0xFF) / 255.0, g = ((c >> 8) & 0xFF) / 255.0, b = (c & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        l = (max + min) / 2;
        s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        if (d == 0)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60 * (((g - b) / d) % 6);
        }
        else if (max == g)
        {
            h = 60 * ((b - r) / d + 2);
        }
        else
        {
            h = 60 * ((r - g) / d + 4);
        }

        if (h < 0)
        {
            h += 360;
        }
    }

    public static uint FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
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
