using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using MouseSwipeVisualizer.Utilities;
using N = MouseSwipeVisualizer.Camera.VirtualCameraNative;

namespace MouseSwipeVisualizer.Camera;

/// <summary>A video camera as seen by standard Windows (Media Foundation) enumeration.</summary>
public readonly record struct EnumeratedCamera(string FriendlyName, string SymbolicLink, bool IsOurs);

/// <summary>Everything the settings window and <c>--camera-status</c> report.</summary>
public sealed record VirtualCameraStatus(
    int WindowsBuild,
    bool NativeDllLoaded,
    bool BuildOk,
    bool ApiPresent,
    bool TypeSupported,
    bool SourceRegistered,
    string? RegisteredPath,
    bool RegisteredFileExists,
    IReadOnlyList<EnumeratedCamera> Cameras,
    string? Error)
{
    public bool ApiSupported => BuildOk && ApiPresent && TypeSupported;

    public EnumeratedCamera? OurCamera => Cameras.Where(c => c.IsOurs).Select(c => (EnumeratedCamera?)c).FirstOrDefault();
}

/// <summary>
/// Registration, removal and diagnostics of the native Windows virtual camera.
/// <para>
/// Two different things need to be set up, with different rights:
/// </para>
/// <list type="number">
/// <item>COM registration of the media source DLL in HKLM (+ copying it to Program Files, where the
/// Frame Server services running as LOCAL SERVICE / SYSTEM can read it). This needs administrator
/// rights once, at install time (<c>--camera-install</c>).</item>
/// <item>The virtual camera itself, created with <c>MFCreateVirtualCamera(SoftwareCameraSource,
/// System lifetime, CurrentUser access)</c>. CurrentUser access needs no elevation and exposes the
/// camera only to this Windows account. System lifetime keeps it registered across app restarts and
/// reboots, so Medal can see it even when the app is not running (it then streams the fallback
/// background). The app re-creates it idempotently at start-up; uninstall always removes it.</item>
/// </list>
/// </summary>
public static class VirtualCameraService
{
    public const string FriendlyName = "Mouse Swipe Visualizer Camera";
    public const string ClsidString = "{0728D89A-2065-4F45-85F7-B128DA227817}";
    public const int Lifetime = N.LifetimeSystem;
    public const int Access = N.AccessCurrentUser;

    public static string InstallRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MouseSwipeVisualizer", "VirtualCamera");

    private static readonly string ClsidKey = $@"SOFTWARE\Classes\CLSID\{ClsidString}";

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string LocalDllPath => Path.Combine(AppContext.BaseDirectory, N.DllName);

    // ------------------------------------------------------------------ status

    public static VirtualCameraStatus GetStatus(bool enumerate = true)
    {
        try
        {
            int hr = N.MsvCam_QueryApiSupport(out int build, out int flags);
            Marshal.ThrowExceptionForHR(hr);
            string? path = GetRegisteredPath();
            IReadOnlyList<EnumeratedCamera> cameras = enumerate ? EnumerateCameras() : Array.Empty<EnumeratedCamera>();
            return new VirtualCameraStatus(build, true,
                (flags & N.ApiBuildOk) != 0, (flags & N.ApiCreatePresent) != 0, (flags & N.ApiTypeSupported) != 0,
                (flags & N.ApiSourceRegistered) != 0, path, path != null && File.Exists(path), cameras, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new VirtualCameraStatus(Environment.OSVersion.Version.Build, false, false, false, false, false, null, false,
                Array.Empty<EnumeratedCamera>(), $"Native camera component not found ({N.DllName}): {ex.Message}");
        }
        catch (Exception ex) when (ex is COMException or Win32Exception)
        {
            return new VirtualCameraStatus(Environment.OSVersion.Version.Build, true, false, false, false, false, null, false,
                Array.Empty<EnumeratedCamera>(), ex.Message);
        }
    }

    public static string? GetRegisteredPath()
    {
        var buffer = new char[1024];
        int hr = N.MsvCam_GetRegisteredServerPath(buffer, buffer.Length);
        return hr == 0 ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var n and >= 0 ? n : buffer.Length) : null;
    }

    public static IReadOnlyList<EnumeratedCamera> EnumerateCameras()
    {
        var buffer = new char[64 * 1024];
        int hr = N.MsvCam_EnumerateCameras(buffer, buffer.Length, out _);
        if (hr != 0)
        {
            Logger.Warn($"Camera enumeration failed: 0x{hr:X8}");
            return Array.Empty<EnumeratedCamera>();
        }

        var result = new List<EnumeratedCamera>();
        string text = new(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0')));
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t');
            if (parts.Length >= 3)
            {
                result.Add(new EnumeratedCamera(parts[0], parts[1], parts[2] == "1"));
            }
        }

        return result;
    }

