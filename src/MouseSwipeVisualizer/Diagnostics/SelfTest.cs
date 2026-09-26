using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using MouseSwipeVisualizer.Input;
using MouseSwipeVisualizer.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MouseSwipeVisualizer.Camera;
using MouseSwipeVisualizer.Capture;
using MouseSwipeVisualizer.Engine;
using MouseSwipeVisualizer.Rendering;
using MouseSwipeVisualizer.Shell;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Swipe;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Diagnostics;

public sealed record SelfTestOptions(string OutputPath, int InputSeconds, bool SkipCamera, string? Filter = null);

/// <summary>
/// Built-in verification, run with <c>MouseSwipeVisualizer.exe --selftest</c>. It exercises the real
/// Win32 paths (Raw Input registration, capture-window styles, OBS window filter, PrintWindow capture, monitor enumeration) and the pure
/// logic (tracker, settings, buffers), then writes a report and exits with 0 (pass) or 1 (fail).
/// The app itself never injects input; with <c>--selftest-input-seconds N</c> it only counts real
/// WM_INPUT packets for N seconds (move the mouse, or inject from an external script).
/// </summary>
public static class SelfTest
{
    public static bool TryParseArguments(string[] args, out SelfTestOptions? options)
    {
        options = null;
        if (Array.IndexOf(args, "--selftest") < 0)
        {
            return false;
        }

        string output = Path.Combine(AppPaths.DataDirectory, "selftest.txt");
        int inputSeconds = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--selftest-out")
            {
                output = Path.GetFullPath(args[i + 1]);
            }
            else if (args[i] == "--selftest-input-seconds" && int.TryParse(args[i + 1], out int s))
            {
                inputSeconds = Math.Clamp(s, 0, 120);
            }
        }

