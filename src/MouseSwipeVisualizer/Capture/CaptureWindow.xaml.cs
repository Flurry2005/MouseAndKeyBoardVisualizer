using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseSwipeVisualizer.Engine;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Utilities;
using static MouseSwipeVisualizer.Interop.NativeMethods;

namespace MouseSwipeVisualizer.Capture;

/// <summary>
/// Preview of the rendered frames, and the target for OBS "Window Capture" in OBS output mode.
/// It shows background + swipe only; settings, debug output and all controls live in other windows.
/// <para>
/// The window does not render the swipe itself: it displays frames produced by the window-independent
/// <see cref="SwipeEngine"/> (the same pixels the virtual camera gets). Closing, hiding, covering or
/// minimizing it therefore never affects the camera output.
/// </para>
/// <para>
/// Capture space is separated from desktop space: the swipe is laid out on a surface of exactly
/// CaptureWidth x CaptureHeight units, and the client area is sized to exactly that many physical
/// pixels (via AdjustWindowRectExForDpi). The Viewbox scale therefore is 1/(DPI scale), and the
/// geometry OBS receives is identical at 100 %, 125 %, 150 % or 200 % display scaling.
/// </para>
/// </summary>
public partial class CaptureWindow : Window
{
    /// <summary>Stable title OBS uses to find the window. Never change it at runtime.</summary>
    public const string CaptureTitle = "Mouse Swipe Visualizer";

    /// <summary>Allows client areas larger than the monitor (e.g. 1080x1080 + title bar on a 1080p screen).</summary>
    private const int MaxTrackSize = 16384;

    private HwndSource? _source;
    private int _captureWidth = AppSettings.DefaultCaptureSize;
    private int _captureHeight = AppSettings.DefaultCaptureSize;
    private PreviewFrameStore? _frames;
    private WriteableBitmap? _bitmap;
    private long _lastFrameSequence = -1;
    private readonly Action _pullFrameAction;
    private readonly Action<uint[], int, int> _copyFrameAction;
    private readonly Action _frameAvailableHandler;

    public CaptureWindow()
    {
        InitializeComponent();
        Title = CaptureTitle;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => ApplyExactClientSize(), System.Windows.Threading.DispatcherPriority.Background);
        StateChanged += (_, _) =>
        {
            UpdatePreviewActive();
            MinimizedChanged?.Invoke(WindowState == WindowState.Minimized);
        };
        IsVisibleChanged += (_, _) => UpdatePreviewActive();
        MouseDoubleClick += OnMouseDoubleClick;
        _pullFrameAction = PullFrame;
        _copyFrameAction = CopyFrame;
        _frameAvailableHandler = () => Dispatcher.BeginInvoke(DispatcherPriority.Render, _pullFrameAction);
    }

    /// <summary>Raised when the preview starts/stops wanting frames (visible and not minimized).</summary>
    public event Action<bool>? PreviewActiveChanged;

    /// <summary>Connects the window to the engine's preview output.</summary>
    public void AttachFrames(PreviewFrameStore frames)
    {
        _frames = frames;
        // Called on the engine thread at most once per pulled frame (no queue build-up).
        frames.FrameAvailable += _frameAvailableHandler;
        UpdatePreviewActive();
        PullFrame();
    }

    private void UpdatePreviewActive()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized;
        if (_frames != null && _frames.IsActive != active)
        {
            _frames.SetActive(active);
            PreviewActiveChanged?.Invoke(active);
        }
    }

    private void PullFrame()
    {
        _frames?.CopyLatest(ref _lastFrameSequence, _copyFrameAction);
    }

    private void CopyFrame(uint[] pixels, int width, int height)
    {
        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            FrameImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
    }

    /// <summary>Frames shown so far (self-test).</summary>
    public long LastShownFrameSequence => _lastFrameSequence;

    public event Action? SettingsRequested;

    public event Action? ClearRequested;

    public event Action? ExitRequested;

    /// <summary>Raised with true when minimized: WPF stops rendering then and OBS gets no new frames.</summary>
    public event Action<bool>? MinimizedChanged;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public int CaptureWidth => _captureWidth;

    public int CaptureHeight => _captureHeight;

    /// <summary>The element whose contents are exactly the capture output (used by the self-test).</summary>
    public FrameworkElement CaptureSurface => Surface;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            // By default Windows caps a window at roughly the monitor size, which would shrink e.g. a
            // 1080x1080 client area on a 1080p monitor. Parts outside the monitor are still rendered
            // into the window's DWM surface, so OBS captures the full canvas.
            Marshal.WriteInt32(lParam, MINMAXINFO_MaxTrackSizeOffset, MaxTrackSize);
            Marshal.WriteInt32(lParam, MINMAXINFO_MaxTrackSizeOffset + 4, MaxTrackSize);
        }

        return IntPtr.Zero;
    }

    /// <summary>Applies canvas size, background and swipe settings.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _captureWidth = settings.CaptureWidth;
        _captureHeight = settings.CaptureHeight;
        Surface.Width = _captureWidth;
        Surface.Height = _captureHeight;

        var background = new SolidColorBrush(settings.GetCaptureBackgroundColor());
        background.Freeze();
        Background = background;
        Surface.Background = background;

        DebugPanel.Visibility = settings.IncludeDebugInCapture ? Visibility.Visible : Visibility.Collapsed;
        ApplyExactClientSize();
    }

    public void SetDebugText(string text) => DebugText.Text = text;

    /// <summary>Outer window size (physical px) that yields the requested client size at the window's DPI.</summary>
    public (int Width, int Height) ComputeOuterSize()
    {
        IntPtr hwnd = Handle;
        uint dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 96;
        var rect = new RECT(0, 0, _captureWidth, _captureHeight);
        uint style = hwnd != IntPtr.Zero ? (uint)GetWindowStyle(hwnd) : 0x00CF0000u;
        uint exStyle = hwnd != IntPtr.Zero ? (uint)GetWindowExStyle(hwnd) : 0u;
        if (!AdjustWindowRectExForDpi(ref rect, style, false, exStyle, dpi == 0 ? 96 : dpi))
        {
            Logger.Warn($"AdjustWindowRectExForDpi failed (error {Marshal.GetLastPInvokeError()}).");
            return (_captureWidth, _captureHeight);
        }

        return (rect.Width, rect.Height);
    }

    /// <summary>Resizes the window (keeping its position) so the client area is exactly the capture size.</summary>
    public void ApplyExactClientSize()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || WindowState != WindowState.Normal)
        {
            return;
        }

        if (GetClientRect(hwnd, out RECT client) && client.Width == _captureWidth && client.Height == _captureHeight)
        {
            return;
        }

        (int width, int height) = ComputeOuterSize();
        if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
        {
            Logger.Warn($"SetWindowPos (capture size) failed (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            SettingsRequested?.Invoke();
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void ClearMenuItem_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        if (_frames != null)
        {
            _frames.FrameAvailable -= _frameAvailableHandler;
            _frames.SetActive(false);
            PreviewActiveChanged?.Invoke(false);
        }
        base.OnClosed(e);
    }
}
