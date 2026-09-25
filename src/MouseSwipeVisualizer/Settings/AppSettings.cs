using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace MouseSwipeVisualizer.Settings;

/// <summary>What is drawn at the newest point of the trail.</summary>
public enum HeadStyle
{
    Arrow,
    Dot,
    None,
}

/// <summary>Where the keyboard panel sits relative to the swipe area.</summary>
public enum KeyboardPosition
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>Where the rendered swipe goes.</summary>
public enum OutputMode
{
    /// <summary>Native Windows virtual camera "Mouse Swipe Visualizer Camera" (Media Foundation).</summary>
    NativeVirtualCamera,

    /// <summary>v2 fallback: a normal window captured by OBS Window Capture.</summary>
    ObsCaptureWindow,
}

/// <summary>Window position in physical screen pixels plus the monitor it was on.</summary>
public sealed class WindowPlacementSettings
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>GDI device name, e.g. \\.\DISPLAY2.</summary>
    public string? MonitorDevice { get; set; }

    public int MonitorLeft { get; set; }

    public int MonitorTop { get; set; }

    public int MonitorWidth { get; set; }

    public int MonitorHeight { get; set; }

    [JsonIgnore]
    public bool HasSize => Width > 0 && Height > 0;

    public WindowPlacementSettings Clone() => (WindowPlacementSettings)MemberwiseClone();
}

