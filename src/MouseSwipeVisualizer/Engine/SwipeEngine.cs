using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MouseSwipeVisualizer.Input;
using MouseSwipeVisualizer.Rendering;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Swipe;
using MouseSwipeVisualizer.Utilities;
using static MouseSwipeVisualizer.Interop.NativeMethods;

namespace MouseSwipeVisualizer.Engine;

/// <summary>
/// Window-independent frame producer:
/// <code>
/// MouseDeltaBuffer → SwipeTracker → SwipeModelBuilder → SoftwareRasterizer → outputs (camera, preview)
/// </code>
/// Runs on its own thread with its own high-resolution timing, so the video never depends on a
/// WPF window being visible: covered, minimized, hidden or closed windows make no difference.
/// <para>
/// Input and video rates are independent: Raw Input (125-8000 Hz) is queued in the delta buffer
/// with per-delta timestamps and consumed once per frame at the output's frame rate.
/// </para>
/// <para>
/// Cost control: with no active output the engine only keeps the tracker up to date (cheap) and
/// sleeps; when nothing is visible it sleeps until the next mouse delta; an unchanged empty frame
/// is not rasterized again.
/// </para>
/// </summary>
public sealed class SwipeEngine : IDisposable
{
    public const int IdleTickMs = 100;
    public const int DefaultFps = 60;

    private readonly MouseDeltaBuffer _buffer;
    private readonly IFrameOutput[] _outputs;
    private readonly long[] _publishedSequence;
    private readonly SwipeTracker _tracker = new();
    private readonly SwipeModelBuilder _builder = new();
    private readonly SwipeRenderModel _model = new();
    private readonly SoftwareRasterizer _rasterizer = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly WaitHandle[] _frameWait;
    private readonly WaitHandle[] _idleWait;
    private readonly WaitableTimerHandle _timer;
    private readonly Thread _thread;

    private AppSettings? _pendingSettings;
    private int _clearRequested;
    private int _captureWidth = AppSettings.DefaultCaptureSize;
    private int _captureHeight = AppSettings.DefaultCaptureSize;
    private int _previewFps = AppSettings.DefaultRenderFps;
    private long _sequence;
    private bool _lastFrameEmpty;
    private bool _forceRaster = true;
    private int _lastWidth;
    private int _lastHeight;
    private long _nextDeadline;
    private bool _disposed;

    public SwipeEngine(MouseDeltaBuffer buffer, AppSettings settings, params IFrameOutput[] outputs)
    {
        _buffer = buffer;
        _outputs = outputs;
        _publishedSequence = new long[outputs.Length];
        Array.Fill(_publishedSequence, -1);
        _timer = new WaitableTimerHandle();
        _frameWait = new WaitHandle[] { _stop, _timer.WaitHandle };
        _idleWait = new WaitHandle[] { _stop, _wake };
        ApplySettingsNow(settings.Clone());
        _buffer.WakeRequested += OnWakeRequested;
        _thread = new Thread(Run) { IsBackground = true, Name = "SwipeEngine", Priority = ThreadPriority.AboveNormal };
    }

    public EngineStats Stats { get; } = new();

    /// <summary>Pressed keys for the keyboard panel (set before Start; written by the input thread).</summary>
    public KeyboardState? Keyboard { get; set; }

    private long _lastKeyboardVersion = -1;

    /// <summary>Tracker state for diagnostics only (read racily from other threads).</summary>
    public SwipeTracker Tracker => _tracker;

    public bool IsRunning => _thread.IsAlive;

    public void Start() => _thread.Start();

    /// <summary>Thread-safe; applied by the engine thread before its next frame.</summary>
    public void ApplySettings(AppSettings settings)
    {
        Interlocked.Exchange(ref _pendingSettings, settings.Clone());
        _wake.Set();
    }

    public void ClearTrail()
    {
        Interlocked.Exchange(ref _clearRequested, 1);
        _wake.Set();
    }

