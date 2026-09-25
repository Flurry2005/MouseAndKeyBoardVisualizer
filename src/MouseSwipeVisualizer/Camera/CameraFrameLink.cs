using System.IO;
using System.IO.MemoryMappedFiles;
using MouseSwipeVisualizer.Engine;
using MouseSwipeVisualizer.Utilities;
using P = MouseSwipeVisualizer.Camera.SharedFrameProtocol;

namespace MouseSwipeVisualizer.Camera;

/// <summary>Snapshot of the camera side of the shared memory (diagnostics).</summary>
public readonly record struct CameraLinkState(
    bool Connected,
    bool ConsumerActive,
    int RequestedWidth,
    int RequestedHeight,
    double RequestedFps,
    uint RequestedSubtype,
    long FramesProduced,
    long FramesDelivered,
    long FramesRepeated,
    long FramesFallback,
    long FramesDroppedByProducer,
    long StreamStarts,
    DateTime? LastConsumerStartUtc,
    int SourceProcessId,
    int LastErrorHr,
    string? LastProblem,
    int HeaderConsumerActive = 0,
    long ConsumerHeartbeat = 0,
    long ProducerHeartbeat = 0,
    int ConvertMicros = 0,
    int DeliverMicros = 0);

/// <summary>
/// Producer end of the frame transport to the virtual camera media source (see SharedFrameProtocol.h):
/// <code>
/// SwipeEngine → CameraFrameLink → Global\ shared memory (3 seqlock slots) → media source → IMFSample
/// </code>
/// The media source creates the mapping when a consumer (e.g. Medal) starts streaming; this class
/// polls for it at a low rate and only becomes <see cref="IsActive"/> while the camera's heartbeat
/// advances. So with no consumer, no 60 fps rendering happens at all.
/// <para>
/// All members except <see cref="GetState"/> and <see cref="Enabled"/> are called on the engine thread.
/// </para>
/// </summary>
public sealed unsafe class CameraFrameLink : IFrameOutput, IDisposable
{
    private const int OpenRetryMs = 250;
    private const int ConsumerTimeoutMs = 1000;
    private const int CloseAfterIdleMs = 5000;

    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private byte* _base;
    private long _nextOpenAttempt;
    private long _lastConsumerHeartbeat;
    private long _lastHeartbeatChange;
    private long _inactiveSince;
    private long _framesProduced;
    private volatile bool _enabled = true;
    private volatile bool _active;
    private volatile int _requestedWidth;
    private volatile int _requestedHeight;
    private volatile int _requestedFps;
    private volatile string? _lastProblem;
    private uint _backgroundColor = 0xFF00FF00;
    private bool _disposed;

    /// <summary>False in OBS output mode: the link closes and the camera shows its fallback frame.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public bool IsActive => _active;

    public int RequestedWidth => _requestedWidth;

    public int RequestedHeight => _requestedHeight;

    public int RequestedFps => _requestedFps;

    /// <summary>Background colour used by the camera for fallback frames (app not running).</summary>
    public uint BackgroundColor
    {
        get => _backgroundColor;
        set => _backgroundColor = value;
    }

    /// <summary>Raised on the engine thread when a consumer starts/stops streaming.</summary>
    public event Action<bool>? ConsumerActiveChanged;

    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (!_enabled)
        {
            SetActive(false, now);
            Close();
            return;
        }

        if (_base == null)
        {
            if (now < _nextOpenAttempt)
            {
                return;
            }

            _nextOpenAttempt = now + OpenRetryMs;
            if (!TryOpen())
            {
                return;
            }
        }

        if (!HeaderValid())
        {
            SetActive(false, now);
            return;
        }

        // Producer liveness for the camera's "app not running" fallback.
        Interlocked.Increment(ref *(long*)(_base + P.Header.ProducerHeartbeat));
        *(int*)(_base + P.Header.ProducerProcessId) = Environment.ProcessId;
        *(uint*)(_base + P.Header.BackgroundColor) = _backgroundColor | 0xFF000000u;

        long heartbeat = Volatile.Read(ref *(long*)(_base + P.Header.ConsumerHeartbeat));
        if (heartbeat != _lastConsumerHeartbeat)
        {
            _lastConsumerHeartbeat = heartbeat;
            _lastHeartbeatChange = now;
        }

