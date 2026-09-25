using System.IO;

namespace MouseSwipeVisualizer.Utilities;

/// <summary>
/// File locations. Everything lives under %LocalAppData% rather than the roaming profile because the
/// settings contain monitor-specific window placement that is meaningless on another machine.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "MouseSwipeVisualizer";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string LogFile => Path.Combine(LogDirectory, "app.log");
}
