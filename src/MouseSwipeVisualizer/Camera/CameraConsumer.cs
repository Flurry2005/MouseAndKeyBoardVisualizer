using System.Runtime.InteropServices;
using N = MouseSwipeVisualizer.Camera.VirtualCameraNative;

namespace MouseSwipeVisualizer.Camera;

/// <summary>
/// Media Foundation consumer of a camera (enumerate → activate → IMFSourceReader), used by the
/// integration tests and <c>--camera-test</c>. It opens the virtual camera exactly the way other
/// applications (Medal, the Camera app) do: through the Windows Camera Frame Server.
/// Reads block, so use it from a background thread.
/// </summary>
public sealed class CameraConsumer : IDisposable
{
    private IntPtr _handle;
    private readonly uint[] _pixels;

    private CameraConsumer(IntPtr handle, int width, int height, int fps, int subtype)
    {
        _handle = handle;
        Width = width;
        Height = height;
        Fps = fps;
        Subtype = subtype;
        _pixels = new uint[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public int Fps { get; }

    /// <summary>1 = NV12, 2 = RGB32.</summary>
    public int Subtype { get; }

    /// <summary>The last frame read, converted to BGRA (0xAARRGGBB).</summary>
    public uint[] Pixels => _pixels;

    public static CameraConsumer Open(string friendlyNamePrefix, int width, int height, int fps, int subtype)
    {
        int hr = N.MsvCam_ConsumerOpen(friendlyNamePrefix, width, height, fps, subtype, out IntPtr handle);
        Marshal.ThrowExceptionForHR(hr);
        return new CameraConsumer(handle, width, height, fps, subtype);
    }

    /// <summary>
    /// Test-only: activates the media source inside this process (no Frame Server, no registration)
    /// and reads it through a real IMFSourceReader.
    /// </summary>
    public static CameraConsumer OpenInProcess(int width, int height, int fps, int subtype)
    {
        int hr = N.MsvCam_ConsumerOpenInProc(width, height, fps, subtype, out IntPtr handle);
        Marshal.ThrowExceptionForHR(hr);
        return new CameraConsumer(handle, width, height, fps, subtype);
    }

    /// <summary>Reads the next frame into <see cref="Pixels"/>.</summary>
    public N.FrameInfo Read()
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        int hr = N.MsvCam_ConsumerRead(_handle, _pixels, _pixels.Length, out N.FrameInfo info);
        Marshal.ThrowExceptionForHR(hr);
        return info;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            N.MsvCam_ConsumerClose(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
