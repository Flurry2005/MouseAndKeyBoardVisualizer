namespace MouseSwipeVisualizer.Swipe;

/// <summary>
/// Visual smoothing applied to a copy of the raw points at render time; the stored samples are
/// never modified, so smoothing can be tuned or replaced without touching input handling.
/// <para>
/// The filter is a zero-phase (symmetric) triangular moving average over a time window. Symmetric
/// means no lag: the smoothed line stays on top of the gesture instead of trailing behind it.
/// The window shrinks towards both ends of the stroke, so the start and the head are kept exactly
/// and the newest part stays responsive. The window is measured in time, not in point count, so
/// behaviour doesn't change with the mouse polling rate.
/// </para>
/// </summary>
public static class TrailSmoother
{
    /// <summary>Half window at strength 1.0. Short enough to keep flicks and direction changes.</summary>
    public const double MaxHalfWindowMs = 18.0;

    /// <summary>Hard cap on neighbours per side, bounding the per-frame cost.</summary>
    public const int MaxNeighbours = 12;

    public static void Smooth(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<long> t,
        Span<double> outX, Span<double> outY, long halfWindowTicks)
    {
        int n = x.Length;
        if (halfWindowTicks <= 0 || n < 3)
        {
            x.CopyTo(outX);
            y.CopyTo(outY);
            return;
        }

        for (int i = 0; i < n; i++)
        {
            int k = 0;
            int maxK = Math.Min(Math.Min(i, n - 1 - i), MaxNeighbours);
            while (k < maxK && t[i] - t[i - k - 1] <= halfWindowTicks && t[i + k + 1] - t[i] <= halfWindowTicks)
            {
                k++;
            }

            if (k == 0)
            {
                outX[i] = x[i];
                outY[i] = y[i];
                continue;
            }

            // Triangular weights (k+1-|j|) approximate a Gaussian while staying cheap and exact at k=0.
            double sx = x[i] * (k + 1);
            double sy = y[i] * (k + 1);
            double sw = k + 1;
            for (int j = 1; j <= k; j++)
            {
                double w = k + 1 - j;
                sx += (x[i - j] + x[i + j]) * w;
                sy += (y[i - j] + y[i + j]) * w;
                sw += 2 * w;
            }

            outX[i] = sx / sw;
            outY[i] = sy / sw;
        }
    }
}