    public static string Describe(VirtualCameraStatus status, CameraLinkState? link)
    {
        static string YesNo(bool value) => value ? "YES" : "NO";
        var sb = new StringBuilder();
        if (!status.NativeDllLoaded)
        {
            sb.AppendLine(status.Error);
            return sb.ToString();
        }

        if (!status.ApiSupported)
        {
            sb.AppendLine("Native Windows Virtual Camera is not supported on this Windows version.");
            sb.AppendLine("Requires Windows 11 build 22000 or later.");
        }

        EnumeratedCamera? ours = status.OurCamera;
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows build: {status.WindowsBuild}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Virtual Camera API supported: {YesNo(status.ApiSupported)}");
        string registeredPath = status.RegisteredPath != null ? $" ({status.RegisteredPath}{(status.RegisteredFileExists ? "" : " - FILE MISSING")})" : "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"MediaSource registered: {YesNo(status.SourceRegistered && status.RegisteredFileExists)}{registeredPath}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Virtual camera registered: {YesNo(ours != null)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Virtual camera enumerable: {YesNo(ours != null)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Camera friendly name: {(ours?.FriendlyName ?? FriendlyName)}");
        if (link is { } l)
        {
            string subtype = l.RequestedSubtype switch { 1 => "NV12", 2 => "RGB32", _ => "?" };
            sb.AppendLine(CultureInfo.InvariantCulture, $"Camera active consumer: {YesNo(l.ConsumerActive)}");
            sb.AppendLine(l.RequestedWidth > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Requested format: {l.RequestedWidth}x{l.RequestedHeight} {subtype} @ {l.RequestedFps:0.##} FPS")
                : "Requested format: -");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Frames produced: {l.FramesProduced}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Frames consumed: {l.FramesDelivered}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Camera side: conversion {l.ConvertMicros / 1000.0:0.000} ms, sample creation total {l.DeliverMicros / 1000.0:0.000} ms (avg)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Dropped/repeated frames: {l.FramesDroppedByProducer} dropped / {l.FramesRepeated} repeated, {l.FramesFallback} fallback");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Last consumer start: {(l.LastConsumerStartUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")} (starts: {l.StreamStarts})");
            string error = l.LastErrorHr != 0 ? $"0x{l.LastErrorHr:X8}" : l.LastProblem ?? "-";
            sb.AppendLine(CultureInfo.InvariantCulture, $"Last error: {error}");
        }

        string ready = status.ApiSupported && status.SourceRegistered && status.RegisteredFileExists && ours != null ? "Ready" : "Not ready";
        sb.Append(CultureInfo.InvariantCulture, $"Virtual Camera: {(ours != null ? "Registered" : "Not registered")}   Status: {ready}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ camera device (no admin)

    /// <summary>Creates (or re-opens) the virtual camera. Idempotent; no elevation needed.</summary>
    public static int RegisterCamera()
    {
        int hr = N.MsvCam_CreateCamera(Lifetime, Access);
        Log(hr, "Register virtual camera");
        return hr;
    }

    /// <summary>Removes the virtual camera of the current user (IMFVirtualCamera::Remove). Idempotent.</summary>
    public static int UnregisterCamera()
    {
        int hr = N.MsvCam_RemoveCamera(Lifetime, Access);
        Log(hr, "Remove virtual camera");
        return hr;
    }

    private static void Log(int hr, string what)
    {
        if (hr == 0)
        {
            Logger.Info($"{what}: OK.");
        }
        else
        {
            Logger.Warn($"{what} failed: 0x{hr:X8} ({new Win32Exception(hr).Message})");
        }
    }

    // ------------------------------------------------------------------ install / uninstall (admin)

    /// <summary>
    /// Elevated part of the installation: copies the media source DLL to Program Files (into a folder
    /// named after its content hash, so an updated DLL never collides with a copy the Frame Server still
    /// has loaded) and registers the CLSID in HKLM. Idempotent.
    /// </summary>
    public static void InstallMediaSource(TextWriter output)
    {
        EnsureElevated();
        string source = LocalDllPath;
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"{N.DllName} not found next to the executable.", source);
        }

        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))[..12];
        string targetDir = Path.Combine(InstallRoot, hash);
        string target = Path.Combine(targetDir, N.DllName);
        Directory.CreateDirectory(targetDir);
        if (!File.Exists(target))
        {
            File.Copy(source, target);
        }