    /// <summary>Thread-safe: background picture override (Spotify cover), null = the configured picture.</summary>
    public void SetBackgroundOverride(string? path)
    {
        Volatile.Write(ref _backgroundOverride, path);
        Interlocked.Exchange(ref _backgroundOverridePending, 1);
        _wake.Set();
    }

    private string? _backgroundOverride;
    private int _backgroundOverridePending;

    /// <summary>Wakes the engine, e.g. after an output became active.</summary>
    public void Wake() => _wake.Set();

    private void OnWakeRequested() => _wake.Set();

    private void ApplySettingsNow(AppSettings settings)
    {
        _tracker.Configure(settings.SensitivityScale, settings.SwipeBreakMs, settings.TrailLifetimeMs,
            settings.LiftDetectionEnabled, settings.LiftGapMs);
        _builder.Configure(settings);
        _captureWidth = settings.CaptureWidth;
        _captureHeight = settings.CaptureHeight;
        _previewFps = settings.RenderFps;
        _forceRaster = true;
    }

    private void Run()
    {
        Logger.Info("Swipe engine started.");
        try
        {
            while (!_stop.WaitOne(0))
            {
                RunOnce();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Swipe engine crashed.", ex);
        }

        Logger.Info("Swipe engine stopped.");
    }

    private void RunOnce()
    {
        long start = MonotonicClock.Now;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stats.Loops++;

        AppSettings? settings = Interlocked.Exchange(ref _pendingSettings, null);
        if (settings != null)
        {
            ApplySettingsNow(settings);
        }

        if (Interlocked.Exchange(ref _backgroundOverridePending, 0) == 1)
        {
            _builder.SetImageOverride(Volatile.Read(ref _backgroundOverride));
            _forceRaster = true;
        }

        if (Interlocked.Exchange(ref _clearRequested, 0) == 1)
        {
            _tracker.Clear();
            _forceRaster = true;
        }

        // Pick render size and rate: the first active output with a preference wins (the camera is
        // registered first); otherwise the capture/preview size from the settings.
        int width = _captureWidth, height = _captureHeight, fps = _previewFps;
        int active = 0;
        bool sizeChosen = false;
        foreach (IFrameOutput output in _outputs)
        {
            if (!output.IsActive)
            {
                continue;
            }

            active++;
            if (!sizeChosen && output.RequestedWidth > 0 && output.RequestedHeight > 0)
            {
                width = output.RequestedWidth;
                height = output.RequestedHeight;
                if (output.RequestedFps > 0)
                {
                    fps = output.RequestedFps;
                }

                sizeChosen = true;
            }
        }

        Stats.ActiveOutputs = active;
        Stats.RenderWidth = width;
        Stats.RenderHeight = height;
        Stats.TargetFps = fps;

        // Input is always consumed so the tracker is current when an output starts.
        ReadOnlySpan<MouseDelta> deltas = _buffer.Drain();
        for (int i = 0; i < deltas.Length; i++)
        {
            _tracker.AddDelta(deltas[i]);
        }

        long now = MonotonicClock.Now;
        (double halfX, double halfY) = _builder.GetHalfExtents(width, height);
        _tracker.Update(now, halfX, halfY);

        if (active == 0)
        {
            TickOutputs();
            WaitIdle();
            return;
        }

        _builder.Build(_tracker, width, height, now, _model, Keyboard);
        long modelDone = MonotonicClock.Now;

        bool empty = _model.IsEmpty;
        bool sameSize = width == _lastWidth && height == _lastHeight;
        long keyboardVersion = Keyboard?.Version ?? 0;
        bool rasterize = _forceRaster || !sameSize || !(empty && _lastFrameEmpty) || keyboardVersion != _lastKeyboardVersion
                         || _rasterizer.IsAnimating;
        if (rasterize)
        {
            _rasterizer.Render(_model);
            _sequence++;
            Stats.FramesRasterized++;
            Stats.LastShadedPixels = _rasterizer.LastShadedPixels;
            _lastWidth = width;
            _lastHeight = height;
            _lastFrameEmpty = empty;
            _lastKeyboardVersion = keyboardVersion;
            _forceRaster = false;
        }
        else
        {
            Stats.FramesSkippedUnchanged++;
        }

        long rasterDone = MonotonicClock.Now;
        for (int i = 0; i < _outputs.Length; i++)
        {
            IFrameOutput output = _outputs[i];
            if (output.IsActive && _publishedSequence[i] != _sequence && _rasterizer.Width == width && _rasterizer.Height == height)
            {
                output.Publish(_rasterizer.Pixels, width, height, _sequence);
                _publishedSequence[i] = _sequence;
                Stats.FramesPublished++;
            }
        }

        TickOutputs();
        long end = MonotonicClock.Now;
        Stats.Record(
            MonotonicClock.TicksToMs(modelDone - start),
            MonotonicClock.TicksToMs(rasterDone - modelDone),
            MonotonicClock.TicksToMs(end - rasterDone),
            MonotonicClock.TicksToMs(end - start),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);

        if (empty && !_rasterizer.IsAnimating && _buffer.TryEnterIdle())
        {
            // Nothing visible: sleep until the next mouse delta (or the output heartbeat interval).
            WaitIdle();
            _nextDeadline = 0;
            return;
        }

        WaitForNextFrame(Math.Clamp(fps, 1, 1000));
    }

    private void TickOutputs()
    {
        foreach (IFrameOutput output in _outputs)
        {
            output.Tick();
        }
    }

    private void WaitIdle()
    {
        WaitHandle.WaitAny(_idleWait, IdleTickMs);
        _buffer.ExitIdle();
    }

    private void WaitForNextFrame(int fps)
    {
        long interval = MonotonicClock.MsToTicks(1000.0 / fps);
        long now = MonotonicClock.Now;
        _nextDeadline = _nextDeadline == 0 ? now + interval : _nextDeadline + interval;
        if (_nextDeadline <= now)
        {
            // Fell behind (e.g. the machine was busy): resynchronise instead of bursting frames.
            _nextDeadline = now + interval;
        }

        long due100ns = -Math.Max(1, (long)(MonotonicClock.TicksToMs(_nextDeadline - now) * 10_000));
        if (_timer.Set(due100ns))
        {
            WaitHandle.WaitAny(_frameWait);
        }
        else
        {
            Thread.Sleep(Math.Max(1, (int)MonotonicClock.TicksToMs(_nextDeadline - now)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.WakeRequested -= OnWakeRequested;
        _stop.Set();
        if (_thread.IsAlive && !_thread.Join(2000))
        {
            Logger.Warn("Swipe engine did not stop within 2 s.");
        }

        _timer.Dispose();
        _wake.Dispose();
        _stop.Dispose();
    }

    /// <summary>High-resolution waitable timer exposed as a <see cref="System.Threading.WaitHandle"/>.</summary>
    private sealed class WaitableTimerHandle : IDisposable
    {
        private readonly EventWaitHandle _handle;

        public WaitableTimerHandle()
        {
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (timer == IntPtr.Zero)
            {
                // Pre-1803 fallback: a normal waitable timer (coarser, still correct).
                timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
            }

            if (timer == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWaitableTimerEx failed");
            }

            _handle = new EventWaitHandle(false, EventResetMode.AutoReset);
            SafeWaitHandle placeholder = _handle.SafeWaitHandle;
            _handle.SafeWaitHandle = new SafeWaitHandle(timer, ownsHandle: true);
            placeholder.Dispose();
        }

        public WaitHandle WaitHandle => _handle;

        public bool Set(long dueTime100ns) =>
            SetWaitableTimer(_handle.SafeWaitHandle.DangerousGetHandle(), ref dueTime100ns, 0, IntPtr.Zero, IntPtr.Zero, false);

        public void Dispose() => _handle.Dispose();
    }
}
