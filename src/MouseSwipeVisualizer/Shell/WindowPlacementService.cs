using System.Runtime.InteropServices;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Utilities;
using static MouseSwipeVisualizer.Interop.NativeMethods;

namespace MouseSwipeVisualizer.Shell;

internal readonly record struct MonitorDescription(string Device, RECT Bounds, RECT WorkArea, bool IsPrimary);

/// <summary>
/// Saves and restores window bounds in physical pixels.
/// <para>
/// With per-monitor DPI awareness, WPF's DIP coordinates are relative to each monitor's scale, which
/// makes them ambiguous across monitors with different scaling. Physical pixels from GetWindowRect /
/// SetWindowPos are unambiguous, so those are what is stored.
/// </para>
/// Restoring handles removed monitors, changed resolutions and nonsense values by always ending with
/// a rectangle that lies completely on an existing monitor.
/// </summary>
internal static class WindowPlacementService
{
    public const int MinSizePx = 120;
    private const int FallbackSizePx = 400;

    public static List<MonitorDescription> GetMonitors()
    {
        var monitors = new List<MonitorDescription>();
        MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorDescription(info.szDevice, info.rcMonitor, info.rcWork,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            Logger.Warn("EnumDisplayMonitors failed.");
        }

        GC.KeepAlive(callback);
        return monitors;
    }

    public static WindowPlacementSettings? Capture(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var placement = new WindowPlacementSettings
        {
            X = rect.Left,
            Y = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
        };

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            placement.MonitorDevice = info.szDevice;
            placement.MonitorLeft = info.rcMonitor.Left;
            placement.MonitorTop = info.rcMonitor.Top;
            placement.MonitorWidth = info.rcMonitor.Width;
            placement.MonitorHeight = info.rcMonitor.Height;
        }

        return placement;
    }

    /// <summary>Pure function (testable): where the window should go given the saved data and current monitors.</summary>
    /// <param name="saved">Saved placement (may be null or garbage).</param>
    /// <param name="monitors">Current monitors.</param>
    /// <param name="shrinkToFit">
    /// false for the capture window: its size is the capture resolution and must never change, so it
    /// may extend past the monitor edge; only its position is corrected so the title bar stays reachable.
    /// </param>
    /// <param name="defaultWidth">Size used when nothing valid is saved (0 = a third of the work area).</param>
    public static RECT ComputeRestoreRect(WindowPlacementSettings? saved, IReadOnlyList<MonitorDescription> monitors,
        bool shrinkToFit = true, int defaultWidth = 0, int defaultHeight = 0)
    {
        if (monitors.Count == 0)
        {
            return new RECT(100, 100, 100 + FallbackSizePx, 100 + FallbackSizePx);
        }

        MonitorDescription primary = monitors[0];
        foreach (MonitorDescription m in monitors)
        {
            if (m.IsPrimary)
            {
                primary = m;
                break;
            }
        }

        if (saved == null || !saved.HasSize)
        {
            return DefaultRect(primary, defaultWidth, defaultHeight, shrinkToFit);
        }

        MonitorDescription? match = null;
        if (!string.IsNullOrEmpty(saved.MonitorDevice))
        {
            foreach (MonitorDescription m in monitors)
            {
                if (string.Equals(m.Device, saved.MonitorDevice, StringComparison.OrdinalIgnoreCase))
                {
                    match = m;
                    break;
                }
            }
        }

        MonitorDescription target = match ?? primary;
        int x = saved.X;
        int y = saved.Y;
        bool sameGeometry = match.HasValue
                            && target.Bounds.Left == saved.MonitorLeft && target.Bounds.Top == saved.MonitorTop
                            && target.Bounds.Width == saved.MonitorWidth && target.Bounds.Height == saved.MonitorHeight;
        if (!sameGeometry && saved.MonitorWidth > 0 && saved.MonitorHeight > 0)
        {
            // The monitor moved, changed resolution or is gone: keep the same offset from its top-left corner.
            x = target.Bounds.Left + (saved.X - saved.MonitorLeft);
            y = target.Bounds.Top + (saved.Y - saved.MonitorTop);
        }

        return ClampInto(new RECT(x, y, x + saved.Width, y + saved.Height), target.Bounds, shrinkToFit);
    }

    public static RECT DefaultRect(MonitorDescription monitor, int width = 0, int height = 0, bool shrinkToFit = true)
    {
        RECT work = monitor.WorkArea;
        if (width <= 0 || height <= 0)
        {
            width = height = Math.Max(MinSizePx, Math.Min(work.Width, work.Height) / 3);
        }

        int x = work.Left + (work.Width - width) / 2;
        int y = work.Top + (work.Height - height) / 2;
        return ClampInto(new RECT(x, y, x + width, y + height), work, shrinkToFit);
    }

    public static RECT ClampInto(RECT rect, RECT area, bool shrinkToFit = true)
    {
        int width = shrinkToFit ? Math.Clamp(rect.Width, Math.Min(MinSizePx, area.Width), area.Width) : rect.Width;
        int height = shrinkToFit ? Math.Clamp(rect.Height, Math.Min(MinSizePx, area.Height), area.Height) : rect.Height;

        // Oversized windows are pinned to the top-left corner so the title bar stays on screen.
        int x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - width));
        int y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        return new RECT(x, y, x + width, y + height);
    }

    /// <summary>
    /// Moves the window to <paramref name="rect"/> (physical pixels). Applied twice: if the move
    /// crosses into a monitor with different DPI, WPF resizes the window on WM_DPICHANGED, and the
    /// second call restores the exact size.
    /// </summary>
    public static void Apply(IntPtr hwnd, RECT rect)
    {
        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        for (int i = 0; i < 2; i++)
        {
            if (!SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, flags))
            {
                Logger.Warn($"SetWindowPos failed while restoring placement (error {Marshal.GetLastPInvokeError()}).");
                return;
            }
        }
    }

    /// <summary>After display changes: pulls the window back if it's no longer fully on a monitor.</summary>
    public static void EnsureOnScreen(IntPtr hwnd, bool shrinkToFit = true)
    {
        WindowPlacementSettings? current = Capture(hwnd);
        if (current == null)
        {
            return;
        }

        RECT target = ComputeRestoreRect(current, GetMonitors(), shrinkToFit);
        if (target.Left != current.X || target.Top != current.Y || target.Width != current.Width || target.Height != current.Height)
        {
            Logger.Info($"Window moved back on screen: ({current.X},{current.Y} {current.Width}x{current.Height}) -> ({target.Left},{target.Top} {target.Width}x{target.Height}).");
            Apply(hwnd, target);
        }
    }
}
