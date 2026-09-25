using MouseSwipeVisualizer.Input;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Swipe;

/// <summary>
/// Turns raw mouse deltas into strokes of accumulated points:
/// <code>
/// raw delta -> sensitivity scale -> accumulated stroke-local position -> bounded trail history
/// </code>
/// <para>
/// Only ever used from the render thread (the WPF UI thread). Deltas reach it through
/// <see cref="MouseDeltaBuffer"/>, and each delta keeps its own input timestamp, so pause detection
/// and fading are exact even though deltas are processed in per-frame batches.
/// </para>
/// <para>
/// All points of all strokes live in a single time-ordered ring buffer. Strokes are contiguous runs
/// in that buffer, so the oldest stroke always owns the oldest points. That makes expiry O(1) per
/// point and memory strictly bounded.
/// </para>
/// </summary>
public sealed class SwipeTracker
{
    /// <summary>
    /// Mouse counts that move the head from the centre to the edge of the area at sensitivity 1.0.
    /// 800 counts is one inch on an 800 DPI mouse - a typical FPS flick.
    /// </summary>
    public const double CountsPerRadiusAtUnitySensitivity = 800.0;

    public const int DefaultPointCapacity = 16384;
    public const int MaxStrokes = 64;

    /// <summary>
    /// Raw deltas closer together than this are merged into the newest point. At 8000 Hz this caps
    /// stored points at 500/s without losing shape (the merged point is always the exact head).
    /// </summary>
    public const double MinPointIntervalMs = 2.0;

    /// <summary>Time constant for zooming back in after the gesture shrank (zooming out is instant).</summary>
    public const double ZoomRelaxMs = 250.0;

    public const double MinViewScale = 0.005;

    /// <summary>Time constant of the leaky speed integrator used for lift detection.</summary>
    public const double SpeedTauMs = 15.0;

    /// <summary>
    /// A gap counts as a lift only if the speed right before it was still at least this fraction of
    /// the stroke's peak speed. A hand that stops naturally decelerates first; a sensor that loses
    /// tracking because the mouse left the pad cuts out mid-motion. Relative, so DPI-independent.
    /// </summary>
    public const double LiftSpeedRatio = 0.35;

    /// <summary>Ignore lift detection for tiny twitches (counts per second, peak).</summary>
    public const double LiftMinPeakCountsPerSecond = 300.0;

    private readonly RingBuffer<SwipePoint> _points;
    private readonly List<SwipeStroke> _strokes = new(MaxStrokes);
    private readonly Stack<SwipeStroke> _pool = new(MaxStrokes);

    private SwipeStroke? _active;
    private double _headX;
    private double _headY;
    private long _lastInputTimestamp;
    private long _lastAppendTimestamp;
    private long _lastUpdateTimestamp;
    private int _nextStrokeId;

    private double _unitsPerCount = 1.0 / CountsPerRadiusAtUnitySensitivity;
    private long _breakTicks = MonotonicClock.MsToTicks(120);
    private long _lifetimeTicks = MonotonicClock.MsToTicks(500);
    private readonly long _minPointIntervalTicks = MonotonicClock.MsToTicks(MinPointIntervalMs);
    private bool _liftDetection = true;
    private long _liftGapTicks = MonotonicClock.MsToTicks(40);

    // Leaky integrator of |delta| in counts/s: steady-state equals the true speed regardless of
    // polling rate, and it decays to ~0 when the hand decelerates to a stop.
    private double _speed;
    private double _peakSpeed;

