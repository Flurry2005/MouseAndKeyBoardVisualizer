namespace MouseSwipeVisualizer.Engine;

/// <summary>
/// Counters and timings written by the engine thread and read (without locking) by diagnostics.
/// Individual values are atomic on x64; a snapshot may mix values of neighbouring frames.
/// </summary>
public sealed class EngineStats
{
    private const double Smoothing = 0.05;

    public long Loops;
    public long FramesRasterized;
    public long FramesSkippedUnchanged;
    public long FramesPublished;
    public int RenderWidth;
    public int RenderHeight;
    public int TargetFps;
    public int ActiveOutputs;
    public int LastShadedPixels;

    /// <summary>Exponential moving averages in milliseconds.</summary>
    public double InputAndModelMs;
    public double RasterMs;
    public double PublishMs;
    public double TotalMs;
    public double MaxTotalMs;

    /// <summary>Average managed bytes allocated per engine loop iteration (should stay ~0).</summary>
    public double AllocatedBytesPerFrame;

    /// <summary>Cumulative managed bytes allocated by the engine thread (for exact steady-state measurement).</summary>
    public long TotalAllocatedBytes;

    internal void Record(double inputMs, double rasterMs, double publishMs, double totalMs, long allocated)
    {
        TotalAllocatedBytes += allocated;
        bool first = Loops <= 1;
        InputAndModelMs = first ? inputMs : InputAndModelMs + (inputMs - InputAndModelMs) * Smoothing;
        RasterMs = first ? rasterMs : RasterMs + (rasterMs - RasterMs) * Smoothing;
        PublishMs = first ? publishMs : PublishMs + (publishMs - PublishMs) * Smoothing;
        TotalMs = first ? totalMs : TotalMs + (totalMs - TotalMs) * Smoothing;
        AllocatedBytesPerFrame = first ? allocated : AllocatedBytesPerFrame + (allocated - AllocatedBytesPerFrame) * Smoothing;
        if (totalMs > MaxTotalMs)
        {
            MaxTotalMs = totalMs;
        }
    }
}
