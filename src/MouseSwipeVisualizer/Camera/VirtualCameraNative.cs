using System.Runtime.InteropServices;

namespace MouseSwipeVisualizer.Camera;

/// <summary>
/// P/Invoke into MouseSwipeVisualizer.VirtualCamera.dll (the same DLL the Frame Server loads as the
/// media source; these exports are only used by the app). See CameraControl.h.
/// </summary>
public static class VirtualCameraNative
{
    public const string DllName = "MouseSwipeVisualizer.VirtualCamera.dll";

    public const int ApiBuildOk = 0x1;
    public const int ApiCreatePresent = 0x2;
    public const int ApiTypeSupported = 0x4;
    public const int ApiSourceRegistered = 0x8;

    /// <summary>MFVirtualCameraLifetime.</summary>
    public const int LifetimeSession = 0;
    public const int LifetimeSystem = 1;

    /// <summary>MFVirtualCameraAccess.</summary>
    public const int AccessCurrentUser = 0;
    public const int AccessAllUsers = 1;

    public const int SubtypeNv12 = 1;
    public const int SubtypeRgb32 = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FrameInfo
    {
        public int Width;
        public int Height;
        public int Subtype;
        public int Flags;
        public long Timestamp100ns;
        public long Duration100ns;
        public long ArrivalQpc;
    }

    [DllImport(DllName)]
    internal static extern int MsvCam_QueryApiSupport(out int windowsBuild, out int flags);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern int MsvCam_GetRegisteredServerPath([Out] char[] path, int capacity);

    [DllImport(DllName)]
    internal static extern int MsvCam_CreateCamera(int lifetime, int access);

    [DllImport(DllName)]
    internal static extern int MsvCam_RemoveCamera(int lifetime, int access);

    [DllImport(DllName)]
    internal static extern int MsvCam_RemoveAllDevnodes(out int removed);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern int MsvCam_EnumerateCameras([Out] char[] buffer, int capacity, out int count);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern int MsvCam_ConsumerOpen(string namePrefix, int width, int height, int fps, int subtype, out IntPtr handle);

    [DllImport(DllName)]
    internal static extern int MsvCam_ConsumerOpenInProc(int width, int height, int fps, int subtype, out IntPtr handle);

    [DllImport(DllName)]
    internal static extern int MsvCam_ConsumerRead(IntPtr handle, [Out] uint[] bgra, int capacityPixels, out FrameInfo info);

    [DllImport(DllName)]
    internal static extern void MsvCam_ConsumerClose(IntPtr handle);
}
