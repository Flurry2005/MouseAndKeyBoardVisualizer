using System.Diagnostics;
using System.IO;
using System.Text;

namespace MouseSwipeVisualizer.Utilities;

/// <summary>
/// Minimal thread-safe file logger. Meant for lifecycle events only (startup, registration results,
/// mode changes, errors) - never for per-mouse-event data. When the file exceeds
/// <see cref="MaxLogBytes"/> it is rotated to app.old.log, so at most two files ever exist.
/// </summary>
public static class Logger
{
    public const long MaxLogBytes = 1024 * 1024;
    private const int SizeCheckInterval = 50;

    private static readonly object Gate = new();
    private static string? _path;
    private static int _writesSinceSizeCheck;

    public static string? LogFilePath => _path;

    public static void Initialize(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded();
            }
            catch (Exception ex)
            {
                // Logging must never take the app down; fall back to the debugger output only.
                Debug.WriteLine($"Logger initialization failed: {ex}");
                _path = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = new StringBuilder(128)
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level).Append("] ")
            .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message);
        if (ex != null)
        {
            line.AppendLine().Append(ex);
        }

        line.AppendLine();
        string text = line.ToString();
        Debug.Write(text);

        lock (Gate)
        {
            if (_path == null)
            {
                return;
            }

            try
            {
                if (++_writesSinceSizeCheck >= SizeCheckInterval)
                {
                    _writesSinceSizeCheck = 0;
                    RotateIfNeeded();
                }

                File.AppendAllText(_path, text, Encoding.UTF8);
            }
            catch (IOException ioEx)
            {
                Debug.WriteLine($"Log write failed: {ioEx.Message}");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                Debug.WriteLine($"Log write failed: {uaEx.Message}");
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (_path == null)
        {
            return;
        }

        var info = new FileInfo(_path);
        if (info.Exists && info.Length > MaxLogBytes)
        {
            string old = Path.ChangeExtension(_path, ".old.log");
            File.Move(_path, old, overwrite: true);
        }
    }
}
