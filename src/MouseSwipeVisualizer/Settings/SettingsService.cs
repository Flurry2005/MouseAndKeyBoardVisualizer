using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Settings;

public enum SettingsLoadStatus
{
    Loaded,
    MissingUsedDefaults,
    CorruptUsedDefaults,
    UnreadableUsedDefaults,
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. A missing, unreadable or corrupt file never
/// crashes the app: defaults are used, and a corrupt file is kept as a timestamped backup so the
/// user can inspect it. Saving goes through a temp file so a crash mid-write can't corrupt it.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,

        // The file is meant to be hand-editable: keep '#' colours and '\' in monitor device names readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsService(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public SettingsLoadStatus LastLoadStatus { get; private set; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            LastLoadStatus = SettingsLoadStatus.MissingUsedDefaults;
            Logger.Info($"No settings file at {FilePath}; using defaults.");
            return new AppSettings();
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadStatus = SettingsLoadStatus.UnreadableUsedDefaults;
            Logger.Warn($"Settings file {FilePath} could not be read; using defaults.", ex);
            return new AppSettings();
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            BackupCorruptFile();
            LastLoadStatus = SettingsLoadStatus.CorruptUsedDefaults;
            Logger.Warn("Settings file is not valid JSON; falling back to defaults.", ex);
            return new AppSettings();
        }

        if (settings == null)
        {
            BackupCorruptFile();
            LastLoadStatus = SettingsLoadStatus.CorruptUsedDefaults;
            Logger.Warn("Settings file was empty/null; falling back to defaults.");
            return new AppSettings();
        }

        List<string> fixes = settings.Sanitize();
        foreach (string fix in fixes)
        {
            Logger.Warn($"Settings corrected: {fix}");
        }

        LastLoadStatus = SettingsLoadStatus.Loaded;
        Logger.Info($"Settings loaded from {FilePath}.");
        return settings;
    }

    /// <returns>false if the file could not be written (the error is logged).</returns>
    public bool Save(AppSettings settings)
    {
        string tempPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Logger.Error($"Saving settings to {FilePath} failed.", ex);
            return false;
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            string backup = Path.Combine(
                Path.GetDirectoryName(FilePath)!,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(FilePath, backup, overwrite: true);
            Logger.Warn($"Corrupt settings backed up to {backup}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Could not back up the corrupt settings file.", ex);
        }
    }
}
