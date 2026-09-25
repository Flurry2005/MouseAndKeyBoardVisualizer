using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Camera;

/// <summary>
/// Command-line camera tooling (runs without UI and exits):
/// <code>
/// --camera-status      API support, registration, enumeration, link counters
/// --camera-install     elevated: copy + COM-register the media source; then create the camera
/// --camera-register    create/re-open the camera for this user (no admin)
/// --camera-unregister  remove the camera for this user (IMFVirtualCamera::Remove, no admin)
/// --camera-remove      full uninstall: remove camera(s) + devnodes, COM registration, binaries (elevated)
/// --camera-diagnose    status + every enumerated camera + a short open/read test
/// --camera-test        open the camera as a consumer and read frames [--frames N] [--format 1280x720@30] [--rgb32]
/// </code>
/// All commands are idempotent. Output goes to stdout (use `| Out-Host` in PowerShell, since this is
/// a GUI-subsystem executable) and to --result-file when given.
/// </summary>
public static class CameraCommands
{
    private static readonly string[] Commands =
    {
        "--camera-status", "--camera-install", "--camera-register", "--camera-unregister", "--camera-remove",
        "--camera-uninstall", "--camera-diagnose", "--camera-test",
        "--camera-install-elevated", "--camera-uninstall-elevated",
    };

