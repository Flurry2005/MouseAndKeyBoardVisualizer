namespace MouseSwipeVisualizer.Swipe;

/// <summary>
/// One continuous gesture: all movement between two pauses longer than the swipe-break time.
/// <para>
/// The stroke owns a similarity transform (uniform <see cref="ViewScale"/> + translation) that maps
/// its local points into the visualizer area: display = local * ViewScale + Offset. Because it is a
/// uniform scale plus translation, direction, curvature and proportions are always preserved -
/// large flicks are zoomed out and panned instead of being clipped.
/// </para>
/// Instances are pooled by <see cref="SwipeTracker"/>, so fields are mutable and reset on reuse.
/// </summary>
public sealed class SwipeStroke
{
    public int Id { get; private set; }

    /// <summary>Number of this stroke's points currently stored in the tracker's ring buffer.</summary>
    public int PointCount { get; internal set; }

    public long StartTimestamp { get; private set; }

    public long LastTimestamp { get; internal set; }

    public double ViewScale { get; internal set; } = 1.0;

    public double OffsetX { get; internal set; }

    public double OffsetY { get; internal set; }

    /// <summary>Bounding box of the live (not yet expired) points, refreshed every frame.</summary>
    public double MinX { get; internal set; }

    public double MinY { get; internal set; }

    public double MaxX { get; internal set; }

    public double MaxY { get; internal set; }

    internal void Reset(int id, long timestamp)
    {
        Id = id;
        PointCount = 0;
        StartTimestamp = timestamp;
        LastTimestamp = timestamp;
        ViewScale = 1.0;
        OffsetX = 0;
        OffsetY = 0;
        MinX = MinY = MaxX = MaxY = 0;
    }
}
