using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MouseSwipeVisualizer.Interop;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace MouseSwipeVisualizer.Shell;

/// <summary>
/// Notification-area icon with quick access to the windows and Exit. Also provides the app icon: the same
/// Assets/AppIcon.ico that is the exe's icon (Start menu, search), embedded as a resource.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _icon;
    private readonly Drawing.Icon? _largeIcon;

    public TrayIcon()
    {
        _icon = LoadIcon(Forms.SystemInformation.SmallIconSize) ?? CreateIcon();
        _largeIcon = LoadIcon(new Drawing.Size(256, 256));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Show preview", null, (_, _) => ShowCaptureRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Clear trail", null, (_, _) => ClearRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Mouse Swipe Visualizer",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public event Action? ShowCaptureRequested;

    public event Action? SettingsRequested;

    public event Action? ClearRequested;

    public event Action? ExitRequested;

    /// <summary>The same icon as a WPF image, for Window.Icon (taskbar / Alt+Tab).</summary>
    public ImageSource CreateWindowIcon()
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHIcon((_largeIcon ?? _icon).Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    public void ShowNotification(string title, string text, bool warning)
    {
        _notifyIcon.ShowBalloonTip(5000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    /// <summary>The embedded app icon at the closest available size, or null if the resource is missing.</summary>
    private static Drawing.Icon? LoadIcon(Drawing.Size size)
    {
        using System.IO.Stream? stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("MouseSwipeVisualizer.AppIcon.ico");
        return stream == null ? null : new Drawing.Icon(stream, size);
    }

    /// <summary>Fallback: draws the small swipe-arrow icon.</summary>
    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 28, 30, 38));
            g.FillEllipse(background, 1, 1, 30, 30);
            using var arrowCap = new Drawing2D.AdjustableArrowCap(2.6f, 2.6f);
            using var pen = new Drawing.Pen(Drawing.Color.White, 3.2f)
            {
                StartCap = Drawing2D.LineCap.Round,
                CustomEndCap = arrowCap,
            };
            g.DrawBezier(pen, 7, 23, 12, 22, 16, 12, 24, 9);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _largeIcon?.Dispose();
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
