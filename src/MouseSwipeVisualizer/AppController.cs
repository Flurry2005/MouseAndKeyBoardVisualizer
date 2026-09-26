using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MouseSwipeVisualizer.Capture;
using MouseSwipeVisualizer.Engine;
using MouseSwipeVisualizer.Input;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Shell;
using MouseSwipeVisualizer.Swipe;
using MouseSwipeVisualizer.Utilities;
using static MouseSwipeVisualizer.Interop.NativeMethods;

using MouseSwipeVisualizer.Spotify;

namespace MouseSwipeVisualizer;

/// <summary>
/// UI shell around the window-independent pipeline:
/// <code>
/// RawMouseInput → MouseDeltaBuffer → SwipeEngine (own thread) → camera output / preview output
/// </code>
/// The preview (also the OBS capture window) and the settings window are optional views; closing
/// or minimizing them never stops the engine. The app keeps running in the tray until Exit.
/// Everything in this class runs on the UI thread.
/// </summary>
public sealed class AppController : ISettingsHost, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(250);

    private readonly SettingsService _settingsService;
    private readonly RawMouseInput _input;
    private readonly MouseDeltaBuffer _buffer;
    private readonly SwipeEngine _engine;
    private readonly PreviewFrameStore _preview;
    private readonly bool _headless;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly StringBuilder _statsText = new(2048);
    private readonly SpotifyService _spotify;
    private string? _spotifyCover;
    private string? _appliedCover = "\0"; // nothing applied yet
    private readonly object _coverGate = new();

    /// <summary>The cover is the frame picture only with "Use the cover of what's playing"; the card works either way.</summary>
    private void ApplySpotifyCover()
    {
        lock (_coverGate)
        {
            string? wanted = Settings.SpotifyCoverEnabled ? Volatile.Read(ref _spotifyCover) : null;
            if (wanted != _appliedCover)
            {
                _appliedCover = wanted;
                _engine.SetBackgroundOverride(wanted);
            }
        }
    }

    private CaptureWindow? _capture;
    private IntPtr _captureHwnd;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;
    private bool _exiting;

    private long _lastStatsEvents;
    private long _lastStatsPublished;
    private long _lastStatsTimestamp;
    private double _eventsPerSecond;
    private double _framesPerSecond;

    public AppController(AppSettings settings, SettingsService settingsService, RawMouseInput input, MouseDeltaBuffer buffer,
        SwipeEngine engine, PreviewFrameStore preview, bool headless)
    {
        Settings = settings;
        _settingsService = settingsService;
        _input = input;
        _buffer = buffer;
        _engine = engine;
        _preview = preview;
        _headless = headless;

        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _statsTimer = new DispatcherTimer { Interval = StatsInterval };
        _statsTimer.Tick += (_, _) => UpdateStats();

        _spotify = new SpotifyService(AppPaths.DataDirectory);
        _spotify.CoverChanged += path =>
        {
            Volatile.Write(ref _spotifyCover, path);
            ApplySpotifyCover();
        };
        _spotify.NowPlayingChanged += info => _engine.SetNowPlaying(info);
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Virtual camera one-liner and actions; set by App (keeps this class camera-agnostic).</summary>
    public Func<string>? CameraSummaryProvider { get; set; }

    public Func<CameraAction, Task<string>>? CameraActionHandler { get; set; }

    /// <summary>Extra diagnostics lines (e.g. virtual camera status); set by App.</summary>
    public Func<string>? ExtraDiagnostics { get; set; }

    /// <summary>Raised after settings were edited and applied (UI thread).</summary>
    public event Action<AppSettings>? SettingsApplied;

    /// <summary>The preview window if open (self-test).</summary>
    public CaptureWindow? PreviewWindow => _capture;

    public void Start()
    {
        _tray = new TrayIcon();
        _tray.ShowCaptureRequested += OpenPreview;
        _tray.SettingsRequested += OpenSettings;
        _tray.ClearRequested += ClearTrail;
        _tray.ExitRequested += RequestExit;

        if (!_headless && (Settings.ShowPreview || Settings.OutputMode == OutputMode.ObsCaptureWindow))
        {
            OpenPreview();
        }

        _statsTimer.Start();
        _spotify.Configure(Settings);

        if (!_headless && Settings.ShowSettingsOnStartup)
        {
            OpenSettings();
        }

        if (_headless)
        {
            _tray.ShowNotification("Mouse Swipe Visualizer", "Running in the background (headless). Use the tray icon for settings or the preview.", warning: false);
        }

        if (_settingsService.LastLoadStatus == SettingsLoadStatus.CorruptUsedDefaults)
        {
            _tray.ShowNotification("Settings reset", "The settings file was corrupt and has been backed up; defaults are in use.", warning: true);
        }
    }

    // ------------------------------------------------------------------ preview window

    public void OpenPreview()
    {
        if (_capture != null)
        {
            if (_capture.WindowState == WindowState.Minimized)
            {
                _capture.WindowState = WindowState.Normal;
            }

            _capture.Activate();
            return;
        }

        var window = new CaptureWindow { Icon = _tray?.CreateWindowIcon() };
        window.SettingsRequested += OpenSettings;
        window.ClearRequested += ClearTrail;
        window.ExitRequested += RequestExit;
        window.MinimizedChanged += OnPreviewMinimizedChanged;
        window.PreviewActiveChanged += _ => _engine.Wake();
        window.Closing += (_, _) => CapturePlacement(); // while the HWND still exists
        window.Closed += (_, _) =>
        {
            _capture = null;
            _captureHwnd = IntPtr.Zero;
            Logger.Info("Preview window closed; the engine and camera output keep running.");
        };

        _capture = window;
        _captureHwnd = new WindowInteropHelper(window).EnsureHandle();
        window.ApplySettings(Settings);
        window.AttachFrames(_preview);
        RestorePlacement();
        window.Show();
        RestorePlacement();

        CaptureWindowReport report = CaptureWindowInspector.Inspect(_captureHwnd);
        Logger.Info("Preview / OBS capture window ready:\n" + report.Describe());
    }

    private void OnPreviewMinimizedChanged(bool minimized)
    {
        if (!minimized)
        {
            _capture?.ApplyExactClientSize();
            return;
        }

        if (Settings.OutputMode == OutputMode.ObsCaptureWindow)
        {
            Logger.Warn("OBS capture window minimized: OBS drops minimized windows.");
            _tray?.ShowNotification("Capture window minimized",
                "OBS mode: OBS receives no frames while the capture window is minimized. The virtual camera is not affected.",
                warning: true);
        }
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this) { Icon = _tray?.CreateWindowIcon() };
        PlaceSettingsWindow(_settingsWindow);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            SaveNow();
        };
        _settingsWindow.Loaded += (_, _) => KeepInWorkArea(_settingsWindow);
        _settingsWindow.Show();
        UpdateStats();
    }

    /// <summary>After SizeToContent: keeps the whole window above the taskbar.</summary>
    private static void KeepInWorkArea(Window? window)
    {
        if (window == null)
        {
            return;
        }

        Rect work = SystemParameters.WorkArea;
        if (window.Top + window.ActualHeight > work.Bottom)
        {
            window.Top = Math.Max(work.Top, work.Bottom - window.ActualHeight);
        }
    }

    /// <summary>Next to the preview (never on top of the canvas), else centred.</summary>
    private void PlaceSettingsWindow(Window window)
    {
        if (_capture == null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Rect work = SystemParameters.WorkArea;
        double left = _capture.Left + _capture.ActualWidth + 12;
        if (left + window.Width > work.Right)
        {
            left = Math.Max(work.Left, _capture.Left - window.Width - 12);
        }

        window.Left = left;
        window.Top = Math.Max(work.Top, _capture.Top);
    }

    // ------------------------------------------------------------------ settings

    public void OnSettingsEdited()
    {
        Settings.Sanitize();
        _engine.ApplySettings(Settings);
        _capture?.ApplySettings(Settings);
        _spotify.Configure(Settings);
        ApplySpotifyCover();
        SettingsApplied?.Invoke(Settings);
        if (Settings.OutputMode == OutputMode.ObsCaptureWindow && _capture == null && !_headless)
        {
            OpenPreview(); // OBS captures this window, so it must exist
        }

        ScheduleSave();
    }

    public void ResetSettingsToDefaults()
    {
        Settings = new AppSettings { Window = Settings.Window };
        OnSettingsEdited();
        Logger.Info("Settings reset to defaults.");
    }

    public void ClearTrail() => _engine.ClearTrail();

    public void ShowPreview() => OpenPreview();

    public string CameraSummary() => CameraSummaryProvider?.Invoke() ?? string.Empty;

    public Task<string> RunCameraActionAsync(CameraAction action) =>
        CameraActionHandler?.Invoke(action) ?? Task.FromResult("Camera support is not available in this build.");

    public string SpotifyStatus() => _spotify.Status;

    public bool SpotifyHasSavedSecret => _spotify.HasSavedSecret;

    public Task<string> ConnectSpotifyAsync(string clientId, string? clientSecret)
    {
        Settings.SpotifyClientId = clientId.Trim();
        OnSettingsEdited();
        return _spotify.ConnectAsync(clientId, clientSecret);
    }

    public void DisconnectSpotify() => _spotify.Disconnect();

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        CapturePlacement();
        _settingsService.Save(Settings);
    }

    // ------------------------------------------------------------------ placement

    private void CapturePlacement()
    {
        if (_captureHwnd == IntPtr.Zero || _capture == null || _capture.WindowState != WindowState.Normal)
        {
            return;
        }

        WindowPlacementSettings? placement = WindowPlacementService.Capture(_captureHwnd);
        if (placement != null)
        {
            Settings.Window = placement;
        }
    }

    private void RestorePlacement()
    {
        if (_capture == null)
        {
            return;
        }

        (int outerWidth, int outerHeight) = _capture.ComputeOuterSize();
        WindowPlacementSettings? saved = Settings.Window?.Clone();
        if (saved != null)
        {
            saved.Width = outerWidth;
            saved.Height = outerHeight;
        }

        RECT rect = WindowPlacementService.ComputeRestoreRect(saved, WindowPlacementService.GetMonitors(),
            shrinkToFit: false, outerWidth, outerHeight);
        WindowPlacementService.Apply(_captureHwnd, rect);
        _capture.ApplyExactClientSize();
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>Human-readable pipeline statistics (settings window, debug overlay, self-test).</summary>
    public string BuildDiagnostics()
    {
        EngineStats es = _engine.Stats;
        SwipeTracker tracker = _engine.Tracker;
        long now = MonotonicClock.Now;
        long events = _input.TotalEvents;
        long published = es.FramesPublished;
        if (_lastStatsTimestamp != 0)
        {
            double seconds = MonotonicClock.TicksToMs(now - _lastStatsTimestamp) / 1000.0;
            if (seconds > 0.05)
            {
                _eventsPerSecond = (events - _lastStatsEvents) / seconds;
                _framesPerSecond = (published - _lastStatsPublished) / seconds;
            }
        }

        _lastStatsTimestamp = now;
        _lastStatsEvents = events;
        _lastStatsPublished = published;

        MouseDelta last = _buffer.LastDelta;
        CultureInfo c = CultureInfo.InvariantCulture;
        _statsText.Clear();
        _statsText.Append(c, $"dx {last.Dx,5}  dy {last.Dy,5}   input {_eventsPerSecond,6:0}/s   total {events}\n");
        _statsText.Append(c, $"virtual x {tracker.HeadX,7:0.000}  y {tracker.HeadY,7:0.000}  speed {tracker.SpeedCountsPerSecond,7:0} counts/s\n");
        _statsText.Append(c, $"points {tracker.PointCount}/{tracker.PointCapacity}  strokes {tracker.StrokeCount}  last break {tracker.LastBreakReason}  lifts {tracker.LiftCount}\n");
        _statsText.Append(c, $"engine: {es.RenderWidth}x{es.RenderHeight} target {es.TargetFps} fps, outputs {es.ActiveOutputs}, published {_framesPerSecond,5:0.0}/s\n");
        _statsText.Append(c, $"frames rasterized {es.FramesRasterized}  unchanged-skipped {es.FramesSkippedUnchanged}  published {es.FramesPublished}\n");
        _statsText.Append(c, $"frame ms: input+model {es.InputAndModelMs:0.000}  raster {es.RasterMs:0.000}  publish {es.PublishMs:0.000}  total {es.TotalMs:0.000} (max {es.MaxTotalMs:0.00})\n");
        _statsText.Append(c, $"alloc/frame {es.AllocatedBytesPerFrame:0} B  shaded px {es.LastShadedPixels}  coalesced deltas {_buffer.CoalescedCount}");
        return _statsText.ToString();
    }

    private void UpdateStats()
    {
        bool toSettings = _settingsWindow != null;
        bool toCapture = Settings.IncludeDebugInCapture && _capture != null;
        if (!toSettings && !toCapture)
        {
            return;
        }

        string text = BuildDiagnostics();
        if (toCapture)
        {
            _capture!.SetDebugText(text);
        }

        if (toSettings)
        {
            var sb = new StringBuilder(text);
            string? extra = ExtraDiagnostics?.Invoke();
            if (!string.IsNullOrEmpty(extra))
            {
                sb.Append("\n\n").Append(extra);
            }

            if (_captureHwnd != IntPtr.Zero)
            {
                CaptureWindowReport report = CaptureWindowInspector.Inspect(_captureHwnd);
                sb.Append("\n\nPreview / OBS capture window:\n").Append(report.Describe())
                    .Append("\npreview has focus: ").Append(GetForegroundWindow() == _captureHwnd);
            }
            else
            {
                sb.Append("\n\nPreview window: closed (camera output unaffected)");
            }

            _settingsWindow!.SetDiagnostics(sb.ToString());
        }
    }

    // ------------------------------------------------------------------ lifetime

    private void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exiting = true;
        _saveTimer.Stop();
        _statsTimer.Stop();

        try
        {
            SaveNow();
        }
        catch (Exception ex)
        {
            Logger.Error("Saving settings on exit failed.", ex);
        }

        _spotify.Dispose();
        _tray?.Dispose();
        _settingsWindow?.Close();
        _capture?.Close();
    }
}
