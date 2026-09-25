using System.Diagnostics;

namespace MouseSwipeVisualizer.Utilities;

/// <summary>
/// All timing uses <see cref="Stopwatch.GetTimestamp"/> (QueryPerformanceCounter): monotonic,
/// sub-microsecond and identical across threads, so timestamps taken on the input thread can be
/// compared directly with the render thread's frame time.
/// </summary>
public static class MonotonicClock
{
    public static long Now => Stopwatch.GetTimestamp();

    public static long MsToTicks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);

    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
