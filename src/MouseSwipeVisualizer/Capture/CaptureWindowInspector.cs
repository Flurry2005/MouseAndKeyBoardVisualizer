using System.Globalization;
using System.Text;
using static MouseSwipeVisualizer.Interop.NativeMethods;

namespace MouseSwipeVisualizer.Capture;

/// <summary>Win32 facts about a window that decide whether OBS Window Capture can list and capture it.</summary>
public sealed record CaptureWindowReport(
    IntPtr Hwnd,
    string Title,
    long Style,
    long ExStyle,
    bool Visible,
    bool Minimized,
    bool Cloaked,
    IntPtr Owner,
    IntPtr Parent,
    int ClientWidth,
    int ClientHeight,
    uint Dpi)
{
    public bool IsTopmost => (ExStyle & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Mirrors OBS's window filter (plugins/win-capture/window-helpers.c, check_window_valid):
    /// visible, not minimized/cloaked, no WS_EX_TOOLWINDOW, no WS_CHILD, non-empty client area.
    /// </summary>
    public bool IsObsListable =>
        Visible && !Minimized && !Cloaked
        && (ExStyle & WS_EX_TOOLWINDOW) == 0
        && (Style & WS_CHILD) == 0
        && ClientWidth > 0 && ClientHeight > 0;

    /// <summary>Everything that would stop OBS from listing/capturing the window, or degrade the capture.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (Hwnd == IntPtr.Zero) problems.Add("no HWND");
        if (!Visible) problems.Add("not visible");
        if (Minimized) problems.Add("minimized (OBS skips minimized windows and WPF stops rendering)");
        if (Cloaked) problems.Add("cloaked by DWM (e.g. on another virtual desktop)");
        if ((ExStyle & WS_EX_TOOLWINDOW) != 0) problems.Add("WS_EX_TOOLWINDOW (OBS does not list tool windows)");
        if ((Style & WS_CHILD) != 0) problems.Add("WS_CHILD");
        if ((ExStyle & WS_EX_LAYERED) != 0) problems.Add("WS_EX_LAYERED (desktop-overlay style)");
        if ((ExStyle & WS_EX_TRANSPARENT) != 0) problems.Add("WS_EX_TRANSPARENT (click-through overlay style)");
        if (Owner != IntPtr.Zero) problems.Add("has an owner window");
        if (ClientWidth <= 0 || ClientHeight <= 0) problems.Add("empty client area");
        return problems;
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        CultureInfo c = CultureInfo.InvariantCulture;
        sb.Append(c, $"HWND 0x{Hwnd.ToInt64():X}  title \"{Title}\"\n");
        sb.Append(c, $"style 0x{Style:X8}  exStyle 0x{ExStyle:X8}\n");
        sb.Append(c, $"client {ClientWidth}x{ClientHeight} px  DPI {Dpi} ({Dpi / 96.0:P0})\n");
        sb.Append(c, $"visible {Visible}  minimized {Minimized}  cloaked {Cloaked}  topmost {IsTopmost}\n");
        sb.Append(c, $"toolwindow {(ExStyle & WS_EX_TOOLWINDOW) != 0}  layered {(ExStyle & WS_EX_LAYERED) != 0}  " +
                     $"transparent {(ExStyle & WS_EX_TRANSPARENT) != 0}  child {(Style & WS_CHILD) != 0}  owner 0x{Owner.ToInt64():X}\n");
        List<string> problems = Problems();
        sb.Append(IsObsListable && problems.Count == 0
            ? "OBS Window Capture: listable"
            : "OBS Window Capture: " + string.Join("; ", problems));
        return sb.ToString();
    }
}

public static class CaptureWindowInspector
{
    public static CaptureWindowReport Inspect(IntPtr hwnd)
    {
        GetClientRect(hwnd, out RECT client);
        bool cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloakedValue, sizeof(int)) == 0 && cloakedValue != 0;
        return new CaptureWindowReport(
            hwnd,
            GetWindowTitle(hwnd),
            GetWindowStyle(hwnd),
            GetWindowExStyle(hwnd),
            IsWindowVisible(hwnd),
            IsIconic(hwnd),
            cloaked,
            GetWindow(hwnd, GW_OWNER),
            GetParent(hwnd),
            client.Width,
            client.Height,
            GetDpiForWindow(hwnd));
    }

    /// <summary>
    /// Top-level windows OBS would offer in Window Capture → Window, using the same filter.
    /// (OBS additionally hides its own windows; that doesn't matter here.)
    /// </summary>
    public static List<(IntPtr Hwnd, string Title)> EnumerateObsCandidates()
    {
        var result = new List<(IntPtr, string)>();
        EnumWindowsProc callback = (hwnd, _) =>
        {
            CaptureWindowReport report = Inspect(hwnd);
            if (report.IsObsListable && report.Title.Length > 0)
            {
                result.Add((hwnd, report.Title));
            }

            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }
}
