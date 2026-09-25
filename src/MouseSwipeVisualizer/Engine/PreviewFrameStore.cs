namespace MouseSwipeVisualizer.Engine;

/// <summary>
/// Hands frames from the engine thread to the UI thread without tearing: the engine writes into a
/// private back buffer and swaps under a short lock; the UI copies the front buffer under the same
/// lock. Buffers are reused (no per-frame allocation after the first frame of a given size).
/// </summary>
public sealed class PreviewFrameStore : IFrameOutput
{
    private readonly object _gate = new();
    private uint[] _front = Array.Empty<uint>();
    private uint[] _back = Array.Empty<uint>();
    private int _frontWidth;
    private int _frontHeight;
    private long _frontSequence = -1;
    private volatile bool _active;
    private int _notifyPending;

    /// <summary>Raised on the engine thread after a new frame was stored (at most once until <see cref="CopyLatest"/>).</summary>
    public event Action? FrameAvailable;

    public bool IsActive => _active;

    public int RequestedWidth { get; set; }

    public int RequestedHeight { get; set; }

    public int RequestedFps { get; set; }

    public long FramesPublished { get; private set; }

    /// <summary>Set by the UI when the preview is visible (not minimized/closed).</summary>
    public void SetActive(bool active) => _active = active;

    public void Publish(ReadOnlySpan<uint> pixels, int width, int height, long sequence)
    {
        int n = width * height;
        if (_back.Length < n)
        {
            _back = new uint[n];
        }

        pixels[..n].CopyTo(_back);
        lock (_gate)
        {
            (_front, _back) = (_back, _front);
            _frontWidth = width;
            _frontHeight = height;
            _frontSequence = sequence;
        }

        FramesPublished++;
        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
        {
            FrameAvailable?.Invoke();
        }
    }

    public void Tick()
    {
    }

    /// <summary>UI side: copies the newest frame if it differs from <paramref name="lastSequence"/>.</summary>
    /// <param name="copy">Receives (pixels, width, height) while the lock is held; must copy synchronously.</param>
    public bool CopyLatest(ref long lastSequence, Action<uint[], int, int> copy)
    {
        Interlocked.Exchange(ref _notifyPending, 0);
        lock (_gate)
        {
            if (_frontSequence < 0 || _frontSequence == lastSequence)
            {
                return false;
            }

            copy(_front, _frontWidth, _frontHeight);
            lastSequence = _frontSequence;
            return true;
        }
    }
}
