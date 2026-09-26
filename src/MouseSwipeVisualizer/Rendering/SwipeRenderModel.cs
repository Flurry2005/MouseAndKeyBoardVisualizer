namespace MouseSwipeVisualizer.Rendering;

/// <summary>
/// Renderer-independent description of one frame of the swipe visualization, in canvas pixels.
/// Produced by <see cref="SwipeModelBuilder"/> from the <see cref="Swipe.SwipeTracker"/> state and
/// consumed by <see cref="SoftwareRasterizer"/> (camera + preview). It contains no WPF types, so it
/// can be built and rendered on any thread without a window.
/// <para>
/// All arrays are grow-only and reused between frames: building a model does not allocate once the
/// arrays have reached their working size.
/// </para>
/// </summary>
public sealed class SwipeRenderModel
{
    public const int ArrowStride = 7; // tipX, tipY, leftX, leftY, rightX, rightY, alpha
    public const int RingStride = 4;  // centerX, centerY, radius, alpha
    public const int DotStride = 4;   // centerX, centerY, radius, alpha

    private double[] _x = new double[1024];
    private double[] _y = new double[1024];
    private double[] _a = new double[1024];
    private int[] _polylineStart = new int[64];
    private double[] _arrows = new double[ArrowStride * 16];
    private double[] _rings = new double[RingStride * 4];
    private double[] _dots = new double[DotStride * 8];
    private readonly bool[] _keyPressed = new bool[Input.KeyboardLayout.Keys.Count];

    // ------------------------------------------------------------------ frame + keyboard (static layer)

    /// <summary>Frame/keyboard style; null = swipe only.</summary>
    public OverlayStyle? Style { get; set; }

    public OverlayLayout Layout { get; set; }

    /// <summary>Changes whenever the style changes (the rasterizer caches the static layer by it).</summary>
    public long StyleVersion { get; set; }

    /// <summary>Changes whenever a key goes down/up.</summary>
    public long KeyboardVersion { get; set; }

    /// <summary>Pressed state per <see cref="Input.KeyboardLayout.Keys"/> entry.</summary>
    public Span<bool> KeyPressed => _keyPressed;

    /// <summary>Colour of head dots (0xAARRGGBB).</summary>
    public uint DotColor { get; set; }

    public int DotCount { get; private set; }

    public ReadOnlySpan<double> Dots => _dots.AsSpan(0, DotCount * DotStride);

    // ------------------------------------------------------------------ canvas + style

    public int Width { get; set; }

    /// <summary>What is playing (for the now-playing card); null = nothing / not connected.</summary>
    public NowPlayingInfo? NowPlaying { get; set; }

    /// <summary>Frame time (<see cref="Utilities.MonotonicClock"/> ticks); drives the picture crossfade.</summary>
    public long Now { get; set; }

    public int Height { get; set; }

    /// <summary>Trail width at full opacity, in canvas pixels.</summary>
    public double Thickness { get; set; }

    /// <summary>Width of the dark border around trail/arrow/ring, in canvas pixels.</summary>
    public double OutlineWidth { get; set; }

    public bool OutlineEnabled { get; set; }

    /// <summary>
    /// Opaque colour-ramp fade + hard-edged outline, so no pixel is blended with the background
    /// (clean chroma keying). Otherwise: alpha fade with anti-aliased edges.
    /// </summary>
    public bool ChromaSafe { get; set; }

    /// <summary>0xAARRGGBB (little-endian BGRA in memory).</summary>
    public uint TrailColor { get; set; }

    public uint OutlineColor { get; set; }

    public uint BackgroundColor { get; set; }

    // ------------------------------------------------------------------ geometry

    public int VertexCount { get; private set; }

    public int PolylineCount { get; private set; }

    public int ArrowCount { get; private set; }

    public int RingCount { get; private set; }

    public ReadOnlySpan<double> X => _x.AsSpan(0, VertexCount);

    public ReadOnlySpan<double> Y => _y.AsSpan(0, VertexCount);

    /// <summary>Per-vertex opacity/age factor in [0,1] (1 = newest).</summary>
    public ReadOnlySpan<double> Alpha => _a.AsSpan(0, VertexCount);

    public ReadOnlySpan<double> Arrows => _arrows.AsSpan(0, ArrowCount * ArrowStride);

    public ReadOnlySpan<double> Rings => _rings.AsSpan(0, RingCount * RingStride);

    /// <summary>No swipe geometry (the static frame/keyboard layer may still be drawn).</summary>
    public bool IsEmpty => VertexCount == 0 && ArrowCount == 0 && RingCount == 0 && DotCount == 0;

    /// <summary>Index of the first vertex of polyline <paramref name="index"/>.</summary>
    public int PolylineStart(int index) => _polylineStart[index];

    /// <summary>Exclusive end vertex index of polyline <paramref name="index"/>.</summary>
    public int PolylineEnd(int index) => index + 1 < PolylineCount ? _polylineStart[index + 1] : VertexCount;

    public void ClearGeometry()
    {
        VertexCount = 0;
        PolylineCount = 0;
        ArrowCount = 0;
        RingCount = 0;
        DotCount = 0;
    }

    public void BeginPolyline()
    {
        // An empty previous polyline is simply reused.
        if (PolylineCount > 0 && _polylineStart[PolylineCount - 1] == VertexCount)
        {
            return;
        }

        if (PolylineCount == _polylineStart.Length)
        {
            Array.Resize(ref _polylineStart, _polylineStart.Length * 2);
        }

        _polylineStart[PolylineCount++] = VertexCount;
    }

    public void AddVertex(double x, double y, double alpha)
    {
        if (VertexCount == _x.Length)
        {
            int size = _x.Length * 2;
            Array.Resize(ref _x, size);
            Array.Resize(ref _y, size);
            Array.Resize(ref _a, size);
        }

        _x[VertexCount] = x;
        _y[VertexCount] = y;
        _a[VertexCount] = alpha;
        VertexCount++;
    }

    public void AddArrow(double tipX, double tipY, double leftX, double leftY, double rightX, double rightY, double alpha)
    {
        EnsureCapacity(ref _arrows, (ArrowCount + 1) * ArrowStride);
        int i = ArrowCount * ArrowStride;
        _arrows[i] = tipX;
        _arrows[i + 1] = tipY;
        _arrows[i + 2] = leftX;
        _arrows[i + 3] = leftY;
        _arrows[i + 4] = rightX;
        _arrows[i + 5] = rightY;
        _arrows[i + 6] = alpha;
        ArrowCount++;
    }

    public void AddRing(double centerX, double centerY, double radius, double alpha)
    {
        EnsureCapacity(ref _rings, (RingCount + 1) * RingStride);
        int i = RingCount * RingStride;
        _rings[i] = centerX;
        _rings[i + 1] = centerY;
        _rings[i + 2] = radius;
        _rings[i + 3] = alpha;
        RingCount++;
    }

    public void AddDot(double centerX, double centerY, double radius, double alpha)
    {
        EnsureCapacity(ref _dots, (DotCount + 1) * DotStride);
        int i = DotCount * DotStride;
        _dots[i] = centerX;
        _dots[i + 1] = centerY;
        _dots[i + 2] = radius;
        _dots[i + 3] = alpha;
        DotCount++;
    }

    private static void EnsureCapacity(ref double[] array, int size)
    {
        if (array.Length < size)
        {
            Array.Resize(ref array, Math.Max(size, array.Length * 2));
        }
    }
}
