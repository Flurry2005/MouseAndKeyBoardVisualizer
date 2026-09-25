using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Settings;

/// <summary>
/// Operations the settings UI needs from the application. Keeps the window independent of
/// <see cref="AppController"/>'s other responsibilities.
/// </summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }

    /// <summary>Applies the (already modified) <see cref="Settings"/> live and schedules a save.</summary>
    void OnSettingsEdited();


    void ResetSettingsToDefaults();

    void ClearTrail();

    void ShowPreview();

    /// <summary>Runs a camera action (install, remove, test) in the background; completes with a message.</summary>
    Task<string> RunCameraActionAsync(CameraAction action);

    /// <summary>One-line virtual camera state for the Output group.</summary>
    string CameraSummary();
}

public enum CameraAction
{
    Install,
    Remove,
    Test,
}

/// <summary>
/// Live-editing settings dialog: every change is applied immediately and saved shortly after.
/// Sliders and text boxes are paired; the text box accepts exact values (also outside the slider
/// range where the setting allows it, e.g. sensitivity up to 20x).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly List<Action> _refreshers = new();
    private bool _updating;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();

        BindLogSlider(SensitivitySlider, SensitivityBox,
            () => _host.Settings.SensitivityScale, v => _host.Settings.SensitivityScale = v,
            AppSettings.MinSensitivity, AppSettings.MaxSensitivity, "0.###");
        BindSlider(BreakSlider, BreakBox, () => _host.Settings.SwipeBreakMs, v => _host.Settings.SwipeBreakMs = v,
            AppSettings.MinSwipeBreakMs, AppSettings.MaxSwipeBreakMs, "0");
        BindSlider(SmoothingSlider, SmoothingBox, () => _host.Settings.SmoothingStrength, v => _host.Settings.SmoothingStrength = v,
            AppSettings.MinSmoothing, AppSettings.MaxSmoothing, "0.00");
        BindSlider(LiftGapSlider, LiftGapBox, () => _host.Settings.LiftGapMs, v => _host.Settings.LiftGapMs = v,
            AppSettings.MinLiftGapMs, AppSettings.MaxLiftGapMs, "0");
        BindSlider(LifetimeSlider, LifetimeBox, () => _host.Settings.TrailLifetimeMs, v => _host.Settings.TrailLifetimeMs = v,
            AppSettings.MinTrailLifetimeMs, AppSettings.MaxTrailLifetimeMs, "0");
        BindSlider(ThicknessSlider, ThicknessBox, () => _host.Settings.TrailThickness, v => _host.Settings.TrailThickness = v,
            AppSettings.MinTrailThickness, AppSettings.MaxTrailThickness, "0.#");
        BindSlider(FpsSlider, FpsBox, () => _host.Settings.RenderFps, v => _host.Settings.RenderFps = (int)Math.Round(v),
            AppSettings.MinRenderFps, AppSettings.MaxRenderFps, "0");

        BindCheck(LiftCheck, () => _host.Settings.LiftDetectionEnabled, v => _host.Settings.LiftDetectionEnabled = v);
        BindSlider(DotSizeSlider, DotSizeBox, () => _host.Settings.DotSize, v => _host.Settings.DotSize = v,
            AppSettings.MinDotSize, AppSettings.MaxDotSize, "0");
        BindColor(DotColorBox, () => _host.Settings.DotColor, v => _host.Settings.DotColor = v);
        BindColor(OutlineColorBox, () => _host.Settings.OutlineColor, v => _host.Settings.OutlineColor = v);
        BindEnum(HeadStyleCombo, new[] { "Arrow", "Dot", "None" }, () => (int)_host.Settings.HeadStyle, v => _host.Settings.HeadStyle = (HeadStyle)v);

        BindCheck(KeyboardCheck, () => _host.Settings.KeyboardEnabled, v => _host.Settings.KeyboardEnabled = v);
        BindEnum(KeyboardPositionCombo, new[] { "Left of the swipe", "Right of the swipe", "Above the swipe", "Below the swipe" },
            () => (int)_host.Settings.KeyboardPosition, v => _host.Settings.KeyboardPosition = (KeyboardPosition)v);
        BindSlider(KeyboardSplitSlider, KeyboardSplitBox, () => _host.Settings.KeyboardSplitPercent, v => _host.Settings.KeyboardSplitPercent = (int)Math.Round(v),
            AppSettings.MinKeyboardSplit, AppSettings.MaxKeyboardSplit, "0");
        BindColor(KeyFillBox, () => _host.Settings.KeyFillColor, v => _host.Settings.KeyFillColor = v);
        BindColor(KeyBorderBox, () => _host.Settings.KeyBorderColor, v => _host.Settings.KeyBorderColor = v);
        BindColor(KeyLabelBox, () => _host.Settings.KeyLabelColor, v => _host.Settings.KeyLabelColor = v);
        BindColor(KeyPressedFillBox, () => _host.Settings.KeyPressedFillColor, v => _host.Settings.KeyPressedFillColor = v);
        BindColor(KeyPressedLabelBox, () => _host.Settings.KeyPressedLabelColor, v => _host.Settings.KeyPressedLabelColor = v);

        BindCheck(FrameCheck, () => _host.Settings.FrameEnabled, v => _host.Settings.FrameEnabled = v);
        BindColor(FrameBackgroundBox, () => _host.Settings.FrameBackgroundColor, v => _host.Settings.FrameBackgroundColor = v);
        BindColor(FrameBorderBox, () => _host.Settings.FrameBorderColor, v => _host.Settings.FrameBorderColor = v);
        BindSlider(FrameBorderWidthSlider, FrameBorderWidthBox, () => _host.Settings.FrameBorderWidth, v => _host.Settings.FrameBorderWidth = v, 0, AppSettings.MaxFrameBorder, "0.#");
        BindSlider(FrameRadiusSlider, FrameRadiusBox, () => _host.Settings.FrameCornerRadius, v => _host.Settings.FrameCornerRadius = v, 0, AppSettings.MaxFrameRadius, "0");
        BindSlider(FrameMarginSlider, FrameMarginBox, () => _host.Settings.FrameMargin, v => _host.Settings.FrameMargin = v, 0, AppSettings.MaxFrameMargin, "0");
        BindSlider(FramePaddingSlider, FramePaddingBox, () => _host.Settings.FramePadding, v => _host.Settings.FramePadding = v, 0, AppSettings.MaxFramePadding, "0");

        BindSlider(FrameWidthSlider, FrameWidthBox, () => _host.Settings.FrameWidthPercent, v => _host.Settings.FrameWidthPercent = v,
            AppSettings.MinSwipeBoxPercent, AppSettings.MaxSwipeBoxPercent, "0");
        BindSlider(FrameHeightSlider, FrameHeightBox, () => _host.Settings.FrameHeightPercent, v => _host.Settings.FrameHeightPercent = v,
            AppSettings.MinSwipeBoxPercent, AppSettings.MaxSwipeBoxPercent, "0");

        BindBackgroundImage();
        BindSlider(BgBlurSlider, BgBlurBox, () => _host.Settings.BackgroundImageBlur, v => _host.Settings.BackgroundImageBlur = v, 0, AppSettings.MaxImageBlur, "0");
        BindSlider(BgDimSlider, BgDimBox, () => _host.Settings.BackgroundImageDim, v => _host.Settings.BackgroundImageDim = v, 0, AppSettings.MaxImageDim, "0");
        BindCheck(GlassCheck, () => _host.Settings.GlassEnabled, v => _host.Settings.GlassEnabled = v);
        BindColor(GlassTintBox, () => _host.Settings.GlassTintColor, v => _host.Settings.GlassTintColor = v);
        BindSlider(GlassOpacitySlider, GlassOpacityBox, () => _host.Settings.GlassOpacity, v => _host.Settings.GlassOpacity = v, 0, AppSettings.MaxGlassOpacity, "0");
        BindSlider(GlassBlurSlider, GlassBlurBox, () => _host.Settings.GlassBlur, v => _host.Settings.GlassBlur = v, 0, AppSettings.MaxGlassBlur, "0");
        BindCheck(BordersCheck, () => _host.Settings.BordersEnabled, v => _host.Settings.BordersEnabled = v);

        BindCheck(SwipeBoxCheck, () => _host.Settings.SwipeBoxEnabled, v => _host.Settings.SwipeBoxEnabled = v);
        BindColor(SwipeBoxFillBox, () => _host.Settings.SwipeBoxFillColor, v => _host.Settings.SwipeBoxFillColor = v);
        BindColor(SwipeBoxBorderBox, () => _host.Settings.SwipeBoxBorderColor, v => _host.Settings.SwipeBoxBorderColor = v);
        BindSlider(SwipeBoxBorderWidthSlider, SwipeBoxBorderWidthBox, () => _host.Settings.SwipeBoxBorderWidth, v => _host.Settings.SwipeBoxBorderWidth = v, 0, AppSettings.MaxFrameBorder, "0.#");
        BindSlider(SwipeBoxRadiusSlider, SwipeBoxRadiusBox, () => _host.Settings.SwipeBoxCornerRadius, v => _host.Settings.SwipeBoxCornerRadius = v, 0, AppSettings.MaxFrameRadius, "0");
        BindSlider(SwipeBoxPaddingSlider, SwipeBoxPaddingBox, () => _host.Settings.SwipeBoxPadding, v => _host.Settings.SwipeBoxPadding = v, 0, AppSettings.MaxFramePadding, "0");
        BindSlider(SwipeBoxWidthSlider, SwipeBoxWidthBox, () => _host.Settings.SwipeBoxWidthPercent, v => _host.Settings.SwipeBoxWidthPercent = v,
            AppSettings.MinSwipeBoxPercent, AppSettings.MaxSwipeBoxPercent, "0");
        BindSlider(SwipeBoxHeightSlider, SwipeBoxHeightBox, () => _host.Settings.SwipeBoxHeightPercent, v => _host.Settings.SwipeBoxHeightPercent = v,
            AppSettings.MinSwipeBoxPercent, AppSettings.MaxSwipeBoxPercent, "0");
        BindCheck(OutlineCheck, () => _host.Settings.OutlineEnabled, v => _host.Settings.OutlineEnabled = v);
        BindCheck(BackgroundCheck, () => _host.Settings.CaptureBackgroundEnabled, v => _host.Settings.CaptureBackgroundEnabled = v);
        BindCheck(ChromaSafeCheck, () => _host.Settings.ChromaSafeEdges, v => _host.Settings.ChromaSafeEdges = v);
        BindCheck(DebugInCaptureCheck, () => _host.Settings.IncludeDebugInCapture, v => _host.Settings.IncludeDebugInCapture = v);
        BindCheck(SettingsOnStartupCheck, () => _host.Settings.ShowSettingsOnStartup, v => _host.Settings.ShowSettingsOnStartup = v);

        BindColor(TrailColorBox, () => _host.Settings.TrailColor, v => _host.Settings.TrailColor = v);
        BindColor(ChromaColorBox, () => _host.Settings.ChromaKeyColor, v => _host.Settings.ChromaKeyColor = v);

        BindCaptureSize();
        BindCheck(ShowPreviewCheck, () => _host.Settings.ShowPreview, v => _host.Settings.ShowPreview = v);
        OutputModeCombo.Items.Add("Native Virtual Camera (Medal, camera apps)");
        OutputModeCombo.Items.Add("OBS Capture Window (fallback)");
        OutputModeCombo.SelectionChanged += (_, _) =>
        {
            if (!_updating && OutputModeCombo.SelectedIndex >= 0)
            {
                _host.Settings.OutputMode = (OutputMode)OutputModeCombo.SelectedIndex;
                _host.OnSettingsEdited();
            }
        };
        _refreshers.Add(() => OutputModeCombo.SelectedIndex = (int)_host.Settings.OutputMode);

        CameraInstallButton.Click += async (_, _) => await RunCameraAction(CameraAction.Install);
        CameraRemoveButton.Click += async (_, _) => await RunCameraAction(CameraAction.Remove);
        CameraTestButton.Click += async (_, _) => await RunCameraAction(CameraAction.Test);
        PreviewButton.Click += (_, _) => _host.ShowPreview();
        foreach (double preset in AppSettings.SensitivityPresets)
        {
            var button = new Button
            {
                Content = preset.ToString("0.##", CultureInfo.InvariantCulture) + "x",
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(6, 1, 6, 1),
            };
            button.Click += (_, _) =>
            {
                _host.Settings.SensitivityScale = preset;
                _host.OnSettingsEdited();
                RefreshAll();
            };
            SensitivityPresets.Children.Add(button);
        }

        ResetButton.Click += (_, _) =>
        {
            _host.ResetSettingsToDefaults();
            RefreshAll();
        };
        ClearButton.Click += (_, _) => _host.ClearTrail();
        OpenFolderButton.Click += (_, _) => OpenDataFolder();
        CloseButton.Click += (_, _) => Close();
        MaxHeight = SystemParameters.WorkArea.Height;

        PathText.Text = $"Settings: {AppPaths.SettingsFile}\nLog: {AppPaths.LogFile}";
        RefreshAll();
    }

    /// <summary>Live statistics and capture-window properties, pushed by the controller while open.</summary>
    public void SetDiagnostics(string text)
    {
        DiagnosticsText.Text = text;
        if (!_cameraBusy)
        {
            CameraSummaryText.Text = _host.CameraSummary();
        }
    }

    private bool _cameraBusy;

    private async Task RunCameraAction(CameraAction action)
    {
        _cameraBusy = true;
        CameraInstallButton.IsEnabled = CameraRemoveButton.IsEnabled = CameraTestButton.IsEnabled = false;
        CameraSummaryText.Text = action switch
        {
            CameraAction.Install => "Installing… confirm the Windows administrator prompt (UAC).",
            CameraAction.Remove => "Removing… confirm the Windows administrator prompt (UAC).",
            _ => "Opening the camera as a test consumer…",
        };
        try
        {
            string message = await _host.RunCameraActionAsync(action);
            MessageBox.Show(this, message, "Mouse Swipe Visualizer – camera", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _cameraBusy = false;
            CameraInstallButton.IsEnabled = CameraRemoveButton.IsEnabled = CameraTestButton.IsEnabled = true;
            CameraSummaryText.Text = _host.CameraSummary();
        }
    }

    private const string CustomSizeItem = "Custom";

    /// <summary>Preset combo (400/600/800/1080 square or Custom) plus exact width/height boxes.</summary>
    private void BindCaptureSize()
    {
        foreach (int size in AppSettings.CaptureSizePresets)
        {
            CaptureSizeCombo.Items.Add($"{size} × {size}");
        }

        CaptureSizeCombo.Items.Add(CustomSizeItem);
        CaptureSizeCombo.SelectionChanged += (_, _) =>
        {
            int index = CaptureSizeCombo.SelectedIndex;
            if (_updating || index < 0 || index >= AppSettings.CaptureSizePresets.Length)
            {
                return; // "Custom": edit the boxes
            }

            int size = AppSettings.CaptureSizePresets[index];
            _host.Settings.CaptureWidth = size;
            _host.Settings.CaptureHeight = size;
            _host.OnSettingsEdited();
            RefreshAll();
        };

        BindTextBox(CaptureWidthBox, v =>
        {
            _host.Settings.CaptureWidth = (int)Math.Round(Math.Clamp(v, AppSettings.MinCaptureSize, AppSettings.MaxCaptureSize));
            _host.OnSettingsEdited();
            RefreshAll();
        });
        BindTextBox(CaptureHeightBox, v =>
        {
            _host.Settings.CaptureHeight = (int)Math.Round(Math.Clamp(v, AppSettings.MinCaptureSize, AppSettings.MaxCaptureSize));
            _host.OnSettingsEdited();
            RefreshAll();
        });

        _refreshers.Add(() =>
        {
            int w = _host.Settings.CaptureWidth;
            int h = _host.Settings.CaptureHeight;
            CaptureWidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
            CaptureHeightBox.Text = h.ToString(CultureInfo.InvariantCulture);
            int preset = w == h ? Array.IndexOf(AppSettings.CaptureSizePresets, w) : -1;
            CaptureSizeCombo.SelectedIndex = preset >= 0 ? preset : AppSettings.CaptureSizePresets.Length;
            ChromaColorBox.IsEnabled = _host.Settings.CaptureBackgroundEnabled;
            ChromaSafeCheck.IsEnabled = _host.Settings.CaptureBackgroundEnabled;
        });
    }

    /// <summary>Re-reads every control from the settings (after reset/presets or external changes).</summary>
    public void RefreshAll()
    {
        _updating = true;
        try
        {
            foreach (Action refresh in _refreshers)
            {
                refresh();
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void BindSlider(Slider slider, TextBox box, Func<double> get, Action<double> set, double min, double max, string format)
    {
        slider.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            set(e.NewValue);
            box.Text = get().ToString(format, CultureInfo.InvariantCulture);
            _host.OnSettingsEdited();
        };

        BindTextBox(box, value =>
        {
            set(Math.Clamp(value, min, max));
            _host.OnSettingsEdited();
            RefreshAll();
        });

        _refreshers.Add(() =>
        {
            slider.Value = Math.Clamp(get(), slider.Minimum, slider.Maximum);
            box.Text = get().ToString(format, CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Slider works on log10(value) so 0.05x..20x is usable across the whole range.</summary>
    private void BindLogSlider(Slider slider, TextBox box, Func<double> get, Action<double> set, double min, double max, string format)
    {
        slider.Minimum = Math.Log10(min);
        slider.Maximum = Math.Log10(max);
        slider.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            set(Math.Round(Math.Pow(10, e.NewValue), 3));
            box.Text = get().ToString(format, CultureInfo.InvariantCulture);
            _host.OnSettingsEdited();
        };

        BindTextBox(box, value =>
        {
            set(Math.Clamp(value, min, max));
            _host.OnSettingsEdited();
            RefreshAll();
        });

        _refreshers.Add(() =>
        {
            slider.Value = Math.Log10(Math.Clamp(get(), min, max));
            box.Text = get().ToString(format, CultureInfo.InvariantCulture);
        });
    }

    private void BindTextBox(TextBox box, Action<double> commit)
    {
        void TryCommit()
        {
            if (_updating)
            {
                return;
            }

            // Accept both "1.5" and the Swedish "1,5".
            string text = box.Text.Trim().Replace(',', '.').TrimEnd('x', 'X');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                commit(value);
            }
            else
            {
                RefreshAll(); // restore the current value
            }
        }

        box.LostKeyboardFocus += (_, _) => TryCommit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TryCommit();
                e.Handled = true;
            }
        };
    }

    private void BindEnum(ComboBox combo, string[] names, Func<int> get, Action<int> set)
    {
        foreach (string name in names)
        {
            combo.Items.Add(name);
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (!_updating && combo.SelectedIndex >= 0)
            {
                set(combo.SelectedIndex);
                _host.OnSettingsEdited();
            }
        };
        _refreshers.Add(() => combo.SelectedIndex = get());
    }

    private void BindCheck(CheckBox check, Func<bool> get, Action<bool> set)
    {
        check.Click += (_, _) =>
        {
            set(check.IsChecked == true);
            _host.OnSettingsEdited();
            RefreshAll();
        };
        _refreshers.Add(() => check.IsChecked = get());
    }

    private void BindBackgroundImage()
    {
        void Commit()
        {
            if (_updating)
            {
                return;
            }

            string path = BgImageBox.Text.Trim().Trim('"');
            BgImageBox.Text = path;
            if (path != _host.Settings.BackgroundImagePath)
            {
                _host.Settings.BackgroundImagePath = path;
                _host.OnSettingsEdited();
            }
        }

        BgImageBox.LostKeyboardFocus += (_, _) => Commit();
        BgImageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
            }
        };
        BgImageBrowseButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Background image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            };
            if (dialog.ShowDialog(this) == true)
            {
                BgImageBox.Text = dialog.FileName;
                Commit();
            }
        };
        BgImageClearButton.Click += (_, _) =>
        {
            BgImageBox.Text = string.Empty;
            Commit();
        };
        _refreshers.Add(() => BgImageBox.Text = _host.Settings.BackgroundImagePath);
    }

    private void BindColor(TextBox box, Func<string> get, Action<string> set)
    {
        void Commit()
        {
            if (_updating)
            {
                return;
            }

            if (AppSettings.TryParseColor(box.Text, out _))
            {
                set(box.Text.Trim());
                _host.OnSettingsEdited();
            }
            else
            {
                box.Text = get();
            }
        }

        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        _refreshers.Add(() => box.Text = get());
    }

    private static void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Logger.Warn("Could not open the data folder.", ex);
        }
    }
}