        bool active = _lastHeartbeatChange != 0 && now - _lastHeartbeatChange <= ConsumerTimeoutMs
                      && Volatile.Read(ref *(int*)(_base + P.Header.ConsumerActive)) != 0;
        if (active)
        {
            ReadRequestedFormat();
        }

        SetActive(active, now);

        // Release the mapping (24 MiB) a while after the last consumer went away; polling resumes.
        if (!active && _inactiveSince != 0 && now - _inactiveSince > CloseAfterIdleMs)
        {
            Close();
        }
    }

    private void SetActive(bool active, long now)
    {
        if (active)
        {
            _inactiveSince = 0;
        }
        else if (_inactiveSince == 0)
        {
            _inactiveSince = now;
        }

        if (_active == active)
        {
            return;
        }

        _active = active;
        Logger.Info(active
            ? $"Virtual camera consumer started: {_requestedWidth}x{_requestedHeight} @ {_requestedFps} fps."
            : "Virtual camera consumer stopped; frame production paused.");
        ConsumerActiveChanged?.Invoke(active);
    }

    private void ReadRequestedFormat()
    {
        uint width = *(uint*)(_base + P.Header.RequestedWidth);
        uint height = *(uint*)(_base + P.Header.RequestedHeight);
        uint numerator = *(uint*)(_base + P.Header.RequestedFpsNumerator);
        uint denominator = *(uint*)(_base + P.Header.RequestedFpsDenominator);

        // Untrusted: accept only what a valid slot can hold.
        bool valid = width is > 0 and <= P.MaxWidth && height is > 0 and <= P.MaxHeight && (width & 1) == 0 && (height & 1) == 0;
        _requestedWidth = valid ? (int)width : 0;
        _requestedHeight = valid ? (int)height : 0;
        _requestedFps = denominator != 0 && numerator != 0 ? (int)Math.Clamp(Math.Round((double)numerator / denominator), 1, 240) : 0;
    }

    public void Publish(ReadOnlySpan<uint> pixels, int width, int height, long sequence)
    {
        if (_base == null || !_active || width <= 0 || height <= 0 || width > P.MaxWidth || height > P.MaxHeight)
        {
            return;
        }

        long size = (long)width * height * 4;
        if (size > P.SlotCapacity || pixels.Length < width * height)
        {
            return;
        }

        int latest = Volatile.Read(ref *(int*)(_base + P.Header.LatestSlot));
        int slot = latest is >= 0 and < P.SlotCount ? (latest + 1) % P.SlotCount : 0;
        byte* descriptor = _base + P.SlotDescriptorOffset(slot);
        long* seqLock = (long*)(descriptor + P.Slot.SeqLock);

        // Seqlock: odd while writing, even (and larger) when done. A reader that sees the same even
        // value before and after its copy got a consistent frame.
        long seq = Volatile.Read(ref *seqLock);
        if ((seq & 1) != 0)
        {
            seq++; // a previous writer died mid-write; normalise
        }

        Interlocked.Exchange(ref *seqLock, seq + 1);
        long dataOffset = P.SlotDataOffset(slot);
        *(long*)(descriptor + P.Slot.FrameSequence) = sequence;
        *(uint*)(descriptor + P.Slot.Width) = (uint)width;
        *(uint*)(descriptor + P.Slot.Height) = (uint)height;
        *(uint*)(descriptor + P.Slot.Stride) = (uint)(width * 4);
        *(uint*)(descriptor + P.Slot.PixelFormat) = P.PixelFormatBgra32;
        *(long*)(descriptor + P.Slot.ProducerQpc) = MonotonicClock.Now;
        *(ulong*)(descriptor + P.Slot.DataOffset) = (ulong)dataOffset;
        *(ulong*)(descriptor + P.Slot.DataSize) = (ulong)size;
        pixels[..(width * height)].CopyTo(new Span<uint>(_base + dataOffset, width * height));
        Interlocked.Exchange(ref *seqLock, seq + 2);

        Interlocked.Exchange(ref *(long*)(_base + P.Header.LatestSequence), sequence);
        Interlocked.Exchange(ref *(int*)(_base + P.Header.LatestSlot), slot);
        _framesProduced++;
        *(long*)(_base + P.Header.FramesProduced) = _framesProduced;
    }

    private bool TryOpen()
    {
        try
        {
            _mapping = OpenMapping(MemoryMappedFileRights.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, P.MappingSize, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _base = pointer + _view.PointerOffset;
            _lastHeartbeatChange = 0;
            _lastProblem = null;
            Logger.Info("Connected to the virtual camera shared memory.");
            return true;
        }
        catch (FileNotFoundException)
        {
            // Normal: no consumer has started the camera yet.
            Close();
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            string problem = $"Cannot open camera shared memory: {ex.Message}";
            if (_lastProblem != problem)
            {
                Logger.Warn(problem);
            }

            _lastProblem = problem;
            Close();
            return false;
        }
    }

    /// <summary>The camera service creates the Global name; an in-process source (self-test) the Local one.</summary>
    private static MemoryMappedFile OpenMapping(MemoryMappedFileRights rights)
    {
        try
        {
            return MemoryMappedFile.OpenExisting(P.SharedMemoryName, rights);
        }
        catch (FileNotFoundException)
        {
            return MemoryMappedFile.OpenExisting(P.SharedMemoryNameLocal, rights);
        }
    }

    private bool HeaderValid()
    {
        bool valid = *(uint*)(_base + P.Header.Magic) == P.Magic
                     && *(uint*)(_base + P.Header.Version) == P.Version
                     && *(uint*)(_base + P.Header.HeaderSize) == P.HeaderSize
                     && *(uint*)(_base + P.Header.SlotCount) == P.SlotCount
                     && *(long*)(_base + P.Header.SlotCapacity) == P.SlotCapacity
                     && *(long*)(_base + P.Header.MappingSize) >= P.MappingSize;
        if (!valid)
        {
            _lastProblem = "Camera shared memory has an unexpected layout (version mismatch between app and camera DLL?).";
        }

        return valid;
    }

    private void Close()
    {
        if (_view != null)
        {
            if (_base != null)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            _view.Dispose();
            _view = null;
        }

        _mapping?.Dispose();
        _mapping = null;
        _base = null;
        _inactiveSince = 0;
    }

    /// <summary>Thread-safe snapshot for diagnostics (opens its own read-only view).</summary>
    public CameraLinkState GetState()
    {
        try
        {
            using MemoryMappedFile mapping = OpenMapping(MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, P.HeaderSize, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(P.Header.Magic) != P.Magic || view.ReadUInt32(P.Header.Version) != P.Version)
            {
                return new CameraLinkState(true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0, 0, "unexpected shared memory layout");
            }

            uint numerator = view.ReadUInt32(P.Header.RequestedFpsNumerator);
            uint denominator = view.ReadUInt32(P.Header.RequestedFpsDenominator);
            long start = view.ReadInt64(P.Header.LastStartFileTimeUtc);
            return new CameraLinkState(
                true,
                _active,
                (int)view.ReadUInt32(P.Header.RequestedWidth),
                (int)view.ReadUInt32(P.Header.RequestedHeight),
                denominator == 0 ? 0 : (double)numerator / denominator,
                view.ReadUInt32(P.Header.RequestedSubtype),
                view.ReadInt64(P.Header.FramesProduced),
                view.ReadInt64(P.Header.FramesDelivered),
                view.ReadInt64(P.Header.FramesRepeated),
                view.ReadInt64(P.Header.FramesFallback),
                view.ReadInt64(P.Header.FramesDroppedByProducer),
                view.ReadInt64(P.Header.StreamStarts),
                start > 0 ? DateTime.FromFileTimeUtc(start) : null,
                view.ReadInt32(P.Header.SourceProcessId),
                view.ReadInt32(P.Header.LastErrorHr),
                _lastProblem,
                view.ReadInt32(P.Header.ConsumerActive),
                view.ReadInt64(P.Header.ConsumerHeartbeat),
                view.ReadInt64(P.Header.ProducerHeartbeat),
                (int)view.ReadUInt32(P.Header.ConvertMicrosAverage),
                (int)view.ReadUInt32(P.Header.DeliverMicrosAverage));
        }
        catch (FileNotFoundException)
        {
            return new CameraLinkState(false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0, 0, _lastProblem);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new CameraLinkState(false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0, 0, ex.Message);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_base != null)
        {
            // Clean exit: tell the camera right away instead of letting it show our last frame until
            // the heartbeat times out (it streams the plain fallback background instead).
            Volatile.Write(ref *(long*)(_base + P.Header.ProducerHeartbeat), 0);
        }

        Close();
    }
}