    public static bool IsCameraCommand(string[] args) => args.Any(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        ConsoleOutput.Attach();
        var output = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode;
        try
        {
            // MTA background thread: Media Foundation / Frame Server proxies must not run on the WPF STA.
            exitCode = Task.Run(() => Execute(args, output)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            output.WriteLine($"ERROR: {ex.Message}");
            Logger.Error("Camera command failed.", ex);
            exitCode = ex is UnauthorizedAccessException ? 5 : 1;
        }

        string text = output.ToString();
        Console.Out.Write(text);
        Console.Out.Flush();
        string? resultFile = ArgumentValue(args, "--result-file");
        if (resultFile != null)
        {
            File.WriteAllText(resultFile, text);
        }

        return exitCode;
    }

    private static int Execute(string[] args, TextWriter output)
    {
        string command = args.First(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();
        Logger.Info($"Camera command: {string.Join(' ', args)}");
        switch (command)
        {
            case "--camera-status":
                output.WriteLine(VirtualCameraService.Describe(VirtualCameraService.GetStatus(), new CameraFrameLink().GetState()));
                return 0;

            case "--camera-register":
                return Report(output, "Register camera", VirtualCameraService.RegisterCamera());

            case "--camera-unregister":
                return Report(output, "Remove camera (current user)", VirtualCameraService.UnregisterCamera());

            case "--camera-install-elevated":
                VirtualCameraService.InstallMediaSource(output);
                return 0;

            case "--camera-uninstall-elevated":
                VirtualCameraService.UninstallAll(output);
                return 0;

            case "--camera-install":
                return Install(output);

            case "--camera-remove":
            case "--camera-uninstall":
                return Uninstall(output);

            case "--camera-diagnose":
                return Diagnose(output);

            case "--camera-test":
                return Test(args, output);
        }

        return 2;
    }

    private static int Report(TextWriter output, string what, int hr)
    {
        output.WriteLine(hr == 0 ? $"{what}: OK" : $"{what}: FAILED 0x{hr:X8} ({Marshal.GetExceptionForHR(hr)?.Message})");
        return hr == 0 ? 0 : 1;
    }

    private static int Install(TextWriter output)
    {
        VirtualCameraStatus status = VirtualCameraService.GetStatus(enumerate: false);
        if (!status.ApiSupported)
        {
            output.WriteLine(VirtualCameraService.Describe(status, null));
            return 3;
        }

        // Step 1 (administrator): binaries + HKLM COM registration.
        int code;
        if (VirtualCameraService.IsElevated)
        {
            VirtualCameraService.InstallMediaSource(output);
            code = 0;
        }
        else
        {
            output.WriteLine("Requesting administrator permission to register the media source (UAC)...");
            code = VirtualCameraService.RunElevated("--camera-install-elevated", out string elevatedOutput);
            output.Write(elevatedOutput);
        }

        if (code != 0)
        {
            output.WriteLine($"Media source installation failed (exit {code}).");
            return code;
        }

        // Step 2 (this user, no admin): the camera device itself (System lifetime, CurrentUser access).
        int result = Report(output, "Register camera", VirtualCameraService.RegisterCamera());
        output.WriteLine();
        output.WriteLine(VirtualCameraService.Describe(VirtualCameraService.GetStatus(), null));
        return result;
    }

    private static int Uninstall(TextWriter output)
    {
        // The per-user camera is removed as this user first (Remove is keyed to the calling account).
        Report(output, "Remove camera (current user)", VirtualCameraService.UnregisterCamera());
        int code;
        if (VirtualCameraService.IsElevated)
        {
            VirtualCameraService.UninstallAll(output);
            code = 0;
        }
        else
        {
            output.WriteLine("Requesting administrator permission to remove the media source (UAC)...");
            code = VirtualCameraService.RunElevated("--camera-uninstall-elevated", out string elevatedOutput);
            output.Write(elevatedOutput);
        }

        output.WriteLine();
        output.WriteLine(VirtualCameraService.Describe(VirtualCameraService.GetStatus(), null));
        return code;
    }

    private static int Diagnose(TextWriter output)
    {
        VirtualCameraStatus status = VirtualCameraService.GetStatus();
        output.WriteLine(VirtualCameraService.Describe(status, new CameraFrameLink().GetState()));
        output.WriteLine();
        output.WriteLine($"Local DLL: {VirtualCameraService.LocalDllPath} (exists: {File.Exists(VirtualCameraService.LocalDllPath)})");
        output.WriteLine($"Elevated: {VirtualCameraService.IsElevated}");
        output.WriteLine($"Enumerated video cameras ({status.Cameras.Count}):");
        foreach (EnumeratedCamera camera in status.Cameras)
        {
            output.WriteLine($"  {(camera.IsOurs ? "*" : " ")} {camera.FriendlyName}  [{camera.SymbolicLink}]");
        }

        if (status.OurCamera != null)
        {
            output.WriteLine();
            return Test(new[] { "--frames", "30" }, output);
        }

        return 0;
    }

    /// <summary>Opens the camera as a Media Foundation consumer and reads frames.</summary>
    private static int Test(string[] args, TextWriter output)
    {
        int frames = int.TryParse(ArgumentValue(args, "--frames"), out int n) ? Math.Clamp(n, 1, 100_000) : 120;
        (int width, int height, int fps) = ParseFormat(ArgumentValue(args, "--format")) ?? (1280, 720, 30);
        int subtype = args.Contains("--rgb32", StringComparer.OrdinalIgnoreCase) ? VirtualCameraNative.SubtypeRgb32 : VirtualCameraNative.SubtypeNv12;

        output.WriteLine($"Opening '{VirtualCameraService.FriendlyName}' as {width}x{height} {(subtype == 1 ? "NV12" : "RGB32")} @ {fps}...");
        var stopwatch = Stopwatch.StartNew();
        using CameraConsumer consumer = CameraConsumer.Open(VirtualCameraService.FriendlyName, width, height, fps, subtype);
        output.WriteLine($"Opened in {stopwatch.ElapsedMilliseconds} ms.");

        var infos = new List<VirtualCameraNative.FrameInfo>(frames);
        int swipeFrames = 0;
        int bestSwipePixels = 0;
        uint[]? bestFrame = null;
        for (int i = 0; i < frames; i++)
        {
            infos.Add(consumer.Read());
            int swipePixels = CountNonKeyPixels(consumer.Pixels);
            if (swipePixels > 100)
            {
                swipeFrames++;
            }

            if (swipePixels > bestSwipePixels)
            {
                bestSwipePixels = swipePixels;
                bestFrame = (uint[])consumer.Pixels.Clone();
            }
        }

        FrameCadence cadence = FrameCadence.Analyze(infos, fps, width, height);
        output.WriteLine(cadence.Describe());
        output.WriteLine($"Frames showing a swipe (>100 non-background pixels): {swipeFrames}/{frames}; most swipe pixels in one frame: {bestSwipePixels}");
        string? savePath = ArgumentValue(args, "--save-frame");
        if (savePath != null && bestFrame != null)
        {
            SavePng(bestFrame, width, height, savePath);
            output.WriteLine($"Saved the frame with the most swipe pixels to {savePath}");
        }

        return cadence.Ok ? 0 : 4;
    }

    /// <summary>Pixels that are not the (YUV-decoded) chroma key or black background.</summary>
    private static int CountNonKeyPixels(uint[] pixels)
    {
        int count = 0;
        foreach (uint p in pixels)
        {
            int r = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
            bool key = g >= 240 && r <= 20 && b <= 20;
            bool black = r <= 12 && g <= 12 && b <= 12;
            if (!key && !black)
            {
                count++;
            }
        }

        return count;
    }

    private static void SavePng(uint[] pixels, int width, int height, string path)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy((int[])(object)pixels, y * width, data.Scan0 + y * data.Stride, width);
        }

        bitmap.UnlockBits(data);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static (int, int, int)? ParseFormat(string? text)
    {
        // e.g. 1280x720@30
        if (text == null)
        {
            return null;
        }

        string[] parts = text.Split('x', '@');
        return parts.Length == 3 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && int.TryParse(parts[2], out int f)
            ? (w, h, f)
            : null;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>Timestamp / cadence checks for frames read from the camera.</summary>
public sealed record FrameCadence(
    int Frames,
    int ExpectedFps,
    double MeasuredFps,
    double MeanIntervalMs,
    double MaxIntervalMs,
    bool TimestampsIncreasing,
    long ExpectedDuration100ns,
    bool DurationsCorrect,
    int Discontinuities,
    bool SizesCorrect)
{
    public bool Ok => TimestampsIncreasing && DurationsCorrect && SizesCorrect && Math.Abs(MeasuredFps - ExpectedFps) <= ExpectedFps * 0.15;

    public static FrameCadence Analyze(IReadOnlyList<VirtualCameraNative.FrameInfo> infos, int fps, int width = 0, int height = 0)
    {
        bool increasing = true;
        bool durations = true;
        bool sizes = true;
        int discontinuities = 0;
        double maxInterval = 0;
        long expectedDuration = 10_000_000L / fps;
        for (int i = 0; i < infos.Count; i++)
        {
            VirtualCameraNative.FrameInfo info = infos[i];
            if ((info.Flags & 1) != 0)
            {
                discontinuities++;
            }

            if (Math.Abs(info.Duration100ns - expectedDuration) > 1)
            {
                durations = false;
            }

            if (width > 0 && (info.Width != width || info.Height != height))
            {
                sizes = false;
            }

            if (i > 0)
            {
                if (info.Timestamp100ns <= infos[i - 1].Timestamp100ns)
                {
                    increasing = false;
                }

                maxInterval = Math.Max(maxInterval, (info.Timestamp100ns - infos[i - 1].Timestamp100ns) / 10_000.0);
            }
        }

        // Skip the first two frames: stream start-up latency is not cadence.
        int first = Math.Min(2, Math.Max(0, infos.Count - 2));
        double span = infos.Count - first > 1 ? (infos[^1].Timestamp100ns - infos[first].Timestamp100ns) / 10_000_000.0 : 0;
        double measured = span > 0 ? (infos.Count - 1 - first) / span : 0;
        double mean = measured > 0 ? 1000.0 / measured : 0;
        return new FrameCadence(infos.Count, fps, measured, mean, maxInterval, increasing, expectedDuration, durations, discontinuities, sizes);
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Frames} frames, measured {MeasuredFps:0.00} fps (expected {ExpectedFps}), mean interval {MeanIntervalMs:0.00} ms, " +
        $"max interval {MaxIntervalMs:0.00} ms, timestamps increasing: {TimestampsIncreasing}, " +
        $"durations = {ExpectedDuration100ns} (100 ns): {DurationsCorrect}, discontinuity flags: {Discontinuities}" +
        $"{(SizesCorrect ? string.Empty : ", WRONG FRAME SIZE")} => {(Ok ? "OK" : "FAIL")}");
}

/// <summary>Makes stdout visible when the GUI-subsystem executable is started from a console.</summary>
internal static class ConsoleOutput
{
    private const int AttachParentProcess = -1;

    public static void Attach()
    {
        if (Console.IsOutputRedirected)
        {
            return; // stdout already goes to a file/pipe
        }

        if (AttachConsole(AttachParentProcess))
        {
            var writer = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(writer);
            Console.WriteLine();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