        int filterIndex = Array.IndexOf(args, "--selftest-filter");
        string? filter = filterIndex >= 0 && filterIndex + 1 < args.Length ? args[filterIndex + 1] : null;
        options = new SelfTestOptions(output, inputSeconds, Array.IndexOf(args, "--selftest-skip-camera") >= 0, filter);
        return true;
    }

    public static async Task<int> RunAsync(SelfTestOptions options)
    {
        _imageDirectory = Path.GetDirectoryName(options.OutputPath);
        Directory.CreateDirectory(_imageDirectory!);
        var report = new Report { Filter = options.Filter };
        report.Line($"Mouse Swipe Visualizer self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.Line($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.OSDescription}");
        report.Line(string.Empty);

        report.Run("Interop struct layout", TestStructLayout);
        report.Run("App icon (Start menu / search)", TestAppIcon);
        report.Run("Camera in use: friendly message", r =>
        {
            var inUse = new System.Runtime.InteropServices.COMException("Maskinvarans MFT kunde inte starta direktuppspelningen (0xC00D3704)", CameraDiagnostics.CameraInUseHResult);
            r.Check("0xC00D3704 is recognised (also wrapped)", CameraDiagnostics.IsCameraInUse(inUse) && CameraDiagnostics.IsCameraInUse(new InvalidOperationException("open failed", inUse)), true);
            r.Check("other errors are not", CameraDiagnostics.IsCameraInUse(new InvalidOperationException("device not found")), false);
        });
        report.Run("Settings save/load round trip", TestSettingsRoundTrip);
        report.Run("Settings corrupt JSON fallback", TestSettingsCorrupt);
        report.Run("Settings out-of-range sanitizing", TestSettingsSanitize);
        report.Run("Capture settings defaults + legacy overlay file", TestCaptureSettings);
        report.Run("Delta buffer coalescing + idle wake", TestDeltaBuffer);
        report.Run("Tracker accumulation", TestTrackerAccumulation);
        report.Run("Tracker swipe break", TestTrackerBreak);
        report.Run("Tracker lift detection", TestTrackerLift);
        report.Run("Tracker natural stop is not a lift", TestTrackerNaturalStop);
        report.Run("Tracker expiry", TestTrackerExpiry);
        report.Run("Tracker bounded memory", TestTrackerBounded);
        report.Run("Tracker view fit keeps large flicks inside", TestTrackerViewFit);
        report.Run("Smoother keeps end points", TestSmoother);
        report.Run("Placement restore with missing monitor / bad bounds", TestPlacement);
        await report.RunAsync("Raw Input registration + WM_INPUT", r => TestRawInputAsync(r, options.InputSeconds));
        await report.RunAsync("OBS capture window: Win32 properties, OBS listing, exact capture size", TestCaptureWindowAsync);
        report.Run("Off-screen rasterizer: no window, chroma-safe, NV12-safe", TestOffscreenRasterizer);
        report.Run("Synthetic gestures: right flick, left flick, curve, lift/re-center", TestGestureDirections);
        report.Run("Keyboard panel + frame: 40/60 split, key presses, static layer cache, chroma-safe", TestKeyboardPanel);
        report.Run("Anti-aliasing, background image, glass look, borders off", TestGlassAndAntiAliasing);
        await report.RunAsync("Spotify cover art: secrets, parsing, callback, background override", TestSpotifyAsync);
        report.Run("Cover colours: accent for strokes and key outlines, auto contrast", TestCoverColors);
        await report.RunAsync("Headless engine: preview visible/covered/minimized/hidden/closed", TestHeadlessEngineAsync);
        await report.RunAsync("Preview capture while occluded and unfocused (PrintWindow)", TestOccludedCaptureAsync);
        await report.RunAsync("Rendering cost", TestRenderingCostAsync);
        await report.RunAsync("Settings window opens and edits capture settings", TestSettingsWindowAsync);
        if (!options.SkipCamera)
        {
            await report.RunAsync("Virtual camera media source, in-process consumer (MF contract, IPC, frames)", TestCameraInProcessAsync);
            await report.RunAsync("Virtual camera through the Windows Frame Server (enumeration + consumer)", TestCameraFrameServerAsync);
        }

        report.Line(string.Empty);
        report.Line(report.Failures == 0 ? $"RESULT: PASS ({report.Passed} checks)" : $"RESULT: FAIL ({report.Failures} failed, {report.Passed} passed)");

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        await File.WriteAllTextAsync(options.OutputPath, report.ToString());
        Logger.Info($"Self-test finished: {report.Failures} failures. Report: {options.OutputPath}");
        return report.Failures;
    }

    // ------------------------------------------------------------------ tests

    private static void TestStructLayout(Report r)
    {
        int ptr = IntPtr.Size;
        r.Check("RAWINPUTHEADER size", Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>(), 8 + 2 * ptr);
        r.Check("RAWINPUTDEVICE size", Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>(), 8 + ptr);
        r.Check("RAWMOUSE size", Marshal.SizeOf<NativeMethods.RAWMOUSE>(), 24);
        r.Check("RAWMOUSE.usButtonFlags offset", (int)Marshal.OffsetOf<NativeMethods.RAWMOUSE>(nameof(NativeMethods.RAWMOUSE.usButtonFlags)), 4);
        r.Check("RAWMOUSE.lLastX offset", (int)Marshal.OffsetOf<NativeMethods.RAWMOUSE>(nameof(NativeMethods.RAWMOUSE.lLastX)), 12);
        r.Check("RAWMOUSE.lLastY offset", (int)Marshal.OffsetOf<NativeMethods.RAWMOUSE>(nameof(NativeMethods.RAWMOUSE.lLastY)), 16);
        r.Check("Process is 64-bit", Environment.Is64BitProcess, true);
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MouseSwipeVisualizer-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TestSettingsRoundTrip(Report r)
    {
        string dir = TempDir();
        try
        {
            var service = new SettingsService(Path.Combine(dir, "settings.json"));
            r.Check("missing file -> defaults", service.Load().TrailLifetimeMs, AppSettings.DefaultTrailLifetimeMs);
            r.Check("missing status", service.LastLoadStatus, SettingsLoadStatus.MissingUsedDefaults);

            var s = new AppSettings
            {
                TrailLifetimeMs = 777,
                TrailThickness = 6.5,
                SensitivityScale = 2.5,
                SmoothingStrength = 0.6,
                SwipeBreakMs = 150,
                LiftDetectionEnabled = false,
                LiftGapMs = 55,
                HeadStyle = HeadStyle.Dot,
                KeyboardEnabled = false,
                KeyboardPosition = KeyboardPosition.Bottom,
                FrameCornerRadius = 30,
                KeyPressedFillColor = "#FF8800",
                RenderFps = 120,
                CaptureWidth = 1080,
                CaptureHeight = 600,
                ChromaKeyColor = "#FF00FF",
                CaptureBackgroundEnabled = false,
                ChromaSafeEdges = false,
                IncludeDebugInCapture = true,
                ShowSettingsOnStartup = false,
                Window = new WindowPlacementSettings { X = -1500, Y = 20, Width = 300, Height = 200, MonitorDevice = @"\\.\DISPLAY2" },
            };
            r.Check("save", service.Save(s), true);
            AppSettings loaded = service.Load();
            r.Check("status", service.LastLoadStatus, SettingsLoadStatus.Loaded);
            r.Check("TrailLifetimeMs", loaded.TrailLifetimeMs, 777.0);
            r.Check("TrailThickness", loaded.TrailThickness, 6.5);
            r.Check("SensitivityScale", loaded.SensitivityScale, 2.5);
            r.Check("SmoothingStrength", loaded.SmoothingStrength, 0.6);
            r.Check("SwipeBreakMs", loaded.SwipeBreakMs, 150.0);
            r.Check("LiftDetectionEnabled", loaded.LiftDetectionEnabled, false);
            r.Check("LiftGapMs", loaded.LiftGapMs, 55.0);
            r.Check("HeadStyle", loaded.HeadStyle, HeadStyle.Dot);
            r.Check("KeyboardEnabled", loaded.KeyboardEnabled, false);
            r.Check("KeyboardPosition", loaded.KeyboardPosition, KeyboardPosition.Bottom);
            r.Check("FrameCornerRadius", loaded.FrameCornerRadius, 30.0);
            r.Check("KeyPressedFillColor", loaded.KeyPressedFillColor, "#FF8800");
            r.Check("RenderFps", loaded.RenderFps, 120);
            r.Check("CaptureWidth", loaded.CaptureWidth, 1080);
            r.Check("CaptureHeight", loaded.CaptureHeight, 600);
            r.Check("ChromaKeyColor", loaded.ChromaKeyColor, "#FF00FF");
            r.Check("CaptureBackgroundEnabled", loaded.CaptureBackgroundEnabled, false);
            r.Check("ChromaSafeEdges", loaded.ChromaSafeEdges, false);
            r.Check("IncludeDebugInCapture", loaded.IncludeDebugInCapture, true);
            r.Check("ShowSettingsOnStartup", loaded.ShowSettingsOnStartup, false);
            r.Check("Window.X", loaded.Window?.X, -1500);
            r.Check("Window.MonitorDevice", loaded.Window?.MonitorDevice, @"\\.\DISPLAY2");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void TestSettingsCorrupt(Report r)
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"TrailLifetimeMs\": 12 3, garbage ");
            var service = new SettingsService(path);
            AppSettings loaded = service.Load();
            r.Check("status", service.LastLoadStatus, SettingsLoadStatus.CorruptUsedDefaults);
            r.Check("defaults used", loaded.TrailLifetimeMs, AppSettings.DefaultTrailLifetimeMs);
            r.Check("backup written", Directory.GetFiles(dir, "settings.corrupt-*.json").Length, 1);

            File.WriteAllText(path, "null");
            service.Load();
            r.Check("'null' JSON handled", service.LastLoadStatus, SettingsLoadStatus.CorruptUsedDefaults);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void TestSettingsSanitize(Report r)
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path,
                "{ \"TrailLifetimeMs\": -5, \"SensitivityScale\": 1e9, \"RenderFps\": 5000, \"TrailColor\": \"notacolor\", " +
                "\"Window\": { \"X\": 0, \"Y\": 0, \"Width\": -10, \"Height\": 50 }, }");
            AppSettings loaded = new SettingsService(path).Load();
            r.Check("lifetime clamped", loaded.TrailLifetimeMs, AppSettings.MinTrailLifetimeMs);
            r.Check("sensitivity clamped", loaded.SensitivityScale, AppSettings.MaxSensitivity);
            r.Check("fps clamped", loaded.RenderFps, AppSettings.MaxRenderFps);
            r.Check("color reset", loaded.TrailColor, AppSettings.DefaultTrailColor);
            r.Check("invalid window dropped", loaded.Window == null, true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void TestDeltaBuffer(Report r)
    {
        var buffer = new MouseDeltaBuffer(capacity: 4);
        int wakes = 0;
        buffer.WakeRequested += () => wakes++;
        long sumX = 0, sumY = 0;
        for (int i = 1; i <= 10; i++)
        {
            buffer.Push(new MouseDelta(i, -i, i));
            sumX += i;
            sumY -= i;
        }

        ReadOnlySpan<MouseDelta> drained = buffer.Drain();
        long gotX = 0, gotY = 0;
        foreach (MouseDelta d in drained)
        {
            gotX += d.Dx;
            gotY += d.Dy;
        }

        r.Check("bounded length", drained.Length, 4);
        r.Check("total dx preserved", gotX, sumX);
        r.Check("total dy preserved", gotY, sumY);
        r.Check("coalesced count", buffer.CoalescedCount, 6L);
        r.Check("empty after drain", buffer.Drain().Length, 0);
        r.Check("enter idle", buffer.TryEnterIdle(), true);
        buffer.Push(new MouseDelta(1, 1, 100));
        buffer.Push(new MouseDelta(1, 1, 101));
        r.Check("exactly one wake after idle", wakes, 1);
        buffer.Drain();
        buffer.Push(new MouseDelta(1, 1, 102));
        r.Check("cannot idle with pending data", buffer.TryEnterIdle(), false);
    }

    private static long Ms(double ms) => MonotonicClock.MsToTicks(ms);

    private static SwipeTracker NewTracker(double sensitivity = 1.0)
    {
        var tracker = new SwipeTracker();
        tracker.Configure(sensitivity, 120, 500, liftDetection: true, liftGapMs: 40);
        return tracker;
    }

    private static void TestTrackerAccumulation(Report r)
    {
        SwipeTracker t = NewTracker();
        long t0 = Ms(10_000);
        t.AddDelta(new MouseDelta(8, -2, t0));
        t.AddDelta(new MouseDelta(12, -3, t0 + Ms(3)));
        t.AddDelta(new MouseDelta(17, -5, t0 + Ms(6)));
        t.AddDelta(new MouseDelta(10, -4, t0 + Ms(9)));
        double unit = 1.0 / SwipeTracker.CountsPerRadiusAtUnitySensitivity;
        r.CheckClose("head x = 47 counts", t.HeadX, 47 * unit);
        r.CheckClose("head y = -14 counts", t.HeadY, -14 * unit);
        r.Check("one stroke", t.StrokeCount, 1);
        r.Check("origin + 4 points", t.PointCount, 5);
        r.CheckClose("origin at centre", t.GetPoint(0).X, 0);
        r.CheckClose("2nd point = first delta", t.GetPoint(1).X, 8 * unit);

        SwipeTracker fast = NewTracker(sensitivity: 2.0);
        for (int i = 0; i < 16; i++)
        {
            fast.AddDelta(new MouseDelta(1, 0, t0 + Ms(i * 0.125))); // 8 kHz
        }

        r.CheckClose("sensitivity 2x", fast.HeadX, 16 * 2 * unit);
        r.Check("8 kHz samples merged (<= 2 ms/point)", fast.PointCount <= 3, true);
    }

    private static void TestTrackerBreak(Report r)
    {
        SwipeTracker t = NewTracker();
        long t0 = Ms(20_000);
        for (int i = 0; i < 20; i++)
        {
            t.AddDelta(new MouseDelta(10, 0, t0 + Ms(i))); // slow constant motion, then...
        }

        for (int i = 0; i < 20; i++)
        {
            t.AddDelta(new MouseDelta(10 - i / 2, 0, t0 + Ms(20 + i))); // ...decelerate
        }

        long later = t0 + Ms(40 + 200); // pause > SwipeBreakMs
        t.AddDelta(new MouseDelta(0, 5, later));
        r.Check("two strokes", t.StrokeCount, 2);
        r.Check("break reason", t.LastBreakReason, StrokeBreakReason.Pause);
        r.CheckClose("new stroke starts at centre (x)", t.HeadX, 0);
        r.CheckClose("new stroke head y", t.HeadY, 5 / SwipeTracker.CountsPerRadiusAtUnitySensitivity);

        SwipeTracker c = NewTracker();
        for (int i = 0; i < 300; i++)
        {
            c.AddDelta(new MouseDelta(1, 1, t0 + Ms(i * 8))); // 125 Hz, continuous for 2.4 s
        }

        r.Check("continuous 125 Hz motion stays one stroke", c.StrokeCount, 1);
    }

    private static void TestTrackerLift(Report r)
    {
        SwipeTracker t = NewTracker();
        long t0 = Ms(30_000);
        for (int i = 0; i < 60; i++)
        {
            t.AddDelta(new MouseDelta(20, 0, t0 + Ms(i))); // fast swipe at 1 kHz: 20000 counts/s
        }

        // Sensor cuts out mid-motion. Update() during the silence must end the stroke (re-center).
        long inAir = t0 + Ms(59 + 60);
        t.Update(inAir, 1, 1);
        r.Check("lift detected while in the air", t.LiftCount, 1L);
        r.Check("active stroke ended", t.ActiveStroke == null, true);
        r.Check("re-center marker timestamp", t.LastLiftTimestamp, inAir);

        t.AddDelta(new MouseDelta(20, 0, t0 + Ms(59 + 90))); // put down, gap < SwipeBreakMs
        r.Check("new stroke after lift", t.StrokeCount, 2);
        r.Check("break reason", t.LastBreakReason, StrokeBreakReason.Lift);
        r.CheckClose("new stroke re-centered", t.HeadX, 20 / SwipeTracker.CountsPerRadiusAtUnitySensitivity);

        // Same, but lift and put-down both inside one batch (no Update in between).
        SwipeTracker b = NewTracker();
        for (int i = 0; i < 60; i++)
        {
            b.AddDelta(new MouseDelta(0, -20, t0 + Ms(i)));
        }

        b.AddDelta(new MouseDelta(0, -20, t0 + Ms(59 + 70)));
        r.Check("batched lift detected", b.LastBreakReason, StrokeBreakReason.Lift);

        SwipeTracker off = new();
        off.Configure(1, 120, 500, liftDetection: false, liftGapMs: 40);
        for (int i = 0; i < 60; i++)
        {
            off.AddDelta(new MouseDelta(20, 0, t0 + Ms(i)));
        }

        off.AddDelta(new MouseDelta(20, 0, t0 + Ms(59 + 70)));
        r.Check("lift detection can be disabled", off.StrokeCount, 1);
    }

    private static void TestTrackerNaturalStop(Report r)
    {
        SwipeTracker t = NewTracker();
        long t0 = Ms(40_000);
        for (int i = 0; i < 40; i++)
        {
            t.AddDelta(new MouseDelta(20, 0, t0 + Ms(i)));
        }

        // Decelerate smoothly to ~1 count/ms, then a 70 ms pause (longer than the lift gap,
        // shorter than the 120 ms swipe break). A hand that slowed down did not lift the mouse.
        for (int k = 0; k < 60; k++)
        {
            t.AddDelta(new MouseDelta(Math.Max(1, 20 - k / 3), 0, t0 + Ms(40 + k)));
        }
        long last = t0 + Ms(99);
        t.Update(last + Ms(70), 1, 1);
        t.AddDelta(new MouseDelta(5, 0, last + Ms(70)));
        r.Check("no lift on deceleration", t.LiftCount, 0L);
        r.Check("still one stroke", t.StrokeCount, 1);
    }

    private static void TestTrackerExpiry(Report r)
    {
        SwipeTracker t = NewTracker();
        long t0 = Ms(50_000);
        for (int i = 0; i < 100; i++)
        {
            t.AddDelta(new MouseDelta(3, 1, t0 + Ms(i)));
        }

        int total = t.PointCount;
        t.Update(t0 + Ms(550), 1, 1); // points older than t0+50ms are past the 500 ms lifetime
        r.Check("partially expired (older half gone)", t.PointCount > 0 && t.PointCount < total, true);
        r.Line($"    {total} points -> {t.PointCount} after 550 ms");
        t.Update(t0 + Ms(99 + 501), 1, 1);
        r.Check("all points expired", t.PointCount, 0);
        r.Check("all strokes released", t.StrokeCount, 0);
    }

    private static void TestTrackerBounded(Report r)
    {
        var t = new SwipeTracker(pointCapacity: 1024);
        t.Configure(1, 120, 5000);
        long t0 = Ms(60_000);
        for (int i = 0; i < 200_000; i++)
        {
            // 8 kHz for 25 s with frequent pauses to create many strokes.
            long ts = t0 + Ms(i * 0.125 + (i / 500) * 150);
            t.AddDelta(new MouseDelta(i % 7 - 3, i % 5 - 2, ts));
        }

        r.Check("points bounded by capacity", t.PointCount <= t.PointCapacity, true);
        r.Check("strokes bounded", t.StrokeCount <= SwipeTracker.MaxStrokes, true);
        int sum = 0;
        for (int s = 0; s < t.StrokeCount; s++)
        {
            sum += t.GetStroke(s).PointCount;
        }

        r.Check("stroke counts consistent with ring", sum, t.PointCount);
    }

    private static void TestTrackerViewFit(Report r)
    {
        SwipeTracker t = NewTracker();
        long t0 = Ms(70_000);
        for (int i = 0; i < 100; i++)
        {
            t.AddDelta(new MouseDelta(100, -30, t0 + Ms(i))); // 10000 counts = 12.5 radii
        }

        double half = 0.9;
        t.Update(t0 + Ms(100), half, half);
        SwipeStroke s = t.GetStroke(0);
        double lo = s.MinX * s.ViewScale + s.OffsetX;
        double hi = s.MaxX * s.ViewScale + s.OffsetX;
        double loY = s.MinY * s.ViewScale + s.OffsetY;
        double hiY = s.MaxY * s.ViewScale + s.OffsetY;
        r.Check("zoomed out", s.ViewScale < 0.2, true);
        r.Check("x inside area", lo >= -half - 1e-9 && hi <= half + 1e-9, true);
        r.Check("y inside area", loY >= -half - 1e-9 && hiY <= half + 1e-9, true);

        SwipeTracker small = NewTracker();
        small.AddDelta(new MouseDelta(4, 2, t0));
        small.AddDelta(new MouseDelta(3, 1, t0 + Ms(3)));
        small.Update(t0 + Ms(4), half, half);
        r.CheckClose("micro movement not zoomed", small.GetStroke(0).ViewScale, 1.0);
    }

    private static void TestSmoother(Report r)
    {
        const int n = 50;
        var x = new double[n];
        var y = new double[n];
        var t = new long[n];
        var rnd = new Random(1);
        for (int i = 0; i < n; i++)
        {
            x[i] = i + rnd.NextDouble();
            y[i] = rnd.NextDouble();
            t[i] = Ms(i * 2);
        }

        var ox = new double[n];
        var oy = new double[n];
        TrailSmoother.Smooth(x, y, t, ox, oy, Ms(10));
        r.CheckClose("start kept", ox[0], x[0]);
        r.CheckClose("head kept", ox[n - 1], x[n - 1]);
        r.Check("middle smoothed", Math.Abs(oy[n / 2] - y[n / 2]) < 0.5, true);
        TrailSmoother.Smooth(x, y, t, ox, oy, 0);
        r.CheckClose("strength 0 = raw", ox[17], x[17]);
    }

    private static void TestPlacement(Report r)
    {
        var monitors = WindowPlacementService.GetMonitors();
        r.Check("at least one monitor", monitors.Count >= 1, true);
        foreach (MonitorDescription m in monitors)
        {
            r.Line($"    monitor {m.Device} {m.Bounds.Left},{m.Bounds.Top} {m.Bounds.Width}x{m.Bounds.Height} primary={m.IsPrimary}");
        }

        var gone = new WindowPlacementSettings
        {
            X = 99_000, Y = -50_000, Width = 400, Height = 300, MonitorDevice = @"\\.\DISPLAY99",
            MonitorLeft = 98_000, MonitorTop = -50_000, MonitorWidth = 2560, MonitorHeight = 1440,
        };
        NativeMethods.RECT rect = WindowPlacementService.ComputeRestoreRect(gone, monitors);
        r.Check("missing monitor -> on an existing monitor", IsInsideAny(rect, monitors), true);
        r.Check("size kept", rect.Width == 400 && rect.Height == 300, true);

        var huge = new WindowPlacementSettings { X = 0, Y = 0, Width = 50_000, Height = 50_000 };
        rect = WindowPlacementService.ComputeRestoreRect(huge, monitors);
        r.Check("oversized clamped onto a monitor", IsInsideAny(rect, monitors), true);

        rect = WindowPlacementService.ComputeRestoreRect(null, monitors);
        r.Check("default placement on a monitor", IsInsideAny(rect, monitors), true);

        // Capture window: size is the capture resolution and must never be shrunk, even if it is
        // larger than the monitor (1080x1080 + title bar on a 1080p screen).
        var bigCapture = new WindowPlacementSettings { X = 300, Y = 300, Width = 1096, Height = 1119, MonitorDevice = monitors[0].Device,
            MonitorLeft = monitors[0].Bounds.Left, MonitorTop = monitors[0].Bounds.Top, MonitorWidth = monitors[0].Bounds.Width, MonitorHeight = monitors[0].Bounds.Height };
        rect = WindowPlacementService.ComputeRestoreRect(bigCapture, monitors, shrinkToFit: false);
        r.Check("capture window size never shrunk", rect.Width == 1096 && rect.Height == 1119, true);
        r.Check("capture window title bar on screen", rect.Left >= monitors[0].Bounds.Left && rect.Top >= monitors[0].Bounds.Top
                                                      && rect.Left < monitors[0].Bounds.Right && rect.Top < monitors[0].Bounds.Bottom, true);
        rect = WindowPlacementService.ComputeRestoreRect(null, monitors, shrinkToFit: false, defaultWidth: 816, defaultHeight: 839);
        r.Check("capture default size = requested outer size", rect.Width == 816 && rect.Height == 839, true);
    }

    private static bool IsInsideAny(NativeMethods.RECT rect, IReadOnlyList<MonitorDescription> monitors)
    {
        foreach (MonitorDescription m in monitors)
        {
            if (rect.Left >= m.Bounds.Left && rect.Top >= m.Bounds.Top && rect.Right <= m.Bounds.Right && rect.Bottom <= m.Bounds.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task TestRawInputAsync(Report r, int inputSeconds)
    {
        using var input = new RawMouseInput();
        long events = 0, sumX = 0, sumY = 0;
        input.DeltaReceived += d =>
        {
            Interlocked.Increment(ref events);
            Interlocked.Add(ref sumX, d.Dx);
            Interlocked.Add(ref sumY, d.Dy);
        };

        input.Start();
        r.Check("thread running", input.IsRunning, true);
        r.Check("registered with RIDEV_INPUTSINK to our HWND", input.VerifyRegistration(out string details), true);
        r.Line($"    {details}");

        if (inputSeconds > 0)
        {
            r.Line($"    Listening for WM_INPUT for {inputSeconds} s (move the mouse)...");
            await Task.Delay(TimeSpan.FromSeconds(inputSeconds));
            r.Line($"    packets={input.TotalEvents} movement events={Interlocked.Read(ref events)} sum dx={Interlocked.Read(ref sumX)} dy={Interlocked.Read(ref sumY)}");
            r.Check("received WM_INPUT mouse movement", Interlocked.Read(ref events) > 0, true);
        }
        else
        {
            r.Line("    (movement capture skipped; use --selftest-input-seconds N)");
        }
    }

    private static void TestCaptureSettings(Report r)
    {
        var defaults = new AppSettings();
        r.Check("CaptureWidth default", defaults.CaptureWidth, 800);
        r.Check("CaptureHeight default", defaults.CaptureHeight, 800);
        r.Check("ChromaKeyColor default", defaults.ChromaKeyColor, "#00FF00");
        r.Check("CaptureBackgroundEnabled default", defaults.CaptureBackgroundEnabled, true);
        r.Check("IncludeDebugInCapture default", defaults.IncludeDebugInCapture, false);
        r.Check("chroma background colour", defaults.GetCaptureBackgroundColor(), Color.FromRgb(0, 255, 0));
        r.Check("chroma-safe rendering by default", defaults.UsesChromaSafeRendering, true);
        var noKey = new AppSettings { CaptureBackgroundEnabled = false };
        r.Check("no-key background is black", noKey.GetCaptureBackgroundColor(), Color.FromRgb(0, 0, 0));
        r.Check("no chroma-safe without key background", noKey.UsesChromaSafeRendering, false);

        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"CaptureWidth\": 5, \"CaptureHeight\": 99999, \"ChromaKeyColor\": \"nope\" }");
            AppSettings clamped = new SettingsService(path).Load();
            r.Check("CaptureWidth clamped", clamped.CaptureWidth, AppSettings.MinCaptureSize);
            r.Check("CaptureHeight clamped", clamped.CaptureHeight, AppSettings.MaxCaptureSize);
            r.Check("ChromaKeyColor reset", clamped.ChromaKeyColor, AppSettings.DefaultChromaKeyColor);

            // A schema-1 file from the desktop-overlay version must still load.
            File.WriteAllText(path,
                "{ \"SchemaVersion\": 1, \"TrailThickness\": 7, \"OverlayOpacity\": 0.5, \"AlwaysOnTop\": true, " +
                "\"BackgroundMode\": \"Transparent\", \"ToggleModeHotkey\": \"Ctrl+Shift+F10\", \"StartInOverlayMode\": true }");
            var service = new SettingsService(path);
            AppSettings legacy = service.Load();
            r.Check("legacy file status", service.LastLoadStatus, SettingsLoadStatus.Loaded);
            r.Check("legacy value kept", legacy.TrailThickness, 7.0);
            r.Check("legacy file gets capture defaults", legacy.CaptureWidth == 800 && legacy.CaptureBackgroundEnabled, true);
            r.Check("schema upgraded", legacy.SchemaVersion, AppSettings.CurrentSchemaVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Creates, shows and sizes a preview/capture window like the app does.</summary>
    private static async Task<CaptureWindow> OpenCaptureWindowAsync(AppSettings settings, PreviewFrameStore frames)
    {
        var window = new CaptureWindow { Left = 60, Top = 60 };
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        window.ApplySettings(settings);
        window.AttachFrames(frames);
        window.Show();
        window.ApplyExactClientSize();
        await Task.Delay(150);
        return window;
    }
    private static async Task TestCaptureWindowAsync(Report r)
    {
        var settings = new AppSettings { OutputMode = OutputMode.ObsCaptureWindow };
        CaptureWindow window = await OpenCaptureWindowAsync(settings, new PreviewFrameStore());
        try
        {
            IntPtr hwnd = window.Handle;
            CaptureWindowReport report = CaptureWindowInspector.Inspect(hwnd);
            foreach (string line in report.Describe().Split('\n'))
            {
                r.Line("    " + line);
            }

            r.Check("normal HWND", hwnd != IntPtr.Zero, true);
            r.Check("stable title", report.Title, CaptureWindow.CaptureTitle);
            r.Check("visible", report.Visible, true);
            r.Check("has caption (normal top-level window)", (report.Style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION, true);
            r.Check("not WS_CHILD", (report.Style & NativeMethods.WS_CHILD) == 0, true);
            r.Check("not WS_EX_TOOLWINDOW", (report.ExStyle & NativeMethods.WS_EX_TOOLWINDOW) == 0, true);
            r.Check("not WS_EX_LAYERED", (report.ExStyle & NativeMethods.WS_EX_LAYERED) == 0, true);
            r.Check("not WS_EX_TRANSPARENT (not click-through)", (report.ExStyle & NativeMethods.WS_EX_TRANSPARENT) == 0, true);
            r.Check("not WS_EX_NOACTIVATE", (report.ExStyle & NativeMethods.WS_EX_NOACTIVATE) == 0, true);
            r.Check("not topmost", report.IsTopmost, false);
            r.Check("no owner window", report.Owner, IntPtr.Zero);
            r.Check("not cloaked", report.Cloaked, false);
            r.Check("client area 800x800 physical px", $"{report.ClientWidth}x{report.ClientHeight}", "800x800");
            r.Check("passes OBS window filter", report.IsObsListable, true);
            r.Check("no OBS problems", report.Problems().Count, 0);

            List<(IntPtr Hwnd, string Title)> candidates = CaptureWindowInspector.EnumerateObsCandidates();
            bool listed = false;
            foreach ((IntPtr h, string title) in candidates)
            {
                listed |= h == hwnd && title == CaptureWindow.CaptureTitle;
            }

            r.Check("listed among OBS Window Capture candidates", listed, true);
            r.Line($"    ({candidates.Count} windows pass the OBS filter on this desktop)");

            foreach ((int w, int h) in new[] { (400, 400), (600, 600), (1080, 1080), (1280, 720), (800, 800) })
            {
                settings.CaptureWidth = w;
                settings.CaptureHeight = h;
                window.ApplySettings(settings);
                await Task.Delay(80);
                window.UpdateLayout();
                CaptureWindowReport sized = CaptureWindowInspector.Inspect(hwnd);
                r.Check($"capture {w}x{h}: client area", $"{sized.ClientWidth}x{sized.ClientHeight}", $"{w}x{h}");
                r.Check($"capture {w}x{h}: canvas units", $"{window.CaptureSurface.ActualWidth}x{window.CaptureSurface.ActualHeight}", $"{w}x{h}");
            }

            r.Check("title unchanged after resizes", NativeMethods.GetWindowTitle(hwnd), CaptureWindow.CaptureTitle);

            settings.OutputMode = OutputMode.NativeVirtualCamera;
            window.ApplySettings(settings);
            r.Check("title is the application name in camera mode too", NativeMethods.GetWindowTitle(hwnd), "Mouse Swipe Visualizer");
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ engine test helpers

    /// <summary>Frame sink standing in for the camera: always active, records what it receives.</summary>
    private sealed class TestOutput : IFrameOutput
    {
        private readonly object _gate = new();
        private uint[] _last = Array.Empty<uint>();

        public volatile bool Active = true;

        public int Width { get; set; }

        public int Height { get; set; }

        public int Fps { get; set; } = 60;

        public bool IsActive => Active;

        public int RequestedWidth => Width;

        public int RequestedHeight => Height;

        public int RequestedFps => Fps;

        public long Frames;
        public int LastWidth;
        public int LastHeight;
        public ulong LastHash;

        public void Publish(ReadOnlySpan<uint> pixels, int width, int height, long sequence)
        {
            lock (_gate)
            {
                if (_last.Length < width * height)
                {
                    _last = new uint[width * height];
                }

                pixels[..(width * height)].CopyTo(_last);
                LastWidth = width;
                LastHeight = height;
                ulong hash = 1469598103934665603UL;
                for (int i = 0; i < width * height; i += 7)
                {
                    hash = (hash ^ pixels[i]) * 1099511628211UL;
                }

                LastHash = hash;
                Frames++;
            }
        }

        public void Tick()
        {
        }

        public uint[] Snapshot(out int width, out int height)
        {
            lock (_gate)
            {
                width = LastWidth;
                height = LastHeight;
                return _last.AsSpan(0, width * height).ToArray();
            }
        }
    }

    /// <summary>The production pipeline without Raw Input: buffer → engine → (test output, preview).</summary>
    private sealed class TestPipeline : IDisposable
    {
        public TestPipeline(AppSettings settings, int outputWidth = 0, int outputHeight = 0)
        {
            Output = new TestOutput { Width = outputWidth, Height = outputHeight };
            Engine = new SwipeEngine(Buffer, settings, Output, Preview);
            Engine.Start();
        }

        public MouseDeltaBuffer Buffer { get; } = new();

        public PreviewFrameStore Preview { get; } = new();

        public TestOutput Output { get; }

        public SwipeEngine Engine { get; }

        public void Dispose() => Engine.Dispose();
    }

    public enum Gesture
    {
        RightFlick,
        LeftFlick,
        Curve,
        Lift,
    }

    /// <summary>
    /// Pushes a synthetic gesture into the delta buffer - the same entry point Raw Input uses -
    /// with 1 kHz timestamps ending "now", so the engine sees a complete, fresh stroke.
    /// </summary>
    public static void PushGesture(MouseDeltaBuffer buffer, Gesture gesture)
    {
        long now = MonotonicClock.Now;
        const int n = 150;
        switch (gesture)
        {
            case Gesture.RightFlick:
            case Gesture.LeftFlick:
                int sign = gesture == Gesture.RightFlick ? 1 : -1;
                for (int i = 0; i < n; i++)
                {
                    int speed = (int)Math.Round(2 + 8 * Math.Sin(Math.PI * i / n));
                    buffer.Push(new MouseDelta(sign * speed, 0, now - Ms(n - i)));
                }

                break;

            case Gesture.Curve:
                // Right, bending upwards (screen y negative).
                for (int i = 0; i < n; i++)
                {
                    double angle = Math.PI / 2 * i / n;
                    buffer.Push(new MouseDelta((int)Math.Round(7 * Math.Cos(angle)), (int)Math.Round(-7 * Math.Sin(angle)), now - Ms(n - i)));
                }

                break;

            case Gesture.Lift:
                // Fast right swipe, sensor cut-out (70 ms in the air), then a new swipe downwards.
                for (int i = 0; i < 60; i++)
                {
                    buffer.Push(new MouseDelta(20, 0, now - Ms(200 - i)));
                }

                for (int i = 0; i < 60; i++)
                {
                    buffer.Push(new MouseDelta(0, 8, now - Ms(70 - i)));
                }

                break;
        }
    }

    private readonly record struct PixelCensus(int Key, int Trail, int Halo);

    /// <summary>
    /// Classifies pixels (0xAARRGGBB) against a green key: exact key, "halo" (green-dominant but not
    /// the key: what a chroma key leaves as a fringe), or trail/outline.
    /// </summary>
    private static PixelCensus CountPixels(ReadOnlySpan<uint> pixels)
    {
        int key = 0, trail = 0, halo = 0;
        foreach (uint p in pixels)
        {
            int rr = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
            if (g >= 250 && rr <= 5 && b <= 5)
            {
                key++;
            }
            else if (g - Math.Max(rr, b) > 48)
            {
                halo++;
            }
            else
            {
                trail++;
            }
        }

        return new PixelCensus(key, trail, halo);
    }

    private static PixelCensus CountPixels(byte[] bgra)
    {
        var pixels = new uint[bgra.Length / 4];
        Buffer.BlockCopy(bgra, 0, pixels, 0, pixels.Length * 4);
        return CountPixels(pixels);
    }

    /// <summary>Centroid of "newest trail" pixels (near-white = trail head colour in chroma-safe mode).</summary>
    private static (double X, double Y, int Count) HeadCentroid(ReadOnlySpan<uint> pixels, int width)
    {
        double sx = 0, sy = 0;
        int count = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            if (((p >> 16) & 0xFF) >= 235 && ((p >> 8) & 0xFF) >= 235 && (p & 0xFF) >= 235)
            {
                sx += i % width;
                sy += i / width;
                count++;
            }
        }

        return count == 0 ? (double.NaN, double.NaN, 0) : (sx / count, sy / count, count);
    }

    private static void SavePng(ReadOnlySpan<uint> pixels, int width, int height, string name)
    {
        if (_imageDirectory == null || width == 0 || height == 0)
        {
            return;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels.ToArray(), width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream file = File.Create(Path.Combine(_imageDirectory, name));
        encoder.Save(file);
    }

    /// <summary>Folder next to the report where rendered frames are saved for inspection.</summary>
    private static string? _imageDirectory;

    /// <summary>Renders a gesture straight through tracker → model → rasterizer (no engine thread).</summary>
    private static SoftwareRasterizer RenderGesture(AppSettings settings, Gesture? gesture, int width, int height, KeyboardState? keyboard = null)
    {
        var buffer = new MouseDeltaBuffer();
        if (gesture is Gesture g)
        {
            PushGesture(buffer, g);
        }

        var tracker = new SwipeTracker();
        tracker.Configure(settings.SensitivityScale, settings.SwipeBreakMs, settings.TrailLifetimeMs, settings.LiftDetectionEnabled, settings.LiftGapMs);
        var builder = new SwipeModelBuilder();
        builder.Configure(settings);
        foreach (MouseDelta d in buffer.Drain())
        {
            tracker.AddDelta(d);
        }

        long now = MonotonicClock.Now;
        (double hx, double hy) = builder.GetHalfExtents(width, height);
        tracker.Update(now, hx, hy);
        var model = new SwipeRenderModel();
        builder.Build(tracker, width, height, now, model, keyboard);
        var raster = new SoftwareRasterizer();
        raster.Render(model);
        return raster;
    }

    /// <summary>BGRA → NV12 (BT.601 limited range, 2x2 chroma average) → BGRA, as a camera pipeline does.</summary>
    private static uint[] Nv12RoundTrip(ReadOnlySpan<uint> bgra, int width, int height)
    {
        var y = new byte[width * height];
        var uv = new byte[width * height / 2];
        for (int i = 0; i < width * height; i++)
        {
            uint p = bgra[i];
            int r = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
            y[i] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
        }

        for (int by = 0; by < height / 2; by++)
        {
            for (int bx = 0; bx < width / 2; bx++)
            {
                int su = 0, sv = 0;
                for (int k = 0; k < 4; k++)
                {
                    uint p = bgra[(by * 2 + k / 2) * width + bx * 2 + k % 2];
                    int r = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
                    su += ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    sv += ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                }

                uv[by * width + bx * 2] = (byte)Math.Clamp((su + 2) / 4, 0, 255);
                uv[by * width + bx * 2 + 1] = (byte)Math.Clamp((sv + 2) / 4, 0, 255);
            }
        }

        var result = new uint[width * height];
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                int c = y[py * width + px] - 16;
                int d = uv[(py / 2) * width + (px / 2) * 2] - 128;
                int e = uv[(py / 2) * width + (px / 2) * 2 + 1] - 128;
                int r = Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
                int g = Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                int b = Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);
                result[py * width + px] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }

        return result;
    }

    /// <summary>Same classification as <see cref="CountPixels(ReadOnlySpan{uint})"/>, tolerant of YUV rounding of the key.</summary>
    private static PixelCensus CountPixelsDecoded(ReadOnlySpan<uint> pixels)
    {
        int key = 0, trail = 0, halo = 0;
        foreach (uint p in pixels)
        {
            int rr = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
            if (g >= 240 && rr <= 20 && b <= 20)
            {
                key++;
            }
            else if (g - Math.Max(rr, b) > 48)
            {
                halo++;
            }
            else
            {
                trail++;
            }
        }

        return new PixelCensus(key, trail, halo);
    }

    // ------------------------------------------------------------------ Milestone A: off-screen rendering

    private static void TestOffscreenRasterizer(Report r)
    {
        var settings = SwipeOnly();
        foreach ((int w, int h) in new[] { (800, 800), (1280, 720) })
        {
            SoftwareRasterizer raster = RenderGesture(settings, Gesture.Curve, w, h);
            PixelCensus census = CountPixels(raster.Pixels);
            r.Line($"    {w}x{h} curve: key {census.Key}, trail {census.Trail}, halo {census.Halo}, shaded {raster.LastShadedPixels}");
            r.Check($"{w}x{h}: frame size", $"{raster.Width}x{raster.Height}", $"{w}x{h}");
            r.Check($"{w}x{h}: swipe drawn without any window", census.Trail > 500, true);
            r.Check($"{w}x{h}: background is the chroma key", census.Key > w * h * 0.9, true);
            r.Check($"{w}x{h}: no green-halo pixels (chroma-safe)", census.Halo, 0);
            uint[] decoded = Nv12RoundTrip(raster.Pixels, w, h);
            PixelCensus nv12 = CountPixelsDecoded(decoded);
            r.Line($"    {w}x{h} after BGRA→NV12→BGRA: key {nv12.Key}, trail {nv12.Trail}, halo {nv12.Halo}");
            r.Check($"{w}x{h}: no halo after NV12 chroma subsampling", nv12.Halo, 0);
            SavePng(raster.Pixels, w, h, $"selftest-offscreen-{w}x{h}.png");
            SavePng(decoded, w, h, $"selftest-offscreen-{w}x{h}-nv12.png");
        }

        var plain = SwipeOnly(new AppSettings { ChromaSafeEdges = false });
        PixelCensus alpha = CountPixels(RenderGesture(plain, Gesture.Curve, 800, 800).Pixels);
        r.Line($"    plain alpha mode for comparison: trail {alpha.Trail}, halo {alpha.Halo}");
        r.Check("halo metric detects plain alpha blending (sanity)", alpha.Halo > 0, true);

        var noOutline = SwipeOnly(new AppSettings { OutlineEnabled = false });
        PixelCensus bare = CountPixels(RenderGesture(noOutline, Gesture.Curve, 800, 800).Pixels);
        r.Check("chroma-safe without outline: no halo either", bare.Halo, 0);
    }

    /// <summary>Swipe over the whole canvas (no keyboard, no frame), as the original swipe tests assume.</summary>
    private static AppSettings SwipeOnly(AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        settings.KeyboardEnabled = false;
        settings.FrameEnabled = false;
        settings.SwipeBoxEnabled = false;
        return settings;
    }

    /// <summary>Counts pixels that differ between two frames, inside and outside a rectangle.</summary>
    private static int CountDiff(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int width, RectD inside, out int outside)
    {
        int inCount = 0;
        outside = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                continue;
            }

            int x = i % width, y = i / width;
            if (x >= inside.X && x < inside.Right && y >= inside.Y && y < inside.Bottom)
            {
                inCount++;
            }
            else
            {
                outside++;
            }
        }

        return inCount;
    }

    private static void TestKeyboardPanel(Report r)
    {
        var settings = new AppSettings(); // defaults: keyboard left, 40 %, frame on
        const int w = 1280, h = 720;
        OverlayStyle style = OverlayStyle.From(settings);
        OverlayLayout layout = OverlayLayout.Compute(w, h, style);
        double split = layout.Keyboard.Width / (layout.Keyboard.Width + layout.Swipe.Width);
        r.Line(string.Create(CultureInfo.InvariantCulture,
            $"    {w}x{h}: frame {layout.Frame}, keyboard {layout.Keyboard}, swipe {layout.Swipe}, split {split:0.000}"));
        r.Check("default split is 40/60 with the keyboard on the left", Math.Abs(split - 0.40) < 0.02 && layout.Keyboard.Right < layout.Swipe.X, true);
        r.Check("keyboard and swipe inside the frame", layout.Keyboard.X > layout.Frame.X && layout.Swipe.Right < layout.Frame.Right
            && layout.Keyboard.Y > layout.Frame.Y && layout.Swipe.Bottom < layout.Frame.Bottom, true);
        RectD box = layout.SwipeBox;
        r.Line(string.Create(CultureInfo.InvariantCulture, $"    mouse box {box}"));
        r.Check("mouse area frame surrounds the swipe area", !box.IsEmpty && box.X < layout.Swipe.X && box.Y < layout.Swipe.Y
            && box.Right > layout.Swipe.Right && box.Bottom > layout.Swipe.Bottom, true);
        r.Check("mouse area frame does not overlap the keyboard", layout.Keyboard.Right <= box.X, true);
        OverlayLayout small = OverlayLayout.Compute(w, h, style with { SwipeBoxWidth = 0.5, SwipeBoxHeight = 0.6 });
        r.Check("mouse box 50 % x 60 %: box resized and centred", Math.Abs(small.SwipeBox.Width - box.Width * 0.5) <= 2
            && Math.Abs(small.SwipeBox.Height - box.Height * 0.6) <= 2 && Math.Abs(small.SwipeBox.CenterX - box.CenterX) <= 2, true);
        r.Line(string.Create(CultureInfo.InvariantCulture,
            $"    swipe radius {layout.SwipeRadius:0} px, key unit {layout.KeyUnit:0.0} px; mouse box 50x60 % → radius {small.SwipeRadius:0}"));
        r.Check("mouse box resize leaves the keyboard alone", small.Keyboard == layout.Keyboard && small.KeyUnit == layout.KeyUnit, true);
        r.Check("mouse box hitting the swipe: swipe shrinks to fit (not cut off)", small.SwipeRadius < layout.SwipeRadius
            && small.SwipeRadius <= Math.Min(small.Swipe.Width, small.Swipe.Height) / 2 + 0.01, true);
        OverlayLayout slightly = OverlayLayout.Compute(w, h, style with { SwipeBoxWidth = 0.95 });
        r.Check("mouse box 95 % wide (still room): swipe keeps its size", slightly.SwipeRadius, layout.SwipeRadius);
        r.Check("mouse box 95 % wide: box really narrower", slightly.SwipeBox.Width < box.Width - 20, true);

        OverlayLayout shortFrame = OverlayLayout.Compute(w, h, style with { FrameHeight = 0.75 });
        r.Line(string.Create(CultureInfo.InvariantCulture,
            $"    frame 100x75 %: frame {shortFrame.Frame.Width}x{shortFrame.Frame.Height}, key unit {shortFrame.KeyUnit:0.0}, swipe radius {shortFrame.SwipeRadius:0}"));
        r.Check("frame height 75 %: frame shorter and centred", Math.Abs(shortFrame.Frame.Height - layout.Frame.Height * 0.75) <= 2
            && Math.Abs(shortFrame.Frame.CenterY - layout.Frame.CenterY) <= 2, true);
        r.Check("frame height 75 % (keyboard still fits): keys keep their size", shortFrame.KeyUnit, layout.KeyUnit);
        r.Check("frame height 75 %: swipe shrinks only as far as the edge forces", shortFrame.SwipeRadius <= layout.SwipeRadius
            && Math.Abs(shortFrame.SwipeRadius - Math.Min(shortFrame.Swipe.Width, shortFrame.Swipe.Height) / 2) < 1, true);
        OverlayLayout narrowFrame = OverlayLayout.Compute(w, h, style with { FrameWidth = 0.6 });
        r.Check("frame width 60 %: keys shrink to fit, never past the keyboard area", narrowFrame.KeyUnit < layout.KeyUnit
            && narrowFrame.KeyUnit * KeyboardLayout.WidthUnits <= narrowFrame.Keyboard.Width + 0.01, true);
        OverlayLayout tallFrame = OverlayLayout.Compute(w, h, style with { FrameHeight = 1, FrameWidth = 1 });
        r.Check("frame at 100 %: identical to the default layout", tallFrame, layout);
        r.Check("mouse area frame off: no box", OverlayLayout.Compute(w, h, style with { SwipeBoxEnabled = false }).SwipeBox.IsEmpty, true);
        foreach (KeyboardPosition position in Enum.GetValues<KeyboardPosition>())
        {
            OverlayLayout l = OverlayLayout.Compute(w, h, style with { KeyboardPosition = position });
            bool ok = position switch
            {
                KeyboardPosition.Left => l.Keyboard.Right <= l.Swipe.X,
                KeyboardPosition.Right => l.Swipe.Right <= l.Keyboard.X,
                KeyboardPosition.Top => l.Keyboard.Bottom <= l.Swipe.Y,
                _ => l.Swipe.Bottom <= l.Keyboard.Y,
            };
            r.Check($"position {position}: keyboard and swipe side by side, no overlap", ok && !l.Keyboard.IsEmpty && !l.Swipe.IsEmpty, true);
        }

        OverlayLayout wide = OverlayLayout.Compute(w, h, style with { KeyboardSplit = 0.6 });
        r.Check("split setting moves the divider (60 %)", Math.Abs(wide.Keyboard.Width / (wide.Keyboard.Width + wide.Swipe.Width) - 0.6) < 0.02, true);

        // Key presses through the real model builder + rasterizer.
        var keyboard = new KeyboardState();
        var tracker = new SwipeTracker();
        tracker.Configure(settings.SensitivityScale, settings.SwipeBreakMs, settings.TrailLifetimeMs, settings.LiftDetectionEnabled, settings.LiftGapMs);
        var builder = new SwipeModelBuilder();
        builder.Configure(settings);
        var model = new SwipeRenderModel();
        var raster = new SoftwareRasterizer();
        uint[] Render()
        {
            builder.Build(tracker, w, h, MonotonicClock.Now, model, keyboard);
            raster.Render(model);
            return raster.Pixels.ToArray();
        }

        uint[] idle = Render();
        r.Check("idle: no swipe geometry, frame + keyboard still drawn", model.IsEmpty && CountPixels(idle).Trail > w * h / 4, true);
        uint boxBorder = idle[(int)(model.Layout.SwipeBox.CenterY) * w + (int)model.Layout.SwipeBox.X];
        r.Check("mouse area frame border drawn (left edge, mid-height)", boxBorder & 0xFFFFFFu, 0xE6E6E6u);
        SavePng(idle, w, h, "selftest-keyboard-idle.png");
        int baseBuilds = raster.BaseLayerBuilds, keyBuilds = raster.KeyboardLayerBuilds;
        Render();
        r.Check("unchanged frame: static layers reused", raster.BaseLayerBuilds == baseBuilds && raster.KeyboardLayerBuilds == keyBuilds, true);

        var keys = new RectD[KeyboardLayout.Keys.Count];
        OverlayLayout.ComputeKeys(model.Layout.Keyboard, keys, out double unit, model.Layout.KeyUnit);
        r.Line(string.Create(CultureInfo.InvariantCulture, $"    key unit {unit:0.0} px"));
        RectD wKey = keys[KeyboardLayout.KeyIndexForScanCode(0x11)];
        keyboard.OnKey(0x11, true);
        uint[] pressed = Render();
        int inside = CountDiff(idle, pressed, w, wKey, out int outside);
        r.Line($"    W pressed: {inside} changed pixels inside the key ({(int)(wKey.Width * wKey.Height)} px), {outside} outside");
        r.Check("W press lights up the W key", inside > wKey.Width * wKey.Height * 0.5, true);
        r.Check("W press changes nothing outside the W key", outside, 0);
        r.Check("key press rebuilds only the keyboard layer", raster.BaseLayerBuilds == baseBuilds && raster.KeyboardLayerBuilds == keyBuilds + 1, true);
        long version = keyboard.Version;
        keyboard.OnKey(0x11, true);
        r.Check("auto-repeat does not bump the version", keyboard.Version, version);
        keyboard.OnKey(0x24, true);
        keyboard.OnKey(0x24, false);
        r.Check("non-overlay key (J) is ignored", keyboard.Version, version);
        keyboard.OnKey(0x11, false);
        r.Check("W release restores the idle frame exactly", Render().AsSpan().SequenceEqual(idle), true);

        int shift = KeyboardLayout.KeyIndexForScanCode(0x2A), ctrl = KeyboardLayout.KeyIndexForScanCode(0x1D);
        keyboard.OnKey(0x2A, true);
        keyboard.OnKey(0x36, true);
        keyboard.OnKey(0x2A, false);
        r.Check("left+right Shift: releasing one keeps Shift lit", keyboard.IsDown(shift), true);
        keyboard.OnKey(0x36, false);
        r.Check("both Shifts released: Shift off", keyboard.IsDown(shift), false);
        keyboard.OnKey(0xE01D, true);
        r.Check("right Ctrl (E0 1D) lights the Ctrl key", keyboard.IsDown(ctrl), true);
        keyboard.OnKey(0xE01D, false);

        // Swipe inside its area, chroma-safe with frame and keyboard, for a few layouts.
        var held = new KeyboardState();
        foreach (int code in new[] { 0x11, 0x1E, 0x2A })
        {
            held.OnKey(code, true);
        }

        foreach ((string name, AppSettings s) in new[]
        {
            ("left", new AppSettings()),
            ("bottom", new AppSettings { KeyboardPosition = KeyboardPosition.Bottom }),
            ("no frame", new AppSettings { FrameEnabled = false }),
            ("dot head", new AppSettings { HeadStyle = HeadStyle.Dot }),
            ("no mouse box", new AppSettings { SwipeBoxEnabled = false }),
            ("mouse box only", new AppSettings { FrameEnabled = false, KeyboardEnabled = false }),
            ("small mouse box", new AppSettings { SwipeBoxWidthPercent = 45, SwipeBoxHeightPercent = 40 }),
            ("small frame", new AppSettings { FrameWidthPercent = 80, FrameHeightPercent = 70 }),
        })
        {
            SoftwareRasterizer swipe = RenderGesture(s, Gesture.Curve, w, h, held);
            SoftwareRasterizer still = RenderGesture(s, null, w, h, held);
            OverlayLayout l = OverlayLayout.Compute(w, h, OverlayStyle.From(s));
            RectD allowed = l.SwipeClip.IsEmpty ? l.Swipe : l.SwipeClip;
            int trail = CountDiff(still.Pixels, swipe.Pixels, w, allowed, out int escaped);
            PixelCensus census = CountPixels(swipe.Pixels);
            PixelCensus nv12 = CountPixelsDecoded(Nv12RoundTrip(swipe.Pixels, w, h));
            r.Line($"    {name}: swipe pixels {trail} inside the swipe area, {escaped} outside; halo {census.Halo}, after NV12 {nv12.Halo}");
            r.Check($"{name}: swipe drawn inside its area only", trail > 500 && escaped == 0, true);
            r.Check($"{name}: no green halo (frame/key edges chroma-safe)", census.Halo, 0);
            r.Check($"{name}: no halo after NV12 subsampling", nv12.Halo, 0);
            SavePng(swipe.Pixels, w, h, $"selftest-keyboard-{name.Replace(' ', '-')}.png");
        }
    }

    private static int Brightness(uint p) => (int)((p >> 16) & 0xFF) + (int)((p >> 8) & 0xFF) + (int)(p & 0xFF);

    private static void TestGlassAndAntiAliasing(Report r)
    {
        const int w = 1280, h = 720;

        // 1) Anti-aliasing: on the mouse box the swipe edges blend with the box (grey) instead of stepping.
        var soft = new AppSettings { SwipeBoxFillColor = "#808080", TrailColor = "#FF0000", KeyboardEnabled = false };
        ReadOnlySpan<uint> before = RenderGesture(soft, null, w, h).Pixels;
        SoftwareRasterizer drawnRaster = RenderGesture(soft, Gesture.Curve, w, h);
        ReadOnlySpan<uint> after = drawnRaster.Pixels;
        int changed = 0, blended = 0;
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i] == before[i])
            {
                continue;
            }

            changed++;
            int rr = (int)((after[i] >> 16) & 0xFF), g = (int)((after[i] >> 8) & 0xFF), b = (int)(after[i] & 0xFF);
            if (g >= 0x1C && g <= 0x78 && Math.Abs(rr - g) < 8 && Math.Abs(b - g) < 8)
            {
                blended++; // between the box grey and the dark outline: an anti-aliased edge pixel
            }
        }

        r.Line($"    swipe on the mouse box: {changed} pixels changed, {blended} anti-aliased edge pixels");
        r.Check("swipe edges on a panel are anti-aliased", blended > 50, true);
        SavePng(after, w, h, "selftest-antialias.png");

        // 2) Background image: generated 4-colour picture (no green), cover-fitted and blurred.
        string dir = Path.Combine(Path.GetTempPath(), "MouseSwipeVisualizer-selftest");
        Directory.CreateDirectory(dir);
        string imagePath = WriteQuadrantImage(dir);

        static int MaxStep(ReadOnlySpan<uint> px, int width, int y, int x0, int x1)
        {
            int max = 0;
            for (int x = x0; x < x1; x++)
            {
                int a = (int)((px[y * width + x] >> 8) & 0xFF), b = (int)((px[y * width + x + 1] >> 8) & 0xFF);
                max = Math.Max(max, Math.Abs(a - b));
            }

            return max;
        }

        var glass = new AppSettings { BackgroundImagePath = imagePath, GlassEnabled = true };
        uint[] glassPx = RenderGesture(glass, null, w, h).Pixels.ToArray();
        uint outside = glassPx[h / 2 * w + 2] & 0xFFFFFFu;
        r.Check("outside the frame: still the chroma key", outside, 0x00FF00u);
        r.Check("inside the frame: the picture instead of the frame colour", Brightness(glassPx[12 * w + 100]) > 90, true);
        int sharpStep = MaxStep(RenderGesture(new AppSettings { BackgroundImagePath = imagePath, BackgroundImageBlur = 0 }, null, w, h).Pixels, w, 12, 600, 680);
        int blurStep = MaxStep(glassPx, w, 12, 600, 680);
        r.Line($"    picture edge inside the frame (top strip): max step {sharpStep} unblurred, {blurStep} blurred");
        r.Check("image covers the canvas (colour edge visible unblurred)", sharpStep > 40, true);
        r.Check("image is blurred", blurStep < 20, true);

        // 3) Glass: the mouse box shows the tinted picture instead of its solid fill.
        OverlayLayout layout = OverlayLayout.Compute(w, h, OverlayStyle.From(glass));
        int cx = (int)layout.SwipeBox.CenterX, cy = (int)layout.SwipeBox.CenterY;
        uint solid = RenderGesture(new AppSettings { BackgroundImagePath = imagePath }, null, w, h).Pixels[cy * w + cx];
        uint glassy = glassPx[cy * w + cx];
        r.Line($"    mouse box centre: solid 0x{solid:X8}, glass 0x{glassy:X8}");
        r.Check("glass off: mouse box shows the picture, not its solid colour", Brightness(solid) > 60, true);
        r.Check("glass on: mouse box is the lighter, tinted picture", Brightness(glassy) > Brightness(solid) + 20, true);

        // 4) Borders off: the box edge looks like its inside.
        int ex = (int)layout.SwipeBox.X + 1;
        int withBorder = Brightness(glassPx[cy * w + ex]) - Brightness(glassPx[cy * w + ex + 12]);
        uint[] noBorders = RenderGesture(new AppSettings { BackgroundImagePath = imagePath, GlassEnabled = true, BordersEnabled = false }, null, w, h).Pixels.ToArray();
        int withoutBorder = Brightness(noBorders[cy * w + ex]) - Brightness(noBorders[cy * w + ex + 12]);
        r.Line($"    box edge vs inside brightness: borders on {withBorder}, off {withoutBorder}");
        r.Check("borders on: light glass border visible", withBorder > 40, true);
        r.Check("borders off: no border", Math.Abs(withoutBorder) < 25, true);
        SavePng(glassPx, w, h, "selftest-glass-quadrants.png");

        // 5) Picture loaded once, not per frame; a missing file falls back to the plain background.
        var builder = new SwipeModelBuilder();
        builder.Configure(glass);
        var tracker = new SwipeTracker();
        var model = new SwipeRenderModel();
        var raster = new SoftwareRasterizer();
        for (int i = 0; i < 3; i++)
        {
            builder.Build(tracker, w, h, MonotonicClock.Now, model);
            raster.Render(model);
        }

        r.Check("picture layer built once for three frames", raster.BaseLayerBuilds, 1);
        uint missing = RenderGesture(new AppSettings { BackgroundImagePath = Path.Combine(dir, "missing.png") }, null, w, h).Pixels[12 * w + 100];
        r.Check("missing image: plain frame colour, no crash", missing & 0xFFFFFFu, 0u);
        SavePng(RenderGesture(new AppSettings { BackgroundImagePath = imagePath, FrameEnabled = false }, null, w, h).Pixels, w, h, "selftest-image-noframe.png");

        // Visual samples with a real photo when Windows has its default wallpaper.
        const string wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
        if (File.Exists(wallpaper))
        {
            var held = new KeyboardState();
            held.OnKey(0x11, true);
            held.OnKey(0x2A, true);
            SavePng(RenderGesture(new AppSettings { BackgroundImagePath = wallpaper, GlassEnabled = true }, Gesture.Curve, w, h, held).Pixels,
                w, h, "selftest-glass-wallpaper.png");
            SavePng(RenderGesture(new AppSettings { BackgroundImagePath = wallpaper, GlassEnabled = true, BordersEnabled = false }, Gesture.Curve, w, h, held).Pixels,
                w, h, "selftest-glass-noborders.png");
        }
    }

    /// <summary>640x360 picture with four colours (no green) for background-picture tests.</summary>
    private static string WriteQuadrantImage(string dir)
    {
        string imagePath = Path.Combine(dir, "quadrants.png");
        var quad = new uint[640 * 360];
        for (int y = 0; y < 360; y++)
        {
            for (int x = 0; x < 640; x++)
            {
                quad[y * 640 + x] = x < 320 ? (y < 180 ? 0xFFE03030u : 0xFF3040E0u) : (y < 180 ? 0xFFE0C020u : 0xFFC030C0u);
            }
        }

        using FileStream file = File.Create(imagePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, quad, 640 * 4)));
        encoder.Save(file);
        return imagePath;
    }

    private static async Task TestSpotifyAsync(Report r)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MouseSwipeVisualizer-selftest", "spotify");
        Directory.CreateDirectory(dir);

        // 1) Secrets: DPAPI-encrypted, plaintext never on disk, never in settings.json.
        const string secret = "selftest-secret-4f1c9a", refresh = "selftest-refresh-77b2e0";
        byte[] cipher = Spotify.Dpapi.Protect(Encoding.UTF8.GetBytes(secret));
        r.Check("DPAPI round trip", Encoding.UTF8.GetString(Spotify.Dpapi.Unprotect(cipher)), secret);
        string secretsFile = Path.Combine(dir, "spotify.dat");
        new Spotify.SpotifySecrets { ClientId = "id", ClientSecret = secret, RefreshToken = refresh }.Save(secretsFile);
        string raw = Encoding.UTF8.GetString(File.ReadAllBytes(secretsFile)) + Encoding.Unicode.GetString(File.ReadAllBytes(secretsFile));
        r.Check("secrets file does not contain the secret or token in plain text", raw.Contains(secret) || raw.Contains(refresh), false);
        Spotify.SpotifySecrets loaded = Spotify.SpotifySecrets.Load(secretsFile);
        r.Check("secrets load back", loaded.ClientSecret == secret && loaded.RefreshToken == refresh && loaded.ClientId == "id", true);
        File.WriteAllBytes(secretsFile, new byte[] { 1, 2, 3 });
        r.Check("corrupt secrets file: empty, no crash", Spotify.SpotifySecrets.Load(secretsFile).RefreshToken, null);
        Spotify.SpotifySecrets.Delete(secretsFile);
        string settingsJson = System.Text.Json.JsonSerializer.Serialize(new AppSettings { SpotifyClientId = "abc", SpotifyCoverEnabled = true });
        r.Check("settings.json has no secret/token fields", settingsJson.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || settingsJson.Contains("Token", StringComparison.OrdinalIgnoreCase), false);

        // 2) Sign-in URL and settings sanitizing.
        string url = Spotify.SpotifyClient.AuthorizeUrl("my-client", Spotify.SpotifyService.RedirectUri(8888), "st4te");
        r.Check("authorize URL: client, redirect, scopes, state", url.StartsWith("https://accounts.spotify.com/authorize?", StringComparison.Ordinal)
            && url.Contains("client_id=my-client") && url.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A8888%2Fcallback")
            && url.Contains("user-read-currently-playing") && url.Contains("state=st4te"), true, url);
        var s = new AppSettings { SpotifyPollSeconds = 0.2, SpotifyRedirectPort = 80 };
        s.Sanitize();
        r.Check("poll interval and port sanitized", s.SpotifyPollSeconds == AppSettings.MinSpotifyPollSeconds && s.SpotifyRedirectPort == AppSettings.DefaultSpotifyPort, true);
        var slow = new AppSettings { SpotifyPollSeconds = 900 };
        slow.Sanitize();
        r.Check("safety interval capped at 300 s", slow.SpotifyPollSeconds, AppSettings.MaxSpotifyPollSeconds);
        var legacy = new AppSettings { SchemaVersion = 4, SpotifyPollSeconds = 3 };
        legacy.Sanitize();
        var picked = new AppSettings { SchemaVersion = 4, SpotifyPollSeconds = 7 };
        picked.Sanitize();
        r.Check("old 3 s default migrates to 15 s; a chosen value is kept", legacy.SpotifyPollSeconds == 15 && picked.SpotifyPollSeconds == 7 && legacy.SchemaVersion == 5, true);

        // 3) Parsing /me/player/currently-playing.
        const string track = """
            {"is_playing":true,"item":{"type":"track","name":"Song","artists":[{"name":"A"},{"name":"B"}],
             "album":{"images":[{"url":"https://i.scdn.co/image/small","width":64},{"url":"https://i.scdn.co/image/big","width":640},{"url":"https://i.scdn.co/image/mid","width":300}]}}}
            """;
        Spotify.NowPlaying? now = Spotify.SpotifyClient.ParseCurrentlyPlaying(track);
        r.Check("track: title, artists, largest cover", now is { Title: "Song", Artist: "A, B", ImageUrl: "https://i.scdn.co/image/big", IsPlaying: true }, true, now?.ToString());
        const string episode = """
            {"is_playing":false,"item":{"type":"episode","name":"Ep","show":{"name":"Show","images":[{"url":"https://i.scdn.co/image/show","width":640}]},
             "images":[{"url":"https://i.scdn.co/image/ep","width":640}]}}
            """;
        Spotify.NowPlaying? ep = Spotify.SpotifyClient.ParseCurrentlyPlaying(episode);
        r.Check("episode: show name, episode cover, paused", ep is { Title: "Ep", Artist: "Show", ImageUrl: "https://i.scdn.co/image/ep", IsPlaying: false }, true, ep?.ToString());
        r.Check("nothing playing / ad / garbage → null", Spotify.SpotifyClient.ParseCurrentlyPlaying("""{"is_playing":true,"item":null}""") == null
            && Spotify.SpotifyClient.ParseCurrentlyPlaying("not json") == null && Spotify.SpotifyClient.ParseCurrentlyPlaying("") == null, true);
        r.Check("cover URLs only from Spotify CDNs over HTTPS",
            Spotify.SpotifyClient.IsAllowedImageUrl("https://i.scdn.co/image/ab67") && Spotify.SpotifyClient.IsAllowedImageUrl("https://image-cdn-ak.spotifycdn.com/image/x")
            && !Spotify.SpotifyClient.IsAllowedImageUrl("http://i.scdn.co/image/ab67") && !Spotify.SpotifyClient.IsAllowedImageUrl("https://i.scdn.co.evil.example/x")
            && !Spotify.SpotifyClient.IsAllowedImageUrl("https://evil.example/i.scdn.co") && !Spotify.SpotifyClient.IsAllowedImageUrl("file:///C:/x.png")
            && !Spotify.SpotifyClient.IsAllowedImageUrl("https://i.scdn.co:8443/x"), true);

        // 3b) Smart timing: check when the song ends, re-check a few times, safety interval otherwise.
        const string timed = """
            {"is_playing":true,"progress_ms":190000,"item":{"type":"track","id":"t1","name":"Song","duration_ms":200000,"artists":[],"album":{"images":[]}}}
            """;
        Spotify.NowPlaying? song = Spotify.SpotifyClient.ParseCurrentlyPlaying(timed);
        r.Check("parses song id, position and length", song is { Id: "t1", ProgressMs: 190000, DurationMs: 200000 }, true, song?.ToString());
        TimeSpan safety = TimeSpan.FromSeconds(15);
        var watch = default(Spotify.SongEndWatch);
        TimeSpan toEnd = Spotify.SpotifySchedule.NextDelay(song, safety, true, ref watch);
        r.Check("10 s left: next check right after the end (10.8 s)", Math.Abs(toEnd.TotalSeconds - 10.8) < 0.01, true, toEnd.ToString());
        Spotify.NowPlaying atEnd = song! with { ProgressMs = 199700 };
        var retries = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            retries.Add(Spotify.SpotifySchedule.NextDelay(atEnd, safety, true, ref watch).TotalSeconds);
        }

        r.Line($"    same song still reported at its end: re-checks after {string.Join(", ", retries)} s");
        r.Check("old song still reported: re-checks after 1, 2, 3 s, then the regular interval", retries.SequenceEqual(new[] { 1.0, 2.0, 3.0, 15.0 }), true);
        var next = new Spotify.NowPlaying("Next", "", null, true, "t2", 1000, 180000);
        r.Check("new song: regular interval while far from its end", Spotify.SpotifySchedule.NextDelay(next, safety, true, ref watch), safety);
        r.Check("paused: regular interval", Spotify.SpotifySchedule.NextDelay(song with { IsPlaying = false }, safety, true, ref watch), safety);
        r.Check("smart timing off: regular interval", Spotify.SpotifySchedule.NextDelay(song, TimeSpan.FromSeconds(3), false, ref watch), TimeSpan.FromSeconds(3));
        var repeat = default(Spotify.SongEndWatch);
        Spotify.SpotifySchedule.NextDelay(song, safety, true, ref repeat);
        TimeSpan replay = Spotify.SpotifySchedule.NextDelay(song with { ProgressMs = 5000 }, safety, true, ref repeat);
        r.Check("same song restarted (repeat/seek back): normal schedule, no re-check burst", replay, safety);

        // 4) OAuth redirect listener on 127.0.0.1 (random free port).
        int port;
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using (var listener = Spotify.LoopbackCallback.Start(port))
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            Task<Dictionary<string, string>> wait = listener.WaitAsync("/callback", TimeSpan.FromSeconds(10), CancellationToken.None);
            using System.Net.Http.HttpResponseMessage other = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
            r.Check("callback: other paths get 404 and keep waiting", other.StatusCode == System.Net.HttpStatusCode.NotFound && !wait.IsCompleted, true);
            using System.Net.Http.HttpResponseMessage cb = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=a%2Bb%20c&state=xyz");
            Dictionary<string, string> query = await wait;
            r.Check("callback: returns code and state, answers 200", cb.IsSuccessStatusCode && query.GetValueOrDefault("code") == "a+b c"
                && query.GetValueOrDefault("state") == "xyz", true);
        }

        bool busy = false;
        using (var first = Spotify.LoopbackCallback.Start(port))
        {
            try
            {
                using var second = Spotify.LoopbackCallback.Start(port);
            }
            catch (System.Net.Sockets.SocketException)
            {
                busy = true;
            }
        }

        r.Check("callback port in use is reported (not silently shared)", busy, true);

        // 5) The cover replaces the frame picture through the engine's override, and goes away again.
        string cover = WriteQuadrantImage(dir);
        const int w = 1280, h = 720;
        var builder = new SwipeModelBuilder();
        builder.Configure(new AppSettings { BackgroundFadeMs = 0 }); // override itself; the fade is tested below
        var tracker = new SwipeTracker();
        var model = new SwipeRenderModel();
        var raster = new SoftwareRasterizer();
        uint FramePixel()
        {
            builder.Build(tracker, w, h, MonotonicClock.Now, model);
            raster.Render(model);
            return raster.Pixels[12 * w + 100];
        }

        uint plain = FramePixel();
        builder.SetImageOverride(cover);
        uint withCover = FramePixel();
        builder.SetImageOverride(null);
        uint after = FramePixel();
        r.Check("cover override shows in the frame, then back to the frame colour",
            (plain & 0xFFFFFFu) == 0 && Brightness(withCover) > 90 && (after & 0xFFFFFFu) == 0, true, $"0x{plain:X8} 0x{withCover:X8} 0x{after:X8}");

        // 6) Crossfade when the cover changes: old → blend → new, then idle again; 0 ms = instant.
        long t0 = MonotonicClock.Now;
        uint RenderAt(SwipeModelBuilder b, SoftwareRasterizer rs, double ms)
        {
            b.Build(tracker, w, h, t0 + MonotonicClock.MsToTicks(ms), model);
            rs.Render(model);
            return rs.Pixels[12 * w + 100];
        }

        var fadeBuilder = new SwipeModelBuilder();
        fadeBuilder.Configure(new AppSettings { BackgroundFadeMs = 600 });
        var fadeRaster = new SoftwareRasterizer();
        fadeBuilder.SetImageOverride(cover);
        uint coverPx = RenderAt(fadeBuilder, fadeRaster, 0);
        r.Check("first picture appears without a fade (nothing to fade from yet)", fadeRaster.IsAnimating, false);
        fadeBuilder.SetImageOverride(null);
        uint start = RenderAt(fadeBuilder, fadeRaster, 1000);
        bool runningAtStart = fadeRaster.IsAnimating;
        uint mid = RenderAt(fadeBuilder, fadeRaster, 1300);
        bool runningMid = fadeRaster.IsAnimating;
        uint end = RenderAt(fadeBuilder, fadeRaster, 1700);
        r.Line($"    fade 600 ms: cover 0x{coverPx:X8}, start 0x{start:X8}, 300 ms 0x{mid:X8}, 700 ms 0x{end:X8}");
        r.Check("cover change starts a crossfade from the old cover", runningAtStart && start == coverPx, true);
        r.Check("halfway: a blend of old and new", runningMid && Brightness(mid) < Brightness(coverPx) && Brightness(mid) > 0, true);
        r.Check("after the fade time: the new picture, fade finished", !fadeRaster.IsAnimating && (end & 0xFFFFFFu) == 0, true);

        var instantBuilder = new SwipeModelBuilder();
        instantBuilder.Configure(new AppSettings { BackgroundFadeMs = 0 });
        var instantRaster = new SoftwareRasterizer();
        instantBuilder.SetImageOverride(cover);
        RenderAt(instantBuilder, instantRaster, 0);
        instantBuilder.SetImageOverride(null);
        uint instant = RenderAt(instantBuilder, instantRaster, 1000);
        r.Check("fade 0 ms: switches instantly", !instantRaster.IsAnimating && (instant & 0xFFFFFFu) == 0, true);

        var service = new Spotify.SpotifyService(dir);
        r.Check("service without credentials: not connected", service.IsConnected || service.HasSavedSecret, false);
        string message = await service.ConnectAsync("", null);
        r.Check("connect without client ID is refused before opening a browser", message.Contains("Client ID"), true, message);
        service.Dispose();
    }

    /// <summary>Solid picture with a little grey noise (for cover colour tests).</summary>
    private static string WriteSolidImage(string dir, string name, uint color)
    {
        string path = Path.Combine(dir, name);
        var px = new uint[320 * 180];
        var random = new Random(7);
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = random.Next(10) == 0 ? 0xFF808080u : color;
        }

        using FileStream file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, px, 320 * 4)));
        encoder.Save(file);
        return path;
    }

    private static void TestCoverColors(Report r)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MouseSwipeVisualizer-selftest", "colors");
        Directory.CreateDirectory(dir);
        string blue = WriteSolidImage(dir, "blue.png", 0xFF2050D0u);
        string grey = WriteSolidImage(dir, "grey.png", 0xFF606060u);
        string pale = WriteSolidImage(dir, "pale.png", 0xFFE4E6F2u);
        const int w = 1280, h = 720;
        static bool Bluish(uint p) => (p & 0xFF) > ((p >> 16) & 0xFF) + 40;

        // Palette extraction.
        CoverPalette bp = CoverPalette.Extract(new uint[] { 0xFF2050D0u, 0xFF2050D0u, 0xFF808080u }, 1, true);
        r.Check("blue cover → blue accent", bp.HasAccent && Bluish(bp.Accent), true, $"0x{bp.Accent:X8}");
        CoverPalette gp = CoverPalette.Extract(new uint[] { 0xFF606060u, 0xFF707070u, 0xFF505050u }, 1, true);
        r.Check("grey cover → no accent", gp.HasAccent, false);
        CoverPalette lp = CoverPalette.Extract(new uint[] { 0xFFF0F0F0u, 0xFFE8E8F8u, 0xFF3060E0u }, 1, true);
        r.Check("light cover: light, accent darkened for contrast", lp.Light && lp.HasAccent && CoverPalette.Luminance(lp.Accent) < 0.4, true, $"0x{lp.Accent:X8}");

        // Mouse strokes take the accent.
        var held = new KeyboardState();
        held.OnKey(0x11, true);
        (uint[] still, uint[] drawn) Pair(AppSettings s) =>
            (RenderGesture(s, null, w, h, held).Pixels.ToArray(), RenderGesture(s, Gesture.Curve, w, h, held).Pixels.ToArray());
        int BluishStrokePixels(AppSettings s)
        {
            (uint[] a, uint[] b) = Pair(s);
            int n = 0;
            for (int i = 0; i < b.Length; i++)
            {
                if (a[i] != b[i] && Bluish(b[i]))
                {
                    n++;
                }
            }

            return n;
        }

        var on = new AppSettings { BackgroundImagePath = blue, BackgroundFadeMs = 0 };
        var off = new AppSettings { BackgroundImagePath = blue, BackgroundFadeMs = 0, CoverColorsEnabled = false };
        int strokesOn = BluishStrokePixels(on), strokesOff = BluishStrokePixels(off);
        r.Line($"    blue cover: {strokesOn} blue stroke pixels with cover colours, {strokesOff} without");
        r.Check("mouse strokes use the cover accent", strokesOn > 500, true);
        r.Check("cover colours off: strokes keep their own colour (only edges blend with the blue picture)", strokesOn > 3 * strokesOff, true);

        // Key outlines take the accent; pressed-key fill only when asked.
        OverlayLayout layout = OverlayLayout.Compute(w, h, OverlayStyle.From(on));
        var keys = new RectD[KeyboardLayout.Keys.Count];
        OverlayLayout.ComputeKeys(layout.Keyboard, keys, out _, layout.KeyUnit);
        RectD q = keys[KeyboardLayout.KeyIndexForScanCode(0x10)], wKey = keys[KeyboardLayout.KeyIndexForScanCode(0x11)];
        int bx = (int)q.X + 1, by = (int)q.CenterY;
        uint BorderOf(AppSettings s) => RenderGesture(s, null, w, h, held).Pixels[by * w + bx];
        uint PressedFillOf(AppSettings s) => RenderGesture(s, null, w, h, held).Pixels[(int)(wKey.Y + wKey.Height * 0.2) * w + (int)(wKey.X + wKey.Width * 0.2)];
        uint borderOn = BorderOf(on), borderOff = BorderOf(off), borderGrey = BorderOf(new AppSettings { BackgroundImagePath = grey, BackgroundFadeMs = 0 });
        r.Line($"    Q outline: 0x{borderOn:X8} with cover colours, 0x{borderOff:X8} without, 0x{borderGrey:X8} on a grey cover");
        r.Check("key outlines use the cover accent", Bluish(borderOn), true);
        r.Check("cover colours off / grey cover: normal outline colour", !Bluish(borderOff) && !Bluish(borderGrey), true);
        uint pressedDefault = PressedFillOf(on);
        uint pressedAccent = PressedFillOf(new AppSettings { BackgroundImagePath = blue, BackgroundFadeMs = 0, CoverAccentPressedKeys = true });
        r.Check("pressed keys: white by default, accent when enabled", (pressedDefault & 0xFFFFFFu) == 0xFFFFFFu && Bluish(pressedAccent), true,
            $"0x{pressedDefault:X8} 0x{pressedAccent:X8}");

        // Auto contrast: dark labels on glass keys over a light cover.
        int DarkestInKey(AppSettings s)
        {
            ReadOnlySpan<uint> px = RenderGesture(s, null, w, h).Pixels;
            int min = int.MaxValue;
            for (int y = (int)(q.Y + q.Height * 0.3); y < (int)(q.Y + q.Height * 0.7); y++)
            {
                for (int x = (int)(q.X + q.Width * 0.3); x < (int)(q.X + q.Width * 0.7); x++)
                {
                    min = Math.Min(min, Brightness(px[y * w + x]));
                }
            }

            return min;
        }

        int autoOn = DarkestInKey(new AppSettings { BackgroundImagePath = pale, GlassEnabled = true, BackgroundFadeMs = 0, BackgroundImageDim = 0 });
        int autoOff = DarkestInKey(new AppSettings { BackgroundImagePath = pale, GlassEnabled = true, BackgroundFadeMs = 0, BackgroundImageDim = 0, CoverAutoContrast = false });
        r.Line($"    light glass Q key, darkest pixel: {autoOn} with auto contrast, {autoOff} without");
        r.Check("auto contrast: dark label on light glass", autoOn < 200 && autoOff > 400, true);
        SavePng(RenderGesture(on, Gesture.Curve, w, h, held).Pixels, w, h, "selftest-cover-colors.png");

        // Auto contrast for the mouse strokes, measured against what is under the mouse area.
        string dark = WriteSolidImage(dir, "dark.png", 0xFF1A1A1Eu);
        (double Ratio, uint Trail, uint Outline) StrokeContrast(AppSettings s)
        {
            SoftwareRasterizer raster = RenderGesture(s, Gesture.Curve, w, h);
            double ratio = CoverPalette.ContrastRatio(CoverPalette.RelativeLuminance(raster.EffectiveTrailColor), raster.SwipeBackgroundLuminance);
            return (ratio, raster.EffectiveTrailColor, raster.EffectiveOutlineColor);
        }

        var paleOn = StrokeContrast(new AppSettings { BackgroundImagePath = pale, BackgroundFadeMs = 0, BackgroundImageDim = 0 });
        var paleOff = StrokeContrast(new AppSettings { BackgroundImagePath = pale, BackgroundFadeMs = 0, BackgroundImageDim = 0, CoverAutoContrast = false });
        var paleGlass = StrokeContrast(new AppSettings { BackgroundImagePath = pale, BackgroundFadeMs = 0, BackgroundImageDim = 0, GlassEnabled = true });
        var darkOn = StrokeContrast(new AppSettings { BackgroundImagePath = dark, BackgroundFadeMs = 0, TrailColor = "#2A2A30" });
        var darkOff = StrokeContrast(new AppSettings { BackgroundImagePath = dark, BackgroundFadeMs = 0, TrailColor = "#2A2A30", CoverAutoContrast = false });
        r.Line(string.Create(CultureInfo.InvariantCulture,
            $"    stroke contrast: pale cover {paleOn.Ratio:0.0}:1 (off {paleOff.Ratio:0.0}:1, glass {paleGlass.Ratio:0.0}:1), dark cover + dark stroke {darkOn.Ratio:0.0}:1 (off {darkOff.Ratio:0.0}:1)"));
        r.Line($"    pale cover: stroke 0x{paleOn.Trail:X8}, outline 0x{paleOn.Outline:X8}");
        r.Check("pale cover: white stroke turned dark enough (≥ 4.5:1)", paleOn.Ratio >= 4.5 && paleOff.Ratio < 3, true);
        r.Check("stroke and outline stay distinguishable (≥ 2:1)", CoverPalette.ContrastRatio(CoverPalette.RelativeLuminance(paleOn.Outline), CoverPalette.RelativeLuminance(paleOn.Trail)) >= 2, true);
        r.Check("pale glass cover: stroke readable (≥ 4.5:1)", paleGlass.Ratio >= 4.5, true);
        r.Check("dark cover: dark stroke turned light enough (≥ 4.5:1)", darkOn.Ratio >= 4.5 && darkOff.Ratio < 3, true);
        uint paleBorder = RenderGesture(new AppSettings { BackgroundImagePath = pale, BackgroundFadeMs = 0, BackgroundImageDim = 0 }, null, w, h).Pixels[by * w + bx];
        uint paleBorderOff = RenderGesture(new AppSettings { BackgroundImagePath = pale, BackgroundFadeMs = 0, BackgroundImageDim = 0, CoverAutoContrast = false }, null, w, h).Pixels[by * w + bx];
        double paleLum = CoverPalette.RelativeLuminance(0xFFE4E6F2u);
        r.Line($"    pale cover key outline: 0x{paleBorder:X8} with auto contrast, 0x{paleBorderOff:X8} without");
        r.Check("pale cover: key outlines stand out (≥ 3:1)", CoverPalette.ContrastRatio(CoverPalette.RelativeLuminance(paleBorder), paleLum) >= 3
            && CoverPalette.ContrastRatio(CoverPalette.RelativeLuminance(paleBorderOff), paleLum) < 3, true);
        SavePng(RenderGesture(new AppSettings { BackgroundImagePath = pale, BackgroundFadeMs = 0, BackgroundImageDim = 0 }, Gesture.Curve, w, h).Pixels,
            w, h, "selftest-stroke-contrast-pale.png");

        const string wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
        if (File.Exists(wallpaper))
        {
            SavePng(RenderGesture(new AppSettings { BackgroundImagePath = wallpaper, GlassEnabled = true, BackgroundFadeMs = 0 }, Gesture.Curve, w, h, held).Pixels,
                w, h, "selftest-cover-colors-wallpaper.png");
        }
    }

    private static void TestAppIcon(Report r)
    {
        // The exe's own Win32 icon is what the Start menu, search and Explorer show.
        string exe = Environment.ProcessPath ?? string.Empty;
        using System.Drawing.Icon? shellIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
        int opaque = 0, white = 0;
        if (shellIcon != null)
        {
            using System.Drawing.Bitmap bitmap = shellIcon.ToBitmap();
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    System.Drawing.Color c = bitmap.GetPixel(x, y);
                    if (c.A > 200)
                    {
                        opaque++;
                        if (c.R > 220 && c.G > 220 && c.B > 220)
                        {
                            white++;
                        }
                    }
                }
            }
        }

        r.Line($"    exe icon: {opaque} opaque pixels, {white} white (arrow)");
        r.Check("exe has its own icon (dark circle with a white arrow), not the blank default", opaque > 300 && white > 20, true);
        using Stream? resource = typeof(App).Assembly.GetManifestResourceStream("MouseSwipeVisualizer.AppIcon.ico");
        r.Check("icon embedded for tray and windows", resource != null && resource.Length > 1000, true);
    }

    private static void TestGestureDirections(Report r)
    {
        var settings = SwipeOnly();
        const int w = 800, h = 800;
        (double rx, _, int rc) = HeadCentroid(RenderGesture(settings, Gesture.RightFlick, w, h).Pixels, w);
        (double lx, _, int lc) = HeadCentroid(RenderGesture(settings, Gesture.LeftFlick, w, h).Pixels, w);
        (double cx, double cy, int cc) = HeadCentroid(RenderGesture(settings, Gesture.Curve, w, h).Pixels, w);
        r.Line(string.Create(CultureInfo.InvariantCulture, $"    head centroid: right flick x={rx:0}, left flick x={lx:0}, curve ({cx:0},{cy:0})"));
        r.Check("right flick: head right of centre", rc > 0 && rx > w / 2 + 50, true);
        r.Check("left flick: head left of centre", lc > 0 && lx < w / 2 - 50, true);
        r.Check("curve (right, bending up): head above centre", cc > 0 && cy < h / 2 - 50, true);

        SoftwareRasterizer lift = RenderGesture(settings, Gesture.Lift, w, h);
        (double _, double ly, int lcount) = HeadCentroid(lift.Pixels, w);
        r.Check("lift: second swipe re-centred and heading down", lcount > 0 && ly > h / 2 + 10, true);
        SavePng(lift.Pixels, w, h, "selftest-lift.png");
    }

    private static async Task TestHeadlessEngineAsync(Report r)
    {
        var settings = SwipeOnly();
        using var pipeline = new TestPipeline(settings, 800, 800);
        CaptureWindow window = await OpenCaptureWindowAsync(settings, pipeline.Preview);
        var occluder = new Window
        {
            Title = "Self-test occluder",
            Left = window.Left - 20,
            Top = window.Top - 20,
            Width = 900,
            Height = 900,
            Background = Brushes.DarkSlateGray,
            ShowInTaskbar = false,
        };

        async Task Phase(string name)
        {
            long frames = pipeline.Output.Frames;
            ulong hash = pipeline.Output.LastHash;
            PushGesture(pipeline.Buffer, Gesture.Curve);
            await Task.Delay(200);
            long delivered = pipeline.Output.Frames - frames;
            uint[] frame = pipeline.Output.Snapshot(out int fw, out int _);
            PixelCensus census = CountPixels(frame);
            r.Line($"    {name}: +{delivered} frames to the camera-side output, trail px {census.Trail}, preview active {pipeline.Preview.IsActive}");
            r.Check($"{name}: frames keep coming", delivered >= 5, true);
            r.Check($"{name}: content changed", pipeline.Output.LastHash != hash && census.Trail > 200, true);
            await Task.Delay(600); // let the trail fade so the next phase starts from an empty frame
        }

        try
        {
            await Phase("preview visible");

            occluder.Show();
            occluder.Activate();
            await Task.Delay(100);
            await Phase("preview covered by another window");
            occluder.Close();

            window.WindowState = WindowState.Minimized;
            await Task.Delay(100);
            r.Check("minimized preview stops pulling frames", pipeline.Preview.IsActive, false);
            await Phase("preview minimized");

            window.WindowState = WindowState.Normal;
            window.Hide();
            await Task.Delay(100);
            await Phase("preview hidden");

            window.Close();
            await Task.Delay(100);
            await Phase("preview closed (no windows at all)");

            long before = pipeline.Engine.Stats.FramesRasterized;
            await Task.Delay(1500);
            long idleRasters = pipeline.Engine.Stats.FramesRasterized - before;
            r.Line($"    idle 1.5 s after the trail faded: {idleRasters} rasterizations, {pipeline.Engine.Stats.FramesSkippedUnchanged} skipped as unchanged");
            r.Check("unchanged scene is not re-rasterized", idleRasters <= 1, true);
        }
        finally
        {
            occluder.Close();
            window.Close();
        }
    }

    private static async Task TestOccludedCaptureAsync(Report r)
    {
        using var pipeline = new TestPipeline(SwipeOnly());
        pipeline.Output.Active = false; // only the preview drives the engine here
        CaptureWindow window = await OpenCaptureWindowAsync(SwipeOnly(), pipeline.Preview);
        var occluder = new Window
        {
            Title = "Self-test occluder (stands in for the game)",
            Left = window.Left - 20,
            Top = window.Top - 20,
            Width = 900,
            Height = 900,
            Background = Brushes.DarkSlateGray,
            ShowInTaskbar = false,
        };

        try
        {
            occluder.Show();
            occluder.Activate();
            await Task.Delay(200);
            IntPtr hwnd = window.Handle;
            r.Check("another window has focus", NativeMethods.GetForegroundWindow() != hwnd, true);

            // Feed a gesture; the engine renders it and the preview window displays the frame.
            PushGesture(pipeline.Buffer, Gesture.Curve);
            await Task.Delay(150);
            r.Check("engine rendered while the preview is occluded/unfocused", pipeline.Engine.Stats.FramesRasterized > 0 && pipeline.Engine.Tracker.PointCount > 0, true);
            r.Check("preview displayed the engine frame", window.LastShownFrameSequence >= 0, true);

            // PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT) reads the window's own DWM content,
            // like OBS does, regardless of what is on top of it.
            using var bitmap = new System.Drawing.Bitmap(window.CaptureWidth, window.CaptureHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            bool printed;
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                IntPtr hdc = g.GetHdc();
                printed = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_CLIENTONLY | NativeMethods.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }

            r.Check("PrintWindow succeeded", printed, true);
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bitmap.UnlockBits(data);
            PixelCensus census = CountPixels(bytes);
            r.Line($"    captured client area while occluded: key {census.Key}, trail {census.Trail}, halo {census.Halo}");
            r.Check("captured chroma background (not the occluder)", census.Key > bitmap.Width * bitmap.Height * 0.8, true);
            r.Check("captured the swipe", census.Trail > 100, true);
        }
        finally
        {
            occluder.Close();
            window.Close();
        }
    }

    private static async Task TestRenderingCostAsync(Report r)
    {
        foreach ((int w, int h) in new[] { (800, 800), (1280, 720) })
        {
            using var pipeline = new TestPipeline(new AppSettings(), w, h);
            await Task.Delay(100);
            int gen0Before = GC.CollectionCount(0);
            double angle = 0;
            long start = MonotonicClock.Now;
            long end = start + MonotonicClock.MsToTicks(3000);
            long warmedUp = start + MonotonicClock.MsToTicks(500);
            long allocAtWarm = -1, framesAtWarm = 0;
            while (MonotonicClock.Now < end)
            {
                if (allocAtWarm < 0 && MonotonicClock.Now >= warmedUp)
                {
                    // Buffers have reached their working size; measure the steady state from here.
                    allocAtWarm = pipeline.Engine.Stats.TotalAllocatedBytes;
                    framesAtWarm = pipeline.Engine.Stats.Loops;
                }

                // ~1 kHz mouse: 16 deltas per 16 ms, continuous circular motion (trail always visible).
                long now = MonotonicClock.Now;
                for (int i = 0; i < 16; i++)
                {
                    angle += 0.01;
                    pipeline.Buffer.Push(new MouseDelta((int)(Math.Cos(angle) * 6), (int)(Math.Sin(angle * 1.3) * 6), now - Ms(16 - i)));
                }

                await Task.Delay(16);
            }

            EngineStats s = pipeline.Engine.Stats;
            double steadyAlloc = (double)(s.TotalAllocatedBytes - allocAtWarm) / Math.Max(1, s.Loops - framesAtWarm);
            r.Line(string.Create(CultureInfo.InvariantCulture,
                $"    {w}x{h} @60: input+model {s.InputAndModelMs:0.000} ms, raster {s.RasterMs:0.000} ms, publish {s.PublishMs:0.000} ms, " +
                $"total {s.TotalMs:0.000} ms (max {s.MaxTotalMs:0.00}), steady-state alloc {steadyAlloc:0.0} B/frame, " +
                $"{s.FramesRasterized} frames in 3 s, shaded px {s.LastShadedPixels}, gen0 GCs {GC.CollectionCount(0) - gen0Before} (whole process)"));
            r.Check($"{w}x{h}: ~60 frames/s produced", s.FramesRasterized >= 150, true);
            r.Check($"{w}x{h}: average frame time < 4 ms", s.TotalMs < 4, true);
            r.Check($"{w}x{h}: steady-state allocation < 64 B/frame", steadyAlloc < 64, true);
        }
    }
    // ------------------------------------------------------------------ virtual camera integration

    private readonly record struct RecordedFrame(long ArrivalTicks, VirtualCameraNative.FrameInfo Info, int Key, int Trail, int Halo,
        double HeadX, double HeadY, int HeadCount, ulong Hash);

    /// <summary>Reads camera frames on a background thread and analyses each one.</summary>
    private sealed class CameraRecorder : IDisposable
    {
        private readonly CameraConsumer _consumer;
        private readonly Thread _thread;
        private readonly List<RecordedFrame> _frames = new();
        private volatile bool _stop;

        public CameraRecorder(CameraConsumer consumer)
        {
            _consumer = consumer;
            _thread = new Thread(Run) { IsBackground = true, Name = "CameraRecorder" };
            _thread.Start();
        }

        public Exception? Error { get; private set; }

        public int Count
        {
            get
            {
                lock (_frames)
                {
                    return _frames.Count;
                }
            }
        }

        private void Run()
        {
            try
            {
                while (!_stop)
                {
                    VirtualCameraNative.FrameInfo info = _consumer.Read();
                    uint[] pixels = _consumer.Pixels;
                    PixelCensus census = CountPixelsDecoded(pixels);
                    (double hx, double hy, int hc) = HeadCentroid(pixels, _consumer.Width);
                    ulong hash = 1469598103934665603UL;
                    for (int i = 0; i < pixels.Length; i += 13)
                    {
                        hash = (hash ^ pixels[i]) * 1099511628211UL;
                    }

                    lock (_frames)
                    {
                        _frames.Add(new RecordedFrame(MonotonicClock.Now, info, census.Key, census.Trail, census.Halo, hx, hy, hc, hash));
                    }
                }
            }
            catch (Exception ex)
            {
                Error = ex;
            }
        }

        public List<RecordedFrame> Since(long ticks)
        {
            lock (_frames)
            {
                return _frames.Where(f => f.ArrivalTicks >= ticks).ToList();
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(3000);
            _consumer.Dispose();
        }
    }

    private delegate CameraConsumer CameraOpener(int width, int height, int fps, int subtype);

    private static async Task<List<RecordedFrame>> WaitFrames(CameraRecorder recorder, long since, int count, int timeoutMs = 8000)
    {
        long deadline = MonotonicClock.Now + MonotonicClock.MsToTicks(timeoutMs);
        while (MonotonicClock.Now < deadline && recorder.Since(since).Count < count && recorder.Error == null)
        {
            await Task.Delay(20);
        }

        return recorder.Since(since);
    }

    private static bool MostlyKey(RecordedFrame f) => f.Key > (f.Info.Width * f.Info.Height) * 0.98;

    /// <summary>
    /// Full pipeline through a camera consumer: synthetic input → engine → rasterizer → CameraFrameLink
    /// (shared memory) → media source → NV12/RGB32 → IMFSourceReader → pixel checks.
    /// </summary>
    private static async Task RunCameraPipelineTests(Report r, string label, CameraOpener open, bool viaFrameServer)
    {
        // 1) Nobody produces: the camera must still deliver fallback frames at the right cadence.
        long t0 = MonotonicClock.Now;
        using (var fallback = new CameraRecorder(await Task.Run(() => open(1280, 720, 30, VirtualCameraNative.SubtypeNv12))))
        {
            List<RecordedFrame> frames = await WaitFrames(fallback, t0, 45);
            r.Check($"{label}: consumer opened and read frames without the app producing", frames.Count >= 45, true, fallback.Error?.Message);
            FrameCadence cadence = FrameCadence.Analyze(frames.Select(f => f.Info).ToList(), 30, 1280, 720);
            r.Line($"    {label} fallback 1280x720 NV12@30: {cadence.Describe()}");
            r.Check($"{label}: fallback cadence/timestamps/size OK", cadence.Ok, true);
            r.Check($"{label}: fallback frames are the chroma background", frames.Count > 0 && frames.All(MostlyKey), true);
            r.Check($"{label}: first sample flagged as discontinuity", frames.Count > 0 && (frames[0].Info.Flags & 1) != 0, true);
        }

        // 2) The app pipeline produces, the camera consumes. Preview window shown and later minimized/hidden/closed.
        var settings = SwipeOnly();
        var link = new CameraFrameLink();
        var buffer = new MouseDeltaBuffer();
        var preview = new PreviewFrameStore();
        var engine = new SwipeEngine(buffer, settings, link, preview);
        link.ConsumerActiveChanged += _ => engine.Wake();
        engine.Start();
        CaptureWindow window = await OpenCaptureWindowAsync(settings, preview);
        try
        {
            using var recorder = new CameraRecorder(await Task.Run(() => open(1280, 720, 30, VirtualCameraNative.SubtypeNv12)));
            long waitUntil = MonotonicClock.Now + MonotonicClock.MsToTicks(3000);
            while (!link.IsActive && MonotonicClock.Now < waitUntil)
            {
                await Task.Delay(20);
            }

            r.Check($"{label}: app detected the camera consumer", link.IsActive, true);
            r.Check($"{label}: requested format reached the app", $"{link.RequestedWidth}x{link.RequestedHeight}@{link.RequestedFps}", "1280x720@30");
            CameraLinkState state = link.GetState();
            r.Line($"    {label}: shared memory connected={state.Connected}, source pid {state.SourceProcessId}, starts {state.StreamStarts}");
            await Task.Delay(300);

            async Task<List<RecordedFrame>> RunGesture(Gesture gesture, string name, Action? before = null)
            {
                before?.Invoke();
                await Task.Delay(150);
                long start = MonotonicClock.Now;
                PushGesture(buffer, gesture);
                List<RecordedFrame> got = await WaitFrames(recorder, start, 12);
                await Task.Delay(700); // fade out before the next gesture
                // Judge the frame with the most visible head pixels (the first one may already be ~100 ms old).
                List<RecordedFrame> withSwipe = got.Where(f => f.Trail > 200).OrderByDescending(f => f.HeadCount).ToList();
                r.Line(string.Create(CultureInfo.InvariantCulture,
                    $"    {label} {name}: {got.Count} frames, {withSwipe.Count} with swipe, head " +
                    $"{(withSwipe.Count > 0 ? $"({withSwipe[0].HeadX:0},{withSwipe[0].HeadY:0})" : "-")}, max halo {(got.Count > 0 ? got.Max(f => f.Halo) : 0)}"));
                r.Check($"{label} {name}: swipe visible through the camera", withSwipe.Count > 0, true);
                r.Check($"{label} {name}: no green-halo pixels after NV12", got.Count > 0 && got.All(f => f.Halo == 0), true);
                return withSwipe;
            }

            List<RecordedFrame> right = await RunGesture(Gesture.RightFlick, "right flick");
            r.Check($"{label} right flick: head right of centre", right.Count > 0 && right[0].HeadCount > 0 && right[0].HeadX > 640 + 50, true);
            List<RecordedFrame> curve = await RunGesture(Gesture.Curve, "curve");
            r.Check($"{label} curve: head above centre", curve.Count > 0 && curve[0].HeadCount > 0 && curve[0].HeadY < 360 - 40, true);
            List<RecordedFrame> left = await RunGesture(Gesture.LeftFlick, "left flick");
            r.Check($"{label} left flick: head left of centre", left.Count > 0 && left[0].HeadCount > 0 && left[0].HeadX < 640 - 50, true);
            List<RecordedFrame> lift = await RunGesture(Gesture.Lift, "lift + re-center");
            r.Check($"{label} lift: new swipe re-centred, heading down", lift.Count > 0 && lift[0].HeadCount > 0 && lift[0].HeadY > 360 + 5, true);

            await RunGesture(Gesture.Curve, "preview minimized", () => window.WindowState = WindowState.Minimized);
            await RunGesture(Gesture.RightFlick, "preview hidden", () => { window.WindowState = WindowState.Normal; window.Hide(); });
            await RunGesture(Gesture.LeftFlick, "preview closed", () => window.Close());

            List<RecordedFrame> all = recorder.Since(0);
            FrameCadence cadence = FrameCadence.Analyze(all.Select(f => f.Info).ToList(), 30, 1280, 720);
            r.Line($"    {label} 1280x720 NV12@30 with swipes: {cadence.Describe()}, distinct frames {all.Select(f => f.Hash).Distinct().Count()}");
            r.Check($"{label}: cadence, timestamps and size OK over the whole run", cadence.Ok, true);
            r.Check($"{label}: consumer read without errors", recorder.Error == null, true, recorder.Error?.Message);
        }
        finally
        {
            window.Close();
        }

        // 3) Consumer stopped: the app must stop producing.
        await Task.Delay(1600);
        long published = engine.Stats.FramesPublished;
        await Task.Delay(1000);
        r.Check($"{label}: consumer closed → app detected it", link.IsActive, false);
        r.Line($"    {label}: frames published during 1 s without consumer: {engine.Stats.FramesPublished - published}");

        // 4) 60 fps formats, NV12 and RGB32, and a consumer restart.
        foreach ((int w, int h, int subtype) in new[] { (800, 800, VirtualCameraNative.SubtypeNv12), (800, 800, VirtualCameraNative.SubtypeRgb32), (1280, 720, VirtualCameraNative.SubtypeNv12) })
        {
            string format = $"{w}x{h} {(subtype == 1 ? "NV12" : "RGB32")}@60";
            long start = MonotonicClock.Now;
            using var recorder = new CameraRecorder(await Task.Run(() => open(w, h, 60, subtype)));
            long activeDeadline = MonotonicClock.Now + MonotonicClock.MsToTicks(3000);
            while (!(link.IsActive && link.RequestedWidth == w && link.RequestedFps == 60) && MonotonicClock.Now < activeDeadline)
            {
                await Task.Delay(20);
            }

            r.Line(string.Create(CultureInfo.InvariantCulture,
                $"    {label} {format}: link active {link.IsActive} ({link.RequestedWidth}x{link.RequestedHeight}@{link.RequestedFps}) after {MonotonicClock.TicksToMs(MonotonicClock.Now - start):0} ms, engine {engine.Stats.RenderWidth}x{engine.Stats.RenderHeight}; header active={link.GetState().HeaderConsumerActive} consumerHB={link.GetState().ConsumerHeartbeat} producerHB={link.GetState().ProducerHeartbeat} starts={link.GetState().StreamStarts} delivered={link.GetState().FramesDelivered}"));
            await Task.Delay(150);
            PushGesture(buffer, Gesture.Curve);
            List<RecordedFrame> frames = await WaitFrames(recorder, start, 150);
            FrameCadence cadence = FrameCadence.Analyze(frames.Select(f => f.Info).ToList(), 60, w, h);
            r.Line($"    {label} {format}: {cadence.Describe()}, frames with swipe {frames.Count(f => f.Trail > 200)}, max halo {(frames.Count > 0 ? frames.Max(f => f.Halo) : 0)}");
            r.Check($"{label} {format}: cadence/timestamps/size OK", cadence.Ok, true);
            r.Check($"{label} {format}: swipe arrived through the camera", frames.Any(f => f.Trail > 200), true);
            r.Check($"{label} {format}: no halo", frames.Count > 0 && frames.All(f => f.Halo == 0), true);
        }

        // 5) App crash while streaming → fallback frames; app restart → real frames again.
        {
            using var recorder = new CameraRecorder(await Task.Run(() => open(1280, 720, 30, VirtualCameraNative.SubtypeNv12)));
            await Task.Delay(600);
            engine.Dispose(); // "crash": producer stops beating
            link.Dispose();
            long crashed = MonotonicClock.Now;
            await Task.Delay(2000);
            List<RecordedFrame> afterCrash = recorder.Since(crashed + MonotonicClock.MsToTicks(1300));
            r.Check($"{label}: app gone → camera keeps delivering fallback frames", afterCrash.Count >= 10 && afterCrash.All(MostlyKey), true);

            var link2 = new CameraFrameLink();
            var buffer2 = new MouseDeltaBuffer();
            using var engine2 = new SwipeEngine(buffer2, settings, link2);
            link2.ConsumerActiveChanged += _ => engine2.Wake();
            engine2.Start();
            long restarted = MonotonicClock.Now;
            long until = restarted + MonotonicClock.MsToTicks(3000);
            while (!link2.IsActive && MonotonicClock.Now < until)
            {
                await Task.Delay(20);
            }

            PushGesture(buffer2, Gesture.RightFlick);
            List<RecordedFrame> afterRestart = await WaitFrames(recorder, restarted, 30);
            r.Check($"{label}: app restart → camera reconnects and shows the swipe again", link2.IsActive && afterRestart.Any(f => f.Trail > 200), true);
            link2.Dispose();
        }

        // 6) Two consumers at the same time.
        if (viaFrameServer)
        {
            // Realistic case: a second *application* (separate process) opens the camera while the first streams.
            long start = MonotonicClock.Now;
            using var first = new CameraRecorder(await Task.Run(() => open(1280, 720, 30, VirtualCameraNative.SubtypeNv12)));
            await Task.Delay(500);
            var info = new ProcessStartInfo(Environment.ProcessPath!, "--camera-test --frames 30")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using Process second = Process.Start(info)!;
            string secondOutput = await second.StandardOutput.ReadToEndAsync();
            bool exited = second.WaitForExit(30_000);
            long secondDone = MonotonicClock.Now;
            await Task.Delay(1000);
            int firstFramesDuring = first.Since(start).Count(f => f.ArrivalTicks <= secondDone + MonotonicClock.MsToTicks(1000));
            double seconds = MonotonicClock.TicksToMs(secondDone + MonotonicClock.MsToTicks(1000) - start) / 1000.0;
            string outcome = secondOutput.Contains("=> OK", StringComparison.Ordinal) ? "second process streamed too (shared)"
                : secondOutput.Contains("ERROR", StringComparison.Ordinal) ? "second process got a clean error: " + secondOutput.Split('\n').FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal))?.Trim()
                : "second process: " + secondOutput.Trim();
            r.Line(string.Create(CultureInfo.InvariantCulture, $"    {label}: two applications: first consumer {firstFramesDuring} frames in {seconds:0.0} s; {outcome}"));
            r.Check($"{label}: second application exits cleanly (no hang/crash)", exited && (second.ExitCode == 0 || second.ExitCode == 1), true);
            r.Check($"{label}: first application keeps streaming while a second one tries", first.Error == null && firstFramesDuring >= seconds * 30 * 0.85, true);
        }
        else
        {
            long start = MonotonicClock.Now;
            using var first = new CameraRecorder(await Task.Run(() => open(1280, 720, 30, VirtualCameraNative.SubtypeNv12)));
            using var second = new CameraRecorder(await Task.Run(() => open(1280, 720, 30, VirtualCameraNative.SubtypeNv12)));
            await Task.Delay(1500);
            r.Line($"    {label}: two consumers: #1 {first.Since(start).Count} frames, #2 {second.Since(start).Count} frames");
            r.Check($"{label}: two in-process sources stream concurrently", first.Since(start).Count >= 20 && second.Since(start).Count >= 20, true);
        }
    }
    private static async Task TestCameraInProcessAsync(Report r)
    {
        await RunCameraPipelineTests(r, "in-process source",
            (w, h, fps, subtype) => CameraConsumer.OpenInProcess(w, h, fps, subtype), viaFrameServer: false);
    }

    private static async Task TestCameraFrameServerAsync(Report r)
    {
        VirtualCameraStatus status = await Task.Run(() => VirtualCameraService.GetStatus());
        foreach (EnumeratedCamera camera in status.Cameras)
        {
            r.Line($"    enumerated: {(camera.IsOurs ? "*" : " ")} {camera.FriendlyName}");
        }

        r.Check("Virtual camera API supported (build >= 22000, MFCreateVirtualCamera, type supported)", status.ApiSupported, true);
        if (!status.SourceRegistered || status.OurCamera == null)
        {
            r.Line("    SKIPPED: the virtual camera is not installed (MouseSwipeVisualizer.exe --camera-install).");
            return;
        }

        EnumeratedCamera ours = status.OurCamera.Value;
        r.Check("camera enumerated via MFEnumDeviceSources (KSCATEGORY_VIDEO_CAMERA)", ours.IsOurs, true);
        r.Check("friendly name starts with 'Mouse Swipe Visualizer Camera'", ours.FriendlyName.StartsWith(VirtualCameraService.FriendlyName, StringComparison.Ordinal), true, ours.FriendlyName);
        await RunCameraPipelineTests(r, "Frame Server camera",
            (w, h, fps, subtype) => CameraConsumer.Open(VirtualCameraService.FriendlyName, w, h, fps, subtype), viaFrameServer: true);
    }

    private sealed class TestSettingsHost : ISettingsHost
    {
        public AppSettings Settings { get; private set; } = new();

        public int Edits { get; private set; }

        public void OnSettingsEdited()
        {
            Settings.Sanitize();
            Edits++;
        }

        public void ResetSettingsToDefaults() => Settings = new AppSettings();

        public void ClearTrail()
        {
        }

        public void ShowPreview()
        {
        }

        public string CameraSummary() => "test";

        public Task<string> RunCameraActionAsync(CameraAction action) => Task.FromResult("test");
    }

    private static async Task TestSettingsWindowAsync(Report r)
    {
        var host = new TestSettingsHost();
        var window = new SettingsWindow(host) { Left = 40, Top = 40 };
        try
        {
            window.Show();
            await Task.Delay(150);
            window.SetDiagnostics("diagnostics text");
            r.Check("settings window shown", window.IsVisible, true);
            r.Check("separate window (not part of the capture window)", new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero, true);

            var combo = (System.Windows.Controls.ComboBox)window.FindName("CaptureSizeCombo");
            r.Check("capture size preset shows 800 × 800", combo.SelectedItem as string, "800 × 800");
            combo.SelectedIndex = Array.IndexOf(AppSettings.CaptureSizePresets, 1080);
            r.Check("preset 1080 applied", $"{host.Settings.CaptureWidth}x{host.Settings.CaptureHeight}", "1080x1080");
            combo.SelectedIndex = Array.IndexOf(AppSettings.CaptureSizePresets, 400);
            r.Check("preset 400 applied", $"{host.Settings.CaptureWidth}x{host.Settings.CaptureHeight}", "400x400");
            r.Check("edits reported to host", host.Edits >= 2, true);
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ report helper

    private sealed class Report
    {
        private readonly StringBuilder _text = new();

        public int Failures { get; private set; }

        public int Passed { get; private set; }

        public void Line(string text) => _text.AppendLine(text);

        public string? Filter { get; init; }

        private bool Skip(string name) => Filter != null && name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0;

        public void Run(string name, Action<Report> test)
        {
            if (Skip(name))
            {
                return;
            }

            _text.AppendLine($"[{name}]");
            try
            {
                test(this);
            }
            catch (Exception ex)
            {
                Failures++;
                _text.AppendLine($"  FAIL exception: {ex}");
            }
        }

        public async Task RunAsync(string name, Func<Report, Task> test)
        {
            if (Skip(name))
            {
                return;
            }

            _text.AppendLine($"[{name}]");
            try
            {
                await test(this);
            }
            catch (Exception ex)
            {
                Failures++;
                _text.AppendLine($"  FAIL exception: {ex}");
            }
        }


        public void Check<T>(string what, T actual, T expected, string? detail = null)
        {
            bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
            Record(ok, what, $"{actual}", $"{expected}", detail);
        }

        public void CheckClose(string what, double actual, double expected, double tolerance = 1e-9)
        {
            bool ok = Math.Abs(actual - expected) <= tolerance;
            Record(ok, what, actual.ToString("0.#########", CultureInfo.InvariantCulture),
                expected.ToString("0.#########", CultureInfo.InvariantCulture), null);
        }

        private void Record(bool ok, string what, string actual, string expected, string? detail)
        {
            if (ok)
            {
                Passed++;
                _text.AppendLine($"  ok   {what} = {actual}");
            }
            else
            {
                Failures++;
                _text.AppendLine($"  FAIL {what}: got {actual}, expected {expected}{(string.IsNullOrEmpty(detail) ? string.Empty : " – " + detail)}");
            }
        }

        public override string ToString() => _text.ToString();
    }
}