/// <summary>
/// User settings persisted as JSON. Every numeric value has explicit bounds; <see cref="Sanitize"/>
/// clamps hand-edited or outdated files instead of rejecting them.
/// <para>
/// Schema 2 (OBS capture window) dropped the desktop-overlay settings of schema 1 (OverlayOpacity,
/// AlwaysOnTop, BackgroundMode, BackgroundColor, hotkeys, StartInOverlayMode, ShowDebugStats).
/// Old files still load: unknown JSON properties are ignored.
/// </para>
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 4;

    public const double MinTrailLifetimeMs = 50, MaxTrailLifetimeMs = 5000, DefaultTrailLifetimeMs = 500;
    public const double MinTrailThickness = 1, MaxTrailThickness = 32, DefaultTrailThickness = 4;
    public const double MinSensitivity = 0.05, MaxSensitivity = 20, DefaultSensitivity = 1.0;
    public const double MinSmoothing = 0, MaxSmoothing = 1, DefaultSmoothing = 0.35;
    public const double MinSwipeBreakMs = 10, MaxSwipeBreakMs = 2000, DefaultSwipeBreakMs = 120;
    public const double MinLiftGapMs = 15, MaxLiftGapMs = 200, DefaultLiftGapMs = 40;
    public const int MinRenderFps = 20, MaxRenderFps = 240, DefaultRenderFps = 60;
    public const int MinCaptureSize = 100, MaxCaptureSize = 4096, DefaultCaptureSize = 800;
    public const string DefaultTrailColor = "#FFFFFF";
    public const string DefaultOutlineColor = "#141418";
    public const double MinDotSize = 2, MaxDotSize = 48, DefaultDotSize = 12;
    public const int MinKeyboardSplit = 20, MaxKeyboardSplit = 70, DefaultKeyboardSplit = 40;
    public const double MinSwipeBoxPercent = 20, MaxSwipeBoxPercent = 100;
    public const double MinSpotifyPollSeconds = 1, MaxSpotifyPollSeconds = 60, DefaultSpotifyPollSeconds = 3;
    public const int DefaultSpotifyPort = 8888;
    public const double MaxBackgroundFadeMs = 5000, DefaultBackgroundFadeMs = 600;
    public const double MaxImageBlur = 100, MaxImageDim = 90, MaxGlassOpacity = 60, MaxGlassBlur = 100;
    public const double MaxFrameBorder = 16, MaxFrameRadius = 80, MaxFrameMargin = 80, MaxFramePadding = 80;
    public const string DefaultChromaKeyColor = "#00FF00";

    /// <summary>Background when <see cref="CaptureBackgroundEnabled"/> is off: black works with OBS "Screen"/"Additive" blending.</summary>
    public const string NoKeyBackgroundColor = "#000000";

    public static readonly double[] SensitivityPresets = { 0.25, 0.5, 1.0, 1.5, 2.0, 4.0 };
    public static readonly int[] CaptureSizePresets = { 400, 600, 800, 1080 };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // ------------------------------------------------------------------ swipe

    /// <summary>Multiplier from raw mouse counts to visualizer space.</summary>
    public double SensitivityScale { get; set; } = DefaultSensitivity;

    /// <summary>Input pause (ms) after which the next movement starts a new swipe at the centre.</summary>
    public double SwipeBreakMs { get; set; } = DefaultSwipeBreakMs;

    /// <summary>0 = raw polyline, 1 = strongest visual smoothing.</summary>
    public double SmoothingStrength { get; set; } = DefaultSmoothing;

    /// <summary>
    /// Detects "lift and re-center": an abrupt sensor cut-out while still moving fast, followed by
    /// at least <see cref="LiftGapMs"/> of silence, starts a new swipe even before <see cref="SwipeBreakMs"/>.
    /// </summary>
    public bool LiftDetectionEnabled { get; set; } = true;

    public double LiftGapMs { get; set; } = DefaultLiftGapMs;

    // ------------------------------------------------------------------ trail

    /// <summary>How long a trail point stays visible (ms); it fades out over this time.</summary>
    public double TrailLifetimeMs { get; set; } = DefaultTrailLifetimeMs;

    /// <summary>Trail width in capture pixels (independent of Windows display scaling).</summary>
    public double TrailThickness { get; set; } = DefaultTrailThickness;

    /// <summary>Arrowhead, dot or nothing at the newest point (replaces v3's ArrowheadEnabled).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HeadStyle HeadStyle { get; set; } = HeadStyle.Arrow;

    public string DotColor { get; set; } = DefaultTrailColor;

    /// <summary>Head dot diameter in pixels.</summary>
    public double DotSize { get; set; } = DefaultDotSize;

    /// <summary>Colour of the outline around line, arrow and dot.</summary>
    public string OutlineColor { get; set; } = DefaultOutlineColor;

    /// <summary>Thin dark outline; with the chroma background it also acts as the key-safe edge.</summary>
    public bool OutlineEnabled { get; set; } = true;

    public string TrailColor { get; set; } = DefaultTrailColor;

    /// <summary>Upper bound for redraws per second; actual rate is also limited by the display refresh.</summary>
    public int RenderFps { get; set; } = DefaultRenderFps;

    // ------------------------------------------------------------------ keyboard

    /// <summary>Draws a keyboard (Esc–5, Tab–T, Caps–G, Shift–B, Ctrl/Alt/Space) that lights up pressed keys.</summary>
    public bool KeyboardEnabled { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public KeyboardPosition KeyboardPosition { get; set; } = KeyboardPosition.Left;

    /// <summary>Share of the content area used by the keyboard, in percent (the swipe gets the rest).</summary>
    public int KeyboardSplitPercent { get; set; } = DefaultKeyboardSplit;

    public string KeyFillColor { get; set; } = "#000000";

    public string KeyBorderColor { get; set; } = "#E6E6E6";

    public string KeyLabelColor { get; set; } = "#FFFFFF";

    public string KeyPressedFillColor { get; set; } = "#FFFFFF";

    public string KeyPressedLabelColor { get; set; } = "#000000";

    // ------------------------------------------------------------------ frame (panel behind everything)

    /// <summary>Rounded panel behind keyboard and swipe. Outside it the background (chroma key/black) shows.</summary>
    public bool FrameEnabled { get; set; } = true;

    public string FrameBackgroundColor { get; set; } = "#000000";

    public string FrameBorderColor { get; set; } = "#E6E6E6";

    public double FrameBorderWidth { get; set; } = 2;

    public double FrameCornerRadius { get; set; } = 16;

    /// <summary>Space between the image edge and the panel.</summary>
    public double FrameMargin { get; set; } = 6;

    /// <summary>Space between the panel border and its content.</summary>
    public double FramePadding { get; set; } = 14;

    /// <summary>
    /// Panel size in % of the image (centred). The content keeps its size; it only shrinks when the
    /// panel edge reaches it.
    /// </summary>
    public double FrameWidthPercent { get; set; } = 100;

    public double FrameHeightPercent { get; set; } = 100;

    // ------------------------------------------------------------------ mouse area box

    /// <summary>Rounded box around the swipe (mouse) area, styled like a big key.</summary>
    public bool SwipeBoxEnabled { get; set; } = true;

    public string SwipeBoxFillColor { get; set; } = "#000000";

    public string SwipeBoxBorderColor { get; set; } = "#E6E6E6";

    public double SwipeBoxBorderWidth { get; set; } = 2;

    public double SwipeBoxCornerRadius { get; set; } = 12;

    /// <summary>Space between the box border and the swipe drawing.</summary>
    public double SwipeBoxPadding { get; set; } = 10;

    /// <summary>
    /// Box size in % of the space it has (centred). The swipe keeps its scale; it only shrinks when the
    /// box edge reaches it.
    /// </summary>
    public double SwipeBoxWidthPercent { get; set; } = 100;

    public double SwipeBoxHeightPercent { get; set; } = 100;

    // ------------------------------------------------------------------ background image + glass

    /// <summary>Picture behind everything (replaces the chroma key colour). Empty = none.</summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>Blur of the background picture (px).</summary>
    public double BackgroundImageBlur { get; set; } = 24;

    /// <summary>Darkens the background picture (%), so keys and swipe stand out.</summary>
    public double BackgroundImageDim { get; set; } = 20;

    /// <summary>Frosted-glass panels, mouse box and keys (see-through over the background picture).</summary>
    public bool GlassEnabled { get; set; }

    public string GlassTintColor { get; set; } = "#FFFFFF";

    /// <summary>Tint strength of the glass (%).</summary>
    public double GlassOpacity { get; set; } = 12;

    /// <summary>Extra blur behind the glass (px), on top of <see cref="BackgroundImageBlur"/>.</summary>
    public double GlassBlur { get; set; } = 16;

    /// <summary>Crossfade (ms) when the picture changes, e.g. a new Spotify cover. 0 = switch instantly.</summary>
    public double BackgroundFadeMs { get; set; } = DefaultBackgroundFadeMs;

    /// <summary>Take colours from the picture / Spotify cover (Spotify's API has none, so they are extracted locally).</summary>
    public bool CoverColorsEnabled { get; set; } = true;

    /// <summary>Accent colour for the mouse strokes (line, arrow, dot).</summary>
    public bool CoverAccentTrail { get; set; } = true;

    /// <summary>Accent colour for the key outlines.</summary>
    public bool CoverAccentKeyBorders { get; set; } = true;

    /// <summary>Accent colour as the fill of pressed keys (label picks black/white for contrast).</summary>
    public bool CoverAccentPressedKeys { get; set; }

    /// <summary>Accent colour for the frame and mouse area borders.</summary>
    public bool CoverAccentFrameBorders { get; set; }

    /// <summary>Dark key labels on glass over a light picture; accent darkened on light, brightened on dark pictures.</summary>
    public bool CoverAutoContrast { get; set; } = true;

    /// <summary>Borders on frame, mouse box and keys.</summary>
    public bool BordersEnabled { get; set; } = true;

    // ------------------------------------------------------------------ Spotify cover art

    /// <summary>Use the cover of what is playing on Spotify as the frame background (overrides the picture).</summary>
    public bool SpotifyCoverEnabled { get; set; }

    /// <summary>Client ID of the user's own Spotify app. The client secret is NOT stored here (DPAPI file).</summary>
    public string SpotifyClientId { get; set; } = string.Empty;

    /// <summary>How often to check what is playing (s).</summary>
    public double SpotifyPollSeconds { get; set; } = DefaultSpotifyPollSeconds;

    /// <summary>Port of the local OAuth redirect: http://127.0.0.1:PORT/callback.</summary>
    public int SpotifyRedirectPort { get; set; } = DefaultSpotifyPort;

    // ------------------------------------------------------------------ output

    /// <summary>Primary output. The camera is the default; the OBS window stays as a fallback.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OutputMode OutputMode { get; set; } = OutputMode.NativeVirtualCamera;

    /// <summary>
    /// Show the preview window at start-up. Purely visual: the camera output does not depend on it.
    /// Forced on in <see cref="OutputMode.ObsCaptureWindow"/> mode (OBS captures that window).
    /// </summary>
    public bool ShowPreview { get; set; } = true;

    // ------------------------------------------------------------------ capture canvas / OBS window

    /// <summary>Client-area width of the capture window in physical pixels = what OBS receives.</summary>
    public int CaptureWidth { get; set; } = DefaultCaptureSize;

    public int CaptureHeight { get; set; } = DefaultCaptureSize;

    /// <summary>Solid background removed by OBS's Chroma Key filter.</summary>
    public string ChromaKeyColor { get; set; } = DefaultChromaKeyColor;

    /// <summary>true = chroma-key background; false = black background (use OBS blending mode "Screen").</summary>
    public bool CaptureBackgroundEnabled { get; set; } = true;

    /// <summary>
    /// With the chroma background: no semi-transparent pixels touch the key colour (opaque colour fade
    /// plus aliased outer outline), so keying leaves no green halo. Only relevant when
    /// <see cref="CaptureBackgroundEnabled"/> is true.
    /// </summary>
    public bool ChromaSafeEdges { get; set; } = true;

    /// <summary>Development aid: draws the debug statistics into the capture canvas (and thus into clips).</summary>
    public bool IncludeDebugInCapture { get; set; }

    /// <summary>Open the settings window together with the capture window at start-up.</summary>
    public bool ShowSettingsOnStartup { get; set; } = true;

    /// <summary>Capture window position (physical pixels) and monitor.</summary>
    public WindowPlacementSettings? Window { get; set; }

    [JsonIgnore]
    public bool UsesChromaSafeRendering => CaptureBackgroundEnabled && ChromaSafeEdges;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Window = Window?.Clone();
        return copy;
    }

    /// <summary>Clamps every value into its valid range.</summary>
    /// <returns>Descriptions of the corrections made (empty when the settings were valid).</returns>
    public List<string> Sanitize()
    {
        var fixes = new List<string>();
        TrailLifetimeMs = Clamp(nameof(TrailLifetimeMs), TrailLifetimeMs, MinTrailLifetimeMs, MaxTrailLifetimeMs, DefaultTrailLifetimeMs, fixes);
        TrailThickness = Clamp(nameof(TrailThickness), TrailThickness, MinTrailThickness, MaxTrailThickness, DefaultTrailThickness, fixes);
        SensitivityScale = Clamp(nameof(SensitivityScale), SensitivityScale, MinSensitivity, MaxSensitivity, DefaultSensitivity, fixes);
        SmoothingStrength = Clamp(nameof(SmoothingStrength), SmoothingStrength, MinSmoothing, MaxSmoothing, DefaultSmoothing, fixes);
        SwipeBreakMs = Clamp(nameof(SwipeBreakMs), SwipeBreakMs, MinSwipeBreakMs, MaxSwipeBreakMs, DefaultSwipeBreakMs, fixes);
        LiftGapMs = Clamp(nameof(LiftGapMs), LiftGapMs, MinLiftGapMs, MaxLiftGapMs, DefaultLiftGapMs, fixes);
        RenderFps = ClampInt(nameof(RenderFps), RenderFps, MinRenderFps, MaxRenderFps, fixes);
        CaptureWidth = ClampInt(nameof(CaptureWidth), CaptureWidth, MinCaptureSize, MaxCaptureSize, fixes);
        CaptureHeight = ClampInt(nameof(CaptureHeight), CaptureHeight, MinCaptureSize, MaxCaptureSize, fixes);

        if (!Enum.IsDefined(OutputMode))
        {
            fixes.Add($"{nameof(OutputMode)} invalid");
            OutputMode = OutputMode.NativeVirtualCamera;
        }

        DotSize = Clamp(nameof(DotSize), DotSize, MinDotSize, MaxDotSize, DefaultDotSize, fixes);
        KeyboardSplitPercent = ClampInt(nameof(KeyboardSplitPercent), KeyboardSplitPercent, MinKeyboardSplit, MaxKeyboardSplit, fixes);
        FrameBorderWidth = Clamp(nameof(FrameBorderWidth), FrameBorderWidth, 0, MaxFrameBorder, 2, fixes);
        FrameCornerRadius = Clamp(nameof(FrameCornerRadius), FrameCornerRadius, 0, MaxFrameRadius, 16, fixes);
        FrameMargin = Clamp(nameof(FrameMargin), FrameMargin, 0, MaxFrameMargin, 6, fixes);
        FramePadding = Clamp(nameof(FramePadding), FramePadding, 0, MaxFramePadding, 14, fixes);
        SwipeBoxBorderWidth = Clamp(nameof(SwipeBoxBorderWidth), SwipeBoxBorderWidth, 0, MaxFrameBorder, 2, fixes);
        SwipeBoxCornerRadius = Clamp(nameof(SwipeBoxCornerRadius), SwipeBoxCornerRadius, 0, MaxFrameRadius, 12, fixes);
        SwipeBoxPadding = Clamp(nameof(SwipeBoxPadding), SwipeBoxPadding, 0, MaxFramePadding, 10, fixes);
        BackgroundImagePath = BackgroundImagePath?.Trim().Trim('"') ?? string.Empty;
        SpotifyClientId = SpotifyClientId?.Trim() ?? string.Empty;
        BackgroundFadeMs = Clamp(nameof(BackgroundFadeMs), BackgroundFadeMs, 0, MaxBackgroundFadeMs, DefaultBackgroundFadeMs, fixes);
        SpotifyPollSeconds = Clamp(nameof(SpotifyPollSeconds), SpotifyPollSeconds, MinSpotifyPollSeconds, MaxSpotifyPollSeconds, DefaultSpotifyPollSeconds, fixes);
        if (SpotifyRedirectPort is < 1024 or > 65535)
        {
            fixes.Add($"{nameof(SpotifyRedirectPort)} {SpotifyRedirectPort} → {DefaultSpotifyPort}");
            SpotifyRedirectPort = DefaultSpotifyPort;
        }

        BackgroundImageBlur = Clamp(nameof(BackgroundImageBlur), BackgroundImageBlur, 0, MaxImageBlur, 24, fixes);
        BackgroundImageDim = Clamp(nameof(BackgroundImageDim), BackgroundImageDim, 0, MaxImageDim, 20, fixes);
        GlassOpacity = Clamp(nameof(GlassOpacity), GlassOpacity, 0, MaxGlassOpacity, 12, fixes);
        GlassBlur = Clamp(nameof(GlassBlur), GlassBlur, 0, MaxGlassBlur, 16, fixes);
        GlassTintColor = ValidColor(nameof(GlassTintColor), GlassTintColor, "#FFFFFF", fixes);
        FrameWidthPercent = Clamp(nameof(FrameWidthPercent), FrameWidthPercent, MinSwipeBoxPercent, MaxSwipeBoxPercent, 100, fixes);
        FrameHeightPercent = Clamp(nameof(FrameHeightPercent), FrameHeightPercent, MinSwipeBoxPercent, MaxSwipeBoxPercent, 100, fixes);
        SwipeBoxWidthPercent = Clamp(nameof(SwipeBoxWidthPercent), SwipeBoxWidthPercent, MinSwipeBoxPercent, MaxSwipeBoxPercent, 100, fixes);
        SwipeBoxHeightPercent = Clamp(nameof(SwipeBoxHeightPercent), SwipeBoxHeightPercent, MinSwipeBoxPercent, MaxSwipeBoxPercent, 100, fixes);
        if (!Enum.IsDefined(HeadStyle)) HeadStyle = HeadStyle.Arrow;
        if (!Enum.IsDefined(KeyboardPosition)) KeyboardPosition = KeyboardPosition.Left;
        DotColor = ValidColor(nameof(DotColor), DotColor, DefaultTrailColor, fixes);
        OutlineColor = ValidColor(nameof(OutlineColor), OutlineColor, DefaultOutlineColor, fixes);
        KeyFillColor = ValidColor(nameof(KeyFillColor), KeyFillColor, "#000000", fixes);
        KeyBorderColor = ValidColor(nameof(KeyBorderColor), KeyBorderColor, "#E6E6E6", fixes);
        KeyLabelColor = ValidColor(nameof(KeyLabelColor), KeyLabelColor, "#FFFFFF", fixes);
        KeyPressedFillColor = ValidColor(nameof(KeyPressedFillColor), KeyPressedFillColor, "#FFFFFF", fixes);
        KeyPressedLabelColor = ValidColor(nameof(KeyPressedLabelColor), KeyPressedLabelColor, "#000000", fixes);
        FrameBackgroundColor = ValidColor(nameof(FrameBackgroundColor), FrameBackgroundColor, "#000000", fixes);
        FrameBorderColor = ValidColor(nameof(FrameBorderColor), FrameBorderColor, "#E6E6E6", fixes);
        SwipeBoxFillColor = ValidColor(nameof(SwipeBoxFillColor), SwipeBoxFillColor, "#000000", fixes);
        SwipeBoxBorderColor = ValidColor(nameof(SwipeBoxBorderColor), SwipeBoxBorderColor, "#E6E6E6", fixes);

        if (!TryParseColor(TrailColor, out _))
        {
            fixes.Add($"{nameof(TrailColor)} '{TrailColor}' invalid");
            TrailColor = DefaultTrailColor;
        }

        if (!TryParseColor(ChromaKeyColor, out _))
        {
            fixes.Add($"{nameof(ChromaKeyColor)} '{ChromaKeyColor}' invalid");
            ChromaKeyColor = DefaultChromaKeyColor;
        }

        if (Window != null && (Window.Width < 0 || Window.Height < 0 || Window.Width > 100_000 || Window.Height > 100_000))
        {
            fixes.Add("Window placement invalid");
            Window = null;
        }

        SchemaVersion = CurrentSchemaVersion;
        return fixes;
    }

    /// <summary>The solid colour the capture canvas is filled with.</summary>
    public Color GetCaptureBackgroundColor()
    {
        Color color = CaptureBackgroundEnabled
            ? ParseColorOrDefault(ChromaKeyColor, Colors.Lime)
            : ParseColorOrDefault(NoKeyBackgroundColor, Colors.Black);

        // The window is opaque; a translucent key colour would only blend with an undefined backdrop.
        color.A = 255;
        return color;
    }

    private static string ValidColor(string name, string? value, string fallback, List<string> fixes)
    {
        if (TryParseColor(value, out _))
        {
            return value!;
        }

        fixes.Add($"{name} '{value}' invalid");
        return fallback;
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(text.Trim()) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            return false;
        }

        return false;
    }

    public static Color ParseColorOrDefault(string? text, Color fallback) =>
        TryParseColor(text, out Color c) ? c : fallback;

    private static double Clamp(string name, double value, double min, double max, double fallback, List<string> fixes)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            fixes.Add($"{name} is not a number");
            return fallback;
        }

        if (value < min || value > max)
        {
            fixes.Add($"{name} {value.ToString(CultureInfo.InvariantCulture)} out of range [{min}, {max}]");
            return Math.Clamp(value, min, max);
        }

        return value;
    }

    private static int ClampInt(string name, int value, int min, int max, List<string> fixes)
    {
        if (value < min || value > max)
        {
            fixes.Add($"{name} {value} out of range [{min}, {max}]");
            return Math.Clamp(value, min, max);
        }

        return value;
    }
}
