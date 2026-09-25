namespace MouseSwipeVisualizer.Input;

/// <summary>
/// Bounded single-producer / single-consumer hand-off between the Raw Input thread (1000-8000 Hz)
/// and the render loop (60-240 Hz).
/// <para>
/// Two preallocated arrays are swapped on <see cref="Drain"/>, so neither side allocates. If the
/// consumer falls behind and the buffer fills up, new deltas are merged into the newest queued
/// entry instead of being dropped: temporal resolution is lost but the total movement (and thus the
/// shape of the gesture) is preserved.
/// </para>
/// <para>
/// The consumer can go idle when nothing is visible. The next <see cref="Push"/> then raises
/// <see cref="WakeRequested"/> exactly once, which keeps the app at ~0% CPU while the mouse is still.
/// </para>
/// </summary>
public sealed class MouseDeltaBuffer
{
    public const int DefaultCapacity = 8192;

    private readonly object _gate = new();
    private MouseDelta[] _front;
    private MouseDelta[] _back;
    private int _count;
    private int _consumerIdle;
    private long _coalescedCount;
    private MouseDelta _lastDelta;

    public MouseDeltaBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _front = new MouseDelta[capacity];
        _back = new MouseDelta[capacity];
    }

    /// <summary>Raised on the producer (input) thread when the idle consumer should resume.</summary>
    public event Action? WakeRequested;

    public int Capacity => _front.Length;

    /// <summary>Number of deltas merged because the buffer was full (diagnostics).</summary>
    public long CoalescedCount => Interlocked.Read(ref _coalescedCount);

    public MouseDelta LastDelta
    {
        get
        {
            lock (_gate)
            {
                return _lastDelta;
            }
        }
    }

    /// <summary>Producer side. Signature matches <see cref="RawMouseInput.DeltaReceived"/>.</summary>
    public void Push(MouseDelta delta)
    {
        lock (_gate)
        {
            if (_count < _front.Length)
            {
                _front[_count++] = delta;
            }
            else
            {
                ref MouseDelta last = ref _front[_count - 1];
                last = new MouseDelta(last.Dx + delta.Dx, last.Dy + delta.Dy, delta.Timestamp);
                _coalescedCount++;
            }

            _lastDelta = delta;
        }

        if (Volatile.Read(ref _consumerIdle) == 1 && Interlocked.CompareExchange(ref _consumerIdle, 0, 1) == 1)
        {
            WakeRequested?.Invoke();
        }
    }

    /// <summary>
    /// Consumer side. Returns every delta queued since the previous call, oldest first. The returned
    /// span is valid until the next call to <see cref="Drain"/>.
    /// </summary>
    public ReadOnlySpan<MouseDelta> Drain()
    {
        MouseDelta[] drained;
        int count;
        lock (_gate)
        {
            drained = _front;
            count = _count;
            _front = _back;
            _back = drained;
            _count = 0;
        }

        return new ReadOnlySpan<MouseDelta>(drained, 0, count);
    }

    /// <summary>
    /// Marks the consumer idle. Returns false (and stays awake) if deltas arrived in the meantime, so
    /// a delta pushed during the transition can never be stranded.
    /// </summary>
    public bool TryEnterIdle()
    {
        Volatile.Write(ref _consumerIdle, 1);
        lock (_gate)
        {
            if (_count > 0)
            {
                Volatile.Write(ref _consumerIdle, 0);
                return false;
            }
        }

        return true;
    }

    public void ExitIdle() => Volatile.Write(ref _consumerIdle, 0);
}
