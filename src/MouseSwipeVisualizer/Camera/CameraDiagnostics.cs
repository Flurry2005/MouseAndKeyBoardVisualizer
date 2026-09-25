using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Camera;

/// <summary>
/// Camera section of the settings window's diagnostics. Enumerating cameras costs tens of
/// milliseconds and must not run on the UI thread, so the status is refreshed in the background and
/// the UI always gets the last snapshot immediately.
/// </summary>
public sealed class CameraDiagnostics
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly CameraFrameLink _link;
    private VirtualCameraStatus? _status;
    private DateTime _lastRefresh = DateTime.MinValue;
    private int _refreshing;

    public CameraDiagnostics(CameraFrameLink link)
    {
        _link = link;
    }

    public string Text()
    {
        if (DateTime.UtcNow - _lastRefresh > RefreshInterval && Interlocked.Exchange(ref _refreshing, 1) == 0)
        {
            _lastRefresh = DateTime.UtcNow;
            Task.Run(() =>
            {
                try
                {
                    _status = VirtualCameraService.GetStatus();
                }
                finally
                {
                    Volatile.Write(ref _refreshing, 0);
                }
            });
        }

        VirtualCameraStatus? status = _status;
        return "Virtual camera:\n" + (status == null ? "(checking...)" : VirtualCameraService.Describe(status, _link.GetState()));
    }

    /// <summary>One line for the settings window's Output group.</summary>
    public string Summary()
    {
        VirtualCameraStatus? status = _status;
        if (status == null)
        {
            Text(); // triggers the first background refresh
            return "Virtual camera: checking…";
        }

        if (!status.NativeDllLoaded)
        {
            return "Virtual camera: native component missing (" + status.Error + ")";
        }

        if (!status.ApiSupported)
        {
            return "Native Windows Virtual Camera is not supported on this Windows version. Requires Windows 11 build 22000 or later.";
        }

        if (!status.SourceRegistered || !status.RegisteredFileExists)
        {
            return "Virtual camera: not installed – click \"Install / repair camera\".";
        }

        if (status.OurCamera is not { } camera)
        {
            return "Virtual camera: media source installed, camera not registered – click \"Install / repair camera\".";
        }

        CameraLinkState link = _link.GetState();
        string consumer = link.ConsumerActive
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"streaming {link.RequestedWidth}x{link.RequestedHeight} @ {link.RequestedFps:0.##} fps")
            : "no app is using it";
        return $"Virtual camera: Ready – \"{camera.FriendlyName}\" ({consumer}).";
    }

    /// <summary>Settings-window buttons. Runs off the UI thread; returns a message for the user.</summary>
    public static Task<string> RunActionAsync(CameraAction action) => Task.Run(() =>
    {
        var output = new System.IO.StringWriter();
        try
        {
            switch (action)
            {
                case CameraAction.Install:
                    if (!VirtualCameraService.GetStatus(enumerate: false).ApiSupported)
                    {
                        return "Native Windows Virtual Camera is not supported on this Windows version.\nRequires Windows 11 build 22000 or later.";
                    }

                    int code = 0;
                    if (VirtualCameraService.IsElevated)
                    {
                        VirtualCameraService.InstallMediaSource(output);
                    }
                    else
                    {
                        code = VirtualCameraService.RunElevated("--camera-install-elevated", out string installOutput);
                        output.Write(installOutput);
                    }

                    if (code != 0)
                    {
                        return $"Installation failed or was cancelled (code {code}).\n\n{output}";
                    }

                    int hr = VirtualCameraService.RegisterCamera();
                    output.WriteLine(hr == 0 ? "Camera registered." : $"Registering the camera failed: 0x{hr:X8}");
                    break;

                case CameraAction.Remove:
                    VirtualCameraService.UnregisterCamera();
                    int removeCode = VirtualCameraService.RunElevated("--camera-uninstall-elevated", out string removeOutput);
                    output.Write(removeOutput);
                    if (removeCode != 0)
                    {
                        output.WriteLine($"(elevated part: code {removeCode})");
                    }

                    break;

                case CameraAction.Test:
                    using (CameraConsumer consumer = CameraConsumer.Open(VirtualCameraService.FriendlyName, 1280, 720, 30, VirtualCameraNative.SubtypeNv12))
                    {
                        var infos = new List<VirtualCameraNative.FrameInfo>();
                        for (int i = 0; i < 60; i++)
                        {
                            infos.Add(consumer.Read());
                        }

                        output.WriteLine("Read 60 frames from \"" + VirtualCameraService.FriendlyName + "\" (1280x720 NV12 @ 30):");
                        output.WriteLine(FrameCadence.Analyze(infos, 30, 1280, 720).Describe());
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Camera action {action} failed.", ex);
            output.WriteLine("Error: " + ex.Message);
        }

        return output.ToString();
    });

    /// <summary>Start-up: (re)creates the camera for this user when the media source is installed.</summary>
    public static void EnsureCameraRegistered()
    {
        try
        {
            VirtualCameraStatus status = VirtualCameraService.GetStatus(enumerate: false);
            if (!status.ApiSupported)
            {
                Logger.Warn("Native Windows Virtual Camera is not supported on this Windows version (requires Windows 11 build 22000 or later).");
                return;
            }

            if (!status.SourceRegistered || !status.RegisteredFileExists)
            {
                Logger.Warn("Virtual camera media source is not installed; run MouseSwipeVisualizer.exe --camera-install (or use Settings).");
                return;
            }

            VirtualCameraService.RegisterCamera();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not register the virtual camera at start-up.", ex);
        }
    }
}