    public SwipeTracker(int pointCapacity = DefaultPointCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pointCapacity, 16);
        _points = new RingBuffer<SwipePoint>(pointCapacity);
        for (int i = 0; i < MaxStrokes; i++)
        {
            _pool.Push(new SwipeStroke());
        }
    }

    public int PointCount => _points.Count;

    public int PointCapacity => _points.Capacity;

    public int StrokeCount => _strokes.Count;

    public long TotalDeltas { get; private set; }

    public long TotalStrokes => _nextStrokeId;

    /// <summary>Head of the current gesture in stroke-local units (debug display).</summary>
    public double HeadX => _active != null ? _headX : 0;

    public double HeadY => _active != null ? _headY : 0;

    public SwipeStroke? ActiveStroke => _active;

    public double LifetimeMs => MonotonicClock.TicksToMs(_lifetimeTicks);

    public long LifetimeTicks => _lifetimeTicks;

    public SwipeStroke GetStroke(int index) => _strokes[index];

    /// <summary>Point by age order: 0 = oldest point of the oldest stroke.</summary>
    public ref readonly SwipePoint GetPoint(int index) => ref _points[index];

    /// <summary>Why the most recent stroke started (debug display).</summary>
    public StrokeBreakReason LastBreakReason { get; private set; }

    public long LiftCount { get; private set; }

    /// <summary>When the last lift was detected (0 = never); drives the re-center marker.</summary>
    public long LastLiftTimestamp { get; private set; }

    private StrokeBreakReason _pendingBreakReason = StrokeBreakReason.First;

    /// <summary>Current smoothed speed in raw counts per second (debug display).</summary>
    public double SpeedCountsPerSecond => _speed;

    public void Configure(double sensitivityScale, double swipeBreakMs, double trailLifetimeMs,
        bool liftDetection = true, double liftGapMs = 40)
    {
        _unitsPerCount = Math.Max(sensitivityScale, 0.0001) / CountsPerRadiusAtUnitySensitivity;
        _breakTicks = MonotonicClock.MsToTicks(Math.Max(swipeBreakMs, 1));
        _lifetimeTicks = MonotonicClock.MsToTicks(Math.Max(trailLifetimeMs, 1));
        _liftDetection = liftDetection;
        _liftGapTicks = MonotonicClock.MsToTicks(Math.Max(liftGapMs, 1));
    }

    public void AddDelta(in MouseDelta delta)
    {
        if (delta.Dx == 0 && delta.Dy == 0)
        {
            return;
        }

        long gap = delta.Timestamp - _lastInputTimestamp;

        // A pause longer than the break time means the gesture ended: the next movement is a new
        // gesture and starts from the centre, not from the end of the old trail.
        // A shorter gap right after still-fast motion means the sensor lost tracking mid-swipe,
        // i.e. the mouse was lifted off the pad to re-center - also a new gesture.
        if (_active == null)
        {
            // Either the very first movement or the stroke was already ended (lift seen by Update).
            BeginStroke(delta.Timestamp, _pendingBreakReason);
        }
        else if (gap > _breakTicks)
        {
            BeginStroke(delta.Timestamp, StrokeBreakReason.Pause);
        }
        else if (_liftDetection && gap > _liftGapTicks && IsAbruptStop())
        {
            // Lift and put-down both happened within one batch of deltas.
            RegisterLift(_lastInputTimestamp + _liftGapTicks);
            BeginStroke(delta.Timestamp, StrokeBreakReason.Lift);
        }

        UpdateSpeed(delta, gap);

        SwipeStroke stroke = _active!;
        _headX += delta.Dx * _unitsPerCount;
        _headY += delta.Dy * _unitsPerCount;

        var point = new SwipePoint(_headX, _headY, delta.Timestamp, stroke.Id);
        if (stroke.PointCount >= 2 && delta.Timestamp - _lastAppendTimestamp < _minPointIntervalTicks)
        {
            // Keep the newest point exactly at the head, but don't store every 0.125 ms sample.
            _points.Newest = point;
        }
        else
        {
            Append(point);
            _lastAppendTimestamp = delta.Timestamp;
        }

        stroke.LastTimestamp = delta.Timestamp;
        _lastInputTimestamp = delta.Timestamp;
        TotalDeltas++;
    }

    /// <summary>
    /// Expires old points and recomputes each stroke's view transform.
    /// </summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="halfExtentX">Usable half width of the area in units (1.0 on the shorter axis minus margins).</param>
    /// <param name="halfExtentY">Usable half height of the area in units.</param>
    public void Update(long now, double halfExtentX, double halfExtentY)
    {
        // Lift detection while the mouse is still in the air: as soon as the sensor has been silent
        // long enough after an abrupt stop, end the gesture so the next one starts at the centre.
        if (_liftDetection && _active != null && now - _lastInputTimestamp > _liftGapTicks && IsAbruptStop())
        {
            RegisterLift(now);
            _active = null;
            _pendingBreakReason = StrokeBreakReason.Lift;
        }

        while (_points.Count > 0 && now - _points.Oldest.Timestamp > _lifetimeTicks)
        {
            _points.PopFront();
            ReleaseOldestPoint();
        }

        double dtMs = _lastUpdateTimestamp == 0 ? 0 : MonotonicClock.TicksToMs(now - _lastUpdateTimestamp);
        _lastUpdateTimestamp = now;
        double relax = 1.0 - Math.Exp(-Math.Max(dtMs, 0) / ZoomRelaxMs);

        halfExtentX = Math.Max(halfExtentX, 0.05);
        halfExtentY = Math.Max(halfExtentY, 0.05);

        int cursor = 0;
        for (int s = 0; s < _strokes.Count; s++)
        {
            SwipeStroke stroke = _strokes[s];
            int end = cursor + stroke.PointCount;
            ref readonly SwipePoint first = ref _points[cursor];
            double minX = first.X, maxX = first.X, minY = first.Y, maxY = first.Y;
            for (int i = cursor + 1; i < end; i++)
            {
                ref readonly SwipePoint p = ref _points[i];
                if (p.X < minX) minX = p.X;
                else if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                else if (p.Y > maxY) maxY = p.Y;
            }

            stroke.MinX = minX;
            stroke.MaxX = maxX;
            stroke.MinY = minY;
            stroke.MaxY = maxY;
            ref readonly SwipePoint head = ref _points[end - 1];
            FitView(stroke, head.X, head.Y, halfExtentX, halfExtentY, relax);
            cursor = end;
        }
    }

    public void Clear()
    {
        _points.Clear();
        foreach (SwipeStroke stroke in _strokes)
        {
            _pool.Push(stroke);
        }

        _strokes.Clear();
        _active = null;
        _headX = _headY = 0;
        _pendingBreakReason = StrokeBreakReason.First;
    }

    /// <summary>
    /// Keeps the live part of the stroke inside the area with a uniform scale plus translation.
    /// Zooming out happens immediately (the head can never leave the area), zooming back in is
    /// eased so the view doesn't pump. Scale changes pivot around the head so the newest part of
    /// the gesture stays put on screen.
    /// </summary>
    private static void FitView(SwipeStroke stroke, double headX, double headY, double halfX, double halfY, double relax)
    {
        double width = stroke.MaxX - stroke.MinX;
        double height = stroke.MaxY - stroke.MinY;
        double fit = 1.0;
        if (width > 1e-9)
        {
            fit = Math.Min(fit, 2 * halfX / width);
        }

        if (height > 1e-9)
        {
            fit = Math.Min(fit, 2 * halfY / height);
        }

        fit = Math.Max(fit, MinViewScale);

        double oldScale = stroke.ViewScale;
        double scale = fit < oldScale ? fit : oldScale + (fit - oldScale) * relax;
        double offsetX = stroke.OffsetX + headX * (oldScale - scale);
        double offsetY = stroke.OffsetY + headY * (oldScale - scale);

        stroke.ViewScale = scale;
        stroke.OffsetX = ClampAxis(stroke.MinX, stroke.MaxX, scale, offsetX, halfX);
        stroke.OffsetY = ClampAxis(stroke.MinY, stroke.MaxY, scale, offsetY, halfY);
    }

    /// <summary>Minimal translation that brings [min,max]*scale+offset inside [-half, half].</summary>
    private static double ClampAxis(double min, double max, double scale, double offset, double half)
    {
        double lo = min * scale + offset;
        double hi = max * scale + offset;
        if (hi - lo >= 2 * half)
        {
            return -(min + max) * 0.5 * scale;
        }

        if (lo < -half)
        {
            return offset + (-half - lo);
        }

        if (hi > half)
        {
            return offset - (hi - half);
        }

        return offset;
    }

    private void RegisterLift(long timestamp)
    {
        LiftCount++;
        LastLiftTimestamp = timestamp;
        _speed = 0;
        _peakSpeed = 0;
    }

    private bool IsAbruptStop() =>
        _peakSpeed >= LiftMinPeakCountsPerSecond && _speed >= _peakSpeed * LiftSpeedRatio;

    private void UpdateSpeed(in MouseDelta delta, long gapTicks)
    {
        double dtMs = Math.Max(MonotonicClock.TicksToMs(gapTicks), 0);
        double magnitude = Math.Sqrt((double)delta.Dx * delta.Dx + (double)delta.Dy * delta.Dy);
        _speed = _speed * Math.Exp(-dtMs / SpeedTauMs) + magnitude * (1000.0 / SpeedTauMs);
        if (_speed > _peakSpeed)
        {
            _peakSpeed = _speed;
        }
    }

    private void BeginStroke(long timestamp, StrokeBreakReason reason)
    {
        LastBreakReason = reason;
        _pendingBreakReason = StrokeBreakReason.Pause;
        _speed = 0;
        _peakSpeed = 0;

        if (_strokes.Count == MaxStrokes)
        {
            DropOldestStroke();
        }

        SwipeStroke stroke = _pool.Count > 0 ? _pool.Pop() : new SwipeStroke();
        stroke.Reset(++_nextStrokeId, timestamp);
        _strokes.Add(stroke);
        _active = stroke;
        _headX = 0;
        _headY = 0;

        // Explicit origin point so the stroke visibly starts at the centre.
        Append(new SwipePoint(0, 0, timestamp, stroke.Id));
        _lastAppendTimestamp = timestamp;
    }

    private void Append(in SwipePoint point)
    {
        if (_points.PushBack(point))
        {
            // Ring full: the oldest point was overwritten, and it always belongs to the oldest stroke.
            ReleaseOldestPoint();
        }

        _active!.PointCount++;
    }

    private void ReleaseOldestPoint()
    {
        SwipeStroke oldest = _strokes[0];
        oldest.PointCount--;
        if (oldest.PointCount <= 0)
        {
            _strokes.RemoveAt(0);
            if (ReferenceEquals(oldest, _active))
            {
                _active = null;
            }

            _pool.Push(oldest);
        }
    }

    private void DropOldestStroke()
    {
        SwipeStroke oldest = _strokes[0];
        for (int i = 0; i < oldest.PointCount; i++)
        {
            _points.PopFront();
        }

        oldest.PointCount = 0;
        _strokes.RemoveAt(0);
        if (ReferenceEquals(oldest, _active))
        {
            _active = null;
        }

        _pool.Push(oldest);
    }
}