        output.WriteLine($"Media source binary: {target}");
        using (RegistryKey clsid = Registry.LocalMachine.CreateSubKey(ClsidKey, writable: true))
        {
            clsid.SetValue(null, "Mouse Swipe Visualizer virtual camera media source");
            using RegistryKey server = clsid.CreateSubKey("InprocServer32", writable: true);
            server.SetValue(null, target);
            server.SetValue("ThreadingModel", "Both");
        }

        output.WriteLine($"COM registration: HKLM\\{ClsidKey}\\InprocServer32 -> {target}");
        // Restart the camera services so they pick up the new registration immediately.
        StopFrameServer(output);
        RemoveStaleInstallFolders(targetDir, output);
        Logger.Info($"Media source installed to {target}.");
    }

    /// <summary>
    /// Elevated uninstall: stops camera use, removes every camera device created from our CLSID (all
    /// users, any lifetime), unregisters the COM class and deletes the binaries. Idempotent.
    /// </summary>
    public static void UninstallAll(TextWriter output)
    {
        EnsureElevated();
        int hr = N.MsvCam_RemoveCamera(Lifetime, Access);
        output.WriteLine($"IMFVirtualCamera::Remove (current user): 0x{hr:X8}");
        hr = N.MsvCam_RemoveAllDevnodes(out int removed);
        output.WriteLine($"Removed camera device nodes using our media source: {removed} (0x{hr:X8})");

        StopFrameServer(output);
        Registry.LocalMachine.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
        output.WriteLine($"COM registration removed: HKLM\\{ClsidKey}");

        if (Directory.Exists(InstallRoot))
        {
            foreach (string dir in Directory.GetDirectories(InstallRoot))
            {
                DeleteOrScheduleDirectory(dir, output);
            }

            TryDeleteEmpty(InstallRoot);
            TryDeleteEmpty(Path.GetDirectoryName(InstallRoot)!);
        }

        Logger.Info("Virtual camera uninstalled.");
    }

    private static void RemoveStaleInstallFolders(string keep, TextWriter output)
    {
        foreach (string dir in Directory.GetDirectories(InstallRoot))
        {
            if (!string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            {
                DeleteOrScheduleDirectory(dir, output);
            }
        }
    }

    private static void DeleteOrScheduleDirectory(string dir, TextWriter output)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            output.WriteLine($"Deleted {dir}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still loaded by the Frame Server: delete at the next reboot instead of failing.
            foreach (string file in Directory.GetFiles(dir))
            {
                MoveFileEx(file, null, MoveFileDelayUntilReboot);
            }

            MoveFileEx(dir, null, MoveFileDelayUntilReboot);
            output.WriteLine($"{dir} is in use; scheduled for deletion at the next reboot.");
        }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not delete {dir}: {ex.Message}");
        }
    }

    /// <summary>Stops the camera services so they release the DLL (they are trigger-started again on demand).</summary>
    public static void StopFrameServer(TextWriter output)
    {
        foreach (string name in new[] { "FrameServer", "FrameServerMonitor" })
        {
            var info = new ProcessStartInfo("sc.exe", $"stop {name}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                using Process process = Process.Start(info)!;
                process.WaitForExit(10_000);
                output.WriteLine($"sc stop {name}: exit {process.ExitCode} (1062 = was not running)");
            }
            catch (Win32Exception ex)
            {
                output.WriteLine($"Could not stop {name}: {ex.Message}");
            }
        }

        Thread.Sleep(1500); // let svchost release the module
    }
    private static void EnsureElevated()
    {
        if (!IsElevated)
        {
            throw new UnauthorizedAccessException("This operation needs administrator rights (run elevated).");
        }
    }

    /// <summary>Re-runs this executable elevated (UAC prompt) with the given arguments and waits.</summary>
    public static int RunElevated(string arguments, out string output)
    {
        string resultFile = Path.Combine(Path.GetTempPath(), $"msv-elevated-{Guid.NewGuid():N}.txt");
        var info = new ProcessStartInfo(Environment.ProcessPath!, $"{arguments} --result-file \"{resultFile}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using Process process = Process.Start(info)!;
            process.WaitForExit();
            output = File.Exists(resultFile) ? File.ReadAllText(resultFile) : string.Empty;
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            output = "Cancelled: administrator permission was not granted.";
            return 1223;
        }
        finally
        {
            try
            {
                File.Delete(resultFile);
            }
            catch (IOException)
            {
            }
        }
    }

    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}
