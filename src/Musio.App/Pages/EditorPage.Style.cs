using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Media;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Projects;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Musio_App.Controls;
using Musio_App.Helpers;
using Musio_App.Services;
using Musio_App.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Musio_App.Pages;

public sealed partial class EditorPage
{
    private bool _suppressStyleEvents;
    private List<string>? _wallpaperPaths;
    private bool _suppressCursorEvents;

    // Webcam overlay drag state
    private bool _webcamDragging;
    private Windows.Foundation.Point _webcamDragStart;
    private float _webcamNormX;
    private float _webcamNormY;
    private float _webcamNormSize;
    private float _webcamDragStartNormX;
    private float _webcamDragStartNormY;

    // ─── Background Style Editing ───────────────────────────────────────

    // ─── Cursor Style Editing ───────────────────────────────────────────

    private void InitializeCursorControls(Project project, CompositionConfig config)
    {
        bool hasCursor = !string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath);
        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Cursor, hasCursor);

        if (!hasCursor) return;

        SyncCursorControlsToConfig(config.Cursor);
    }

    private void SyncCursorControlsToConfig(CursorStyle cursor)
    {
        using var _ = SuppressScope.Enter(ref _suppressCursorEvents);

        // Cursor type
        CursorTypeMouse.IsChecked = cursor.Type != CursorType.Touch;
        CursorTypeTouch.IsChecked = cursor.Type == CursorType.Touch;

        // Size
        CursorSizeSlider.Value = cursor.Scale;

        // Tilt
        CursorTiltToggle.IsOn = cursor.TiltEnabled;
        CursorTiltToggle.Visibility = cursor.Type != CursorType.Touch
            ? Visibility.Visible : Visibility.Collapsed;

        // Color — find matching radio button by Tag
        string cursorColor = (cursor.Color ?? "#FFFFFF").ToUpperInvariant();
        bool found = false;
        foreach (var child in CursorColorPanel.Children)
        {
            if (child is RadioButton rb && rb.Tag is string tag)
            {
                bool match = string.Equals(tag, cursorColor, StringComparison.OrdinalIgnoreCase);
                rb.IsChecked = match;
                if (match) found = true;
            }
        }
        if (!found && CursorColorPanel.Children.FirstOrDefault() is RadioButton first)
            first.IsChecked = true;
    }

    private void CursorType_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        // Show/hide tilt toggle based on cursor type
        bool isMouse = CursorTypeMouse.IsChecked == true;
        CursorTiltToggle.Visibility = isMouse ? Visibility.Visible : Visibility.Collapsed;
        ApplyCursorStyleFromControls();
    }

    private void CursorSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ScheduleCursorUpdate();
    }

    private void CursorTiltToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ApplyCursorStyleFromControls();
    }

    private void CursorColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ApplyCursorStyleFromControls();
    }

    private void ScheduleCursorUpdate()
    {
        _cursorDebouncer ??= new Debouncer(ApplyCursorStyleFromControls);
        _cursorDebouncer.Schedule();
    }

    private void ApplyCursorStyleFromControls()
    {
        var config = ProjectService.Instance.CurrentComposition;
        if (config is null) return;

        var cursorType = CursorTypeTouch.IsChecked == true ? CursorType.Touch : CursorType.Default;

        string color = "#FFFFFF";
        foreach (var child in CursorColorPanel.Children)
        {
            if (child is RadioButton rb && rb.IsChecked == true && rb.Tag is string tag)
            {
                color = tag;
                break;
            }
        }

        var newCursor = config.Cursor with
        {
            Type = cursorType,
            Scale = (float)CursorSizeSlider.Value,
            Color = color,
            TiltEnabled = CursorTiltToggle.IsOn,
        };

        // Cursor style is per-segment: store on the selected segment when one is
        // selected, otherwise update the global config.
        if (SelectedVideoSegment is { } seg)
        {
            seg.CursorStyleOverride = newCursor;
            _ = RebuildForSegmentStyleChangeAsync(seg);
            return;
        }

        config = config with { Cursor = newCursor };
        ProjectService.Instance.CurrentComposition = config;
        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    // ─── Background Style Editing (continued) ───────────────────────────

    private void InitializeStyleControls(Project project, CompositionConfig config)
    {
        // Style panel is available for all capture types. Monitor (full-screen)
        // captures start with zeroed defaults (see ProjectService.SetProject) but
        // users can still customize padding, corner radius, shadow, border, etc.
        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Scene, true);

        // Populate preset combo with built-in presets — each item is a small
        // swatch + label so users can identify gradients at a glance.
        PresetCombo.Items.Clear();
        PresetCombo.Items.Add(BuildPresetItem(new BrandPreset { Name = "(Custom)" }, isCustom: true));
        foreach (var preset in DefaultBrandPresets.All)
            PresetCombo.Items.Add(BuildPresetItem(preset, isCustom: false));

        // Load system wallpapers (async). Pass the project's currently-selected
        // background image so a custom path from a reopened project is merged
        // into the grid and selection survives the async load.
        _ = LoadSystemWallpapersAsync(config.Background.BackgroundImagePath);

        // Sync controls to current config, suppressing change events
        SyncStyleControlsToConfig(config.Background);
    }

    private async Task LoadSystemWallpapersAsync(string? initialCustomPath = null)
    {
        var wallpaperDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");

        // Enumerate and sort files on a background thread to avoid freezing the UI
        var systemPaths = await Task.Run(() =>
        {
            if (!Directory.Exists(wallpaperDir))
                return new List<string>();

            return Directory.GetFiles(wallpaperDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => new FileInfo(f).Length)
                .ToList();
        });

        // Preserve any custom (non-system) paths the user already picked while
        // the system load was in flight — otherwise we'd silently drop them.
        var existingCustom = (_wallpaperPaths ?? new List<string>())
            .Where(p => !p.StartsWith(wallpaperDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrEmpty(initialCustomPath)
            && !initialCustomPath.StartsWith(wallpaperDir, StringComparison.OrdinalIgnoreCase)
            && !existingCustom.Any(p => string.Equals(p, initialCustomPath, StringComparison.OrdinalIgnoreCase)))
        {
            existingCustom.Insert(0, initialCustomPath);
        }

        _wallpaperPaths = existingCustom.Concat(systemPaths).ToList();

        WallpaperGrid.Items.Clear();
        WallpaperGrid.Items.Add(BuildAddWallpaperTile());
        foreach (var path in _wallpaperPaths)
        {
            WallpaperGrid.Items.Add(BuildWallpaperTile(path));
        }

        // After the async load completes the synchronous SyncStyleControlsToConfig
        // call ran before the grid was populated; re-apply selection now that
        // the items exist so the user sees the active wallpaper highlighted.
        // Prefer the project's current background image (it may have changed
        // since the load started — e.g. the user picked a wallpaper while the
        // system enumeration was still running) and fall back to the initial
        // path passed in.
        var currentImagePath = ProjectService.Instance.CurrentComposition?.Background.BackgroundImagePath;
        var targetPath = !string.IsNullOrEmpty(currentImagePath) ? currentImagePath : initialCustomPath;
        if (!string.IsNullOrEmpty(targetPath))
        {
            int idx = _wallpaperPaths.FindIndex(p =>
                string.Equals(p, targetPath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                using var _ = SuppressScope.Enter(ref _suppressStyleEvents);
                WallpaperGrid.SelectedIndex = idx + 1;
            }
        }
    }

    // Sentinel tag used to identify the "+" tile in the wallpaper grid.
    private const string AddWallpaperTileTag = "__add_wallpaper__";

    // Wallpaper tiles are laid out in a fixed number of columns that stretch to fill the
    // pane, rather than at a fixed pixel size that leaves a ragged gap on the right.
    private const int WallpaperColumns = 3;
    private const double WallpaperTileAspect = 2.0 / 3.0; // height / width

    /// <summary>
    /// Divides the grid's width into <see cref="WallpaperColumns"/> equal columns so the
    /// tiles always span the full pane, re-running whenever the pane is resized.
    /// </summary>
    private void WallpaperGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WallpaperGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        double available = e.NewSize.Width;
        if (available <= 0) return;

        double itemWidth = Math.Floor(available / WallpaperColumns);
        if (itemWidth <= 0) return;

        panel.ItemWidth = itemWidth;
        panel.ItemHeight = Math.Round(itemWidth * WallpaperTileAspect);
    }

    private static Border BuildAddWallpaperTile()
    {
        var plus = new FontIcon
        {
            Glyph = "\uE710", // Add
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            Child = plus,
            Tag = AddWallpaperTileTag,
        };
    }

    private static Border BuildWallpaperTile(string path)
    {
        var img = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path))
            {
                DecodePixelHeight = 96, // small thumbnails for perf
            },
        };
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Child = img,
        };
    }

    private void SyncStyleControlsToConfig(BackgroundStyle bg)
    {
        using var _ = SuppressScope.Enter(ref _suppressStyleEvents);
        {
            // Background type combo
            int typeIndex = bg.Type switch
            {
                BackgroundType.Gradient => 1,
                BackgroundType.Image => 2,
                BackgroundType.Blur => 3,
                _ => 0, // SolidColor
            };
            BgTypeCombo.SelectedIndex = typeIndex;

            // Primary color
            var primaryColor = ParseHexColor(bg.Color);
            BgColorPicker.Color = primaryColor;
            BgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(primaryColor);
            BgColorText.Text = bg.Color;

            // Panel visibility based on type
            bool isGradient = bg.Type == BackgroundType.Gradient;
            bool isImage = bg.Type == BackgroundType.Image;
            GradientPanel.Visibility = isGradient ? Visibility.Visible : Visibility.Collapsed;
            WallpaperPanel.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
            ColorPanel.Visibility = bg.Type is not BackgroundType.Blur and not BackgroundType.Image
                ? Visibility.Visible : Visibility.Collapsed;

            if (isGradient)
            {
                var endColor = ParseHexColor(bg.GradientEndColor);
                GradEndColorPicker.Color = endColor;
                GradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(endColor);
                GradEndColorText.Text = bg.GradientEndColor;
                GradAngleSlider.Value = bg.GradientAngle;
            }

            if (isImage && _wallpaperPaths is not null && !string.IsNullOrEmpty(bg.BackgroundImagePath))
            {
                int wpIdx = _wallpaperPaths.FindIndex(p =>
                    string.Equals(p, bg.BackgroundImagePath, StringComparison.OrdinalIgnoreCase));
                // +1 because index 0 in the grid is the "+" add-tile.
                WallpaperGrid.SelectedIndex = wpIdx >= 0 ? wpIdx + 1 : -1;
            }

            // Sliders
            PaddingSlider.Value = bg.Padding;
            CornerRadiusSlider.Value = bg.CornerRadius;

            // Toggles
            ShadowToggle.IsOn = bg.ShadowEnabled;
            BorderToggle.IsOn = bg.BorderEnabled;

            // Motion blur / camera drift live on CompositionConfig, not on BackgroundStyle,
            // so they're read straight from the current composition (falling back to record
            // defaults) rather than from the `bg` parameter passed in here.
            var motionConfig = ProjectService.Instance.CurrentComposition;
            var motionBlur = motionConfig?.MotionBlur ?? new MotionBlurSettings();
            var cameraDrift = motionConfig?.CameraDrift ?? new CameraDriftSettings();
            MotionBlurToggle.IsOn = motionBlur.Enabled;
            MotionBlurSlider.Value = motionBlur.Strength * 100.0;
            MotionBlurSlider.IsEnabled = motionBlur.Enabled;
            CameraDriftToggle.IsOn = cameraDrift.Enabled;
            CameraDriftSlider.Value = cameraDrift.Strength * 50.0;
            CameraDriftSlider.IsEnabled = cameraDrift.Enabled;

            // Select matching preset or (Custom)
            PresetCombo.SelectedIndex = FindMatchingPresetIndex(bg);
        }
    }

    private static ComboBoxItem BuildPresetItem(BrandPreset preset, bool isCustom)
    {
        var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 32,
            Height = 18,
            RadiusX = 4,
            RadiusY = 4,
            Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (isCustom)
        {
            swatch.Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];
        }
        else
        {
            swatch.Fill = BuildPresetSwatchBrush(preset);
        }

        var label = new TextBlock
        {
            Text = preset.Name,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        panel.Children.Add(swatch);
        panel.Children.Add(label);

        return new ComboBoxItem
        {
            Content = panel,
            Tag = preset,
        };
    }

    private static Microsoft.UI.Xaml.Media.Brush BuildPresetSwatchBrush(BrandPreset preset)
    {
        var start = ParseHexColor(preset.BackgroundColor);

        if (preset.BackgroundType != BackgroundType.Gradient
            || string.IsNullOrEmpty(preset.GradientEndColor))
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(start);
        }

        var end = ParseHexColor(preset.GradientEndColor);

        // Convert angle (degrees, 0° = →, 90° = ↓) to start/end points on the
        // unit square so the swatch preview matches the rendered background.
        var (sp, ep) = AngleToGradientEndpoints(preset.GradientAngle);

        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = sp,
            EndPoint = ep,
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = start, Offset = 0 });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = end, Offset = 1 });
        return brush;
    }

    private static (Point Start, Point End) AngleToGradientEndpoints(double angleDegrees)
    {
        // Normalize to [0, 360).
        double a = angleDegrees % 360.0;
        if (a < 0) a += 360.0;
        double rad = a * Math.PI / 180.0;

        // Direction vector for the gradient line.
        double dx = Math.Cos(rad);
        double dy = Math.Sin(rad);

        // Project from center (0.5, 0.5) to the box edge along ±direction.
        // Scale so the longer axis fills the unit square diagonally.
        double scale = 0.5 / Math.Max(Math.Abs(dx), Math.Abs(dy));
        double hx = dx * scale;
        double hy = dy * scale;

        return (new Point(0.5 - hx, 0.5 - hy), new Point(0.5 + hx, 0.5 + hy));
    }

    private int FindMatchingPresetIndex(BackgroundStyle bg)
    {
        var presets = DefaultBrandPresets.All;
        for (int i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            if (p.BackgroundType == bg.Type &&
                string.Equals(p.BackgroundColor, bg.Color, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeHex(p.GradientEndColor), NormalizeHex(bg.GradientEndColor), StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(p.GradientAngle - bg.GradientAngle) < 0.5 &&
                p.Padding == bg.Padding &&
                p.CornerRadius == bg.CornerRadius &&
                p.ShadowEnabled == bg.ShadowEnabled &&
                p.ShadowBlur == bg.ShadowBlur &&
                Math.Abs(p.ShadowOpacity - bg.ShadowOpacity) < 0.001 &&
                string.Equals(NormalizeHex(p.ShadowColor), NormalizeHex(bg.ShadowColor), StringComparison.OrdinalIgnoreCase) &&
                p.BorderEnabled == bg.BorderEnabled &&
                p.BorderWidth == bg.BorderWidth &&
                string.Equals(NormalizeHex(p.BorderColor), NormalizeHex(bg.BorderColor), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.BackgroundImagePath ?? string.Empty, bg.BackgroundImagePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1; // +1 for the "(Custom)" entry at index 0
            }
        }
        return 0; // Custom
    }

    private static string NormalizeHex(string? hex) => string.IsNullOrEmpty(hex) ? string.Empty : hex.Trim();

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        if (PresetCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not BrandPreset preset) return;
        if (preset.Name == "(Custom)") return;

        var bg = BrandPresetConverter.ToBackgroundStyle(preset);
        SyncStyleControlsToConfig(bg);
        ApplyBackgroundStyle(bg);
    }

    private void BgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;

        var selectedType = BgTypeCombo.SelectedIndex switch
        {
            1 => BackgroundType.Gradient,
            2 => BackgroundType.Image,
            3 => BackgroundType.Blur,
            _ => BackgroundType.SolidColor,
        };

        GradientPanel.Visibility = selectedType == BackgroundType.Gradient
            ? Visibility.Visible : Visibility.Collapsed;
        WallpaperPanel.Visibility = selectedType == BackgroundType.Image
            ? Visibility.Visible : Visibility.Collapsed;
        ColorPanel.Visibility = selectedType is not BackgroundType.Blur and not BackgroundType.Image
            ? Visibility.Visible : Visibility.Collapsed;

        ScheduleStyleUpdate();
    }

    private void BgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressStyleEvents) return;
        var hex = ColorToHex(args.NewColor);
        BgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        BgColorText.Text = hex;
        ScheduleStyleUpdate();
    }

    private void GradEndColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressStyleEvents) return;
        var hex = ColorToHex(args.NewColor);
        GradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        GradEndColorText.Text = hex;
        ScheduleStyleUpdate();
    }

    private void StyleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        ScheduleStyleUpdate();
    }

    private void StyleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        ScheduleStyleUpdate();
    }

    // Motion (motion blur / camera drift) — deliberately separate from StyleToggle_Toggled
    // / StyleSlider_ValueChanged: those funnel into ApplyBackgroundStyle, which routes to a
    // selected video segment's FrameStyleOverride. Motion settings are global-only (like
    // ZoomScope), so they must never take that per-segment path.
    private void MotionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStyleEvents) return;

        // Keep the paired slider's enabled state in sync with its toggle immediately.
        if (ReferenceEquals(sender, MotionBlurToggle))
            MotionBlurSlider.IsEnabled = MotionBlurToggle.IsOn;
        else if (ReferenceEquals(sender, CameraDriftToggle))
            CameraDriftSlider.IsEnabled = CameraDriftToggle.IsOn;

        CommitMotionSettings();
        ScheduleMotionPreviewRebuild();
    }

    private void MotionSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        CommitMotionSettings();
        ScheduleMotionPreviewRebuild();
    }

    /// <summary>
    /// Writes the motion controls straight into the live composition. This is
    /// deliberately NOT debounced: the model must be current the instant the user
    /// lets go of a control, because saving, exporting, and re-syncing the pane on
    /// segment selection all read <see cref="ProjectService.CurrentComposition"/>
    /// directly. Deferring the write means a save or export landing inside the
    /// debounce window silently uses the previous settings, and a segment selection
    /// re-syncs the controls from the stale model and visually reverts the edit.
    /// Only the expensive part — rebuilding the preview renderer — is debounced.
    /// </summary>
    private void CommitMotionSettings()
    {
        var config = ProjectService.Instance.CurrentComposition;
        if (config is null) return;

        // Motion blur / camera drift are global (CompositionConfig-only) settings, mirroring
        // ZoomScope/AspectRatio/FitMode/CropAnchor — there is no per-project field to mirror
        // them onto. Rebuild `with` the existing sub-record so tuned values (shutter angle,
        // per-channel strengths, sample caps, etc.) survive; a `new` record would reset
        // them to their Musio.Core defaults.
        ProjectService.Instance.CurrentComposition = config with
        {
            MotionBlur = config.MotionBlur with
            {
                Enabled = MotionBlurToggle.IsOn,
                Strength = (float)(MotionBlurSlider.Value / 100.0),
            },
            CameraDrift = config.CameraDrift with
            {
                Enabled = CameraDriftToggle.IsOn,
                Strength = (float)(CameraDriftSlider.Value / 50.0),
            },
        };
    }

    private void ScheduleMotionPreviewRebuild()
    {
        // Separate timer/instance from _styleDebouncer so a motion slider drag never
        // races or gets coalesced with an in-flight background-style update.
        _motionDebouncer ??= new Debouncer(RefreshPreviewForMotionSettings);
        _motionDebouncer.Schedule();
    }

    private void RefreshPreviewForMotionSettings()
    {
        // Re-read rather than capturing at schedule time, so this always rebuilds from
        // the newest committed settings even if several edits coalesced into one tick.
        var config = ProjectService.Instance.CurrentComposition;
        if (config is null) return;

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void WallpaperGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;

        // If user selected the "+" add tile, open a file picker.
        if (WallpaperGrid.SelectedItem is Border b && (b.Tag as string) == AddWallpaperTileTag)
        {
            int previousIndex = -1;
            if (e.RemovedItems.Count > 0)
            {
                previousIndex = WallpaperGrid.Items.IndexOf(e.RemovedItems[0]);
            }
            _ = PickCustomWallpaperAsync(previousIndex);
            return;
        }

        ScheduleStyleUpdate();
    }

    private async Task PickCustomWallpaperAsync(int previousIndex)
    {
        Windows.Storage.StorageFile? file = null;
        try
        {
            file = await PickerHelper.PickSingleFileAsync(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                new[] { ".jpg", ".jpeg", ".png", ".bmp" },
                Windows.Storage.Pickers.PickerViewMode.Thumbnail);
        }
        catch
        {
            // Picker can throw on cancellation in some scenarios — treat as no-op.
        }

        using (SuppressScope.Enter(ref _suppressStyleEvents))
        {
            if (file is null)
            {
                // User cancelled — restore previous selection (or clear).
                WallpaperGrid.SelectedIndex = previousIndex >= 1 ? previousIndex : -1;
                return;
            }

            string path = file.Path;
            _wallpaperPaths ??= new List<string>();

            int existing = _wallpaperPaths.FindIndex(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                // Insert at the top of the list (right after the "+" tile).
                _wallpaperPaths.Insert(0, path);
                WallpaperGrid.Items.Insert(1, BuildWallpaperTile(path));
                WallpaperGrid.SelectedIndex = 1;
            }
            else
            {
                WallpaperGrid.SelectedIndex = existing + 1;
            }
        }

        ScheduleStyleUpdate();
    }

    private void ScheduleStyleUpdate()
    {
        // Debounce rapid changes (e.g. slider drags) to avoid thrashing the renderer
        _styleDebouncer ??= new Debouncer(() =>
        {
            var bg = BuildBackgroundStyleFromControls();
            ApplyBackgroundStyle(bg);
        });
        _styleDebouncer.Schedule();
    }

    private BackgroundStyle BuildBackgroundStyleFromControls()
    {
        var bgType = BgTypeCombo.SelectedIndex switch
        {
            1 => BackgroundType.Gradient,
            2 => BackgroundType.Image,
            3 => BackgroundType.Blur,
            _ => BackgroundType.SolidColor,
        };

        string? imagePath = null;
        if (bgType == BackgroundType.Image)
        {
            // -1 because index 0 in the grid is the "+" add-tile.
            int idx = WallpaperGrid.SelectedIndex - 1;
            if (_wallpaperPaths is not null && idx >= 0 && idx < _wallpaperPaths.Count)
            {
                imagePath = _wallpaperPaths[idx];
            }
            else
            {
                // Wallpaper list hasn't finished loading yet (or selection was
                // cleared) — preserve the project's currently-applied image so a
                // background sync from another control doesn't blank it out.
                imagePath = ProjectService.Instance.CurrentComposition?.Background.BackgroundImagePath;
            }
        }

        return new BackgroundStyle
        {
            Type = bgType,
            Color = BgColorText.Text,
            GradientEndColor = GradEndColorText.Text,
            GradientAngle = GradAngleSlider.Value,
            BackgroundImagePath = imagePath,
            Padding = (int)PaddingSlider.Value,
            CornerRadius = (int)CornerRadiusSlider.Value,
            ShadowEnabled = ShadowToggle.IsOn,
            ShadowBlur = 24,
            ShadowOpacity = 0.5,
            ShadowColor = "#000000",
            BorderEnabled = BorderToggle.IsOn,
            BorderWidth = 1,
            BorderColor = "#333333",
        };

    }

    /// <summary>
    /// Builds the effective composition config for a specific video segment by
    /// layering its per-segment style overrides (frame style + cursor) on top of the
    /// global config. Global properties (aspect ratio, fit/cover mode, crop anchor,
    /// zoom scope) always come from the global config so they apply to every segment.
    /// </summary>
    private static CompositionConfig BuildSegmentConfig(CompositionConfig global, VideoSegment? seg, int previewFps)
    {
        var cfg = global with { OutputFps = previewFps };
        if (seg?.FrameStyleOverride is { } bg) cfg = cfg with { Background = bg };
        if (seg?.CursorStyleOverride is { } cur) cfg = cfg with { Cursor = cur };
        return cfg;
    }

    /// <summary>
    /// Hides the cursor when a source has no recorded cursor samples (an imported video, or a
    /// recording whose cursor log is missing). The compositor synthesizes a static centre
    /// position for such a source so it can still zoom and apply background styling, but drawing
    /// a cursor at that invented point would be pure fiction — a negative auto-hide delay keeps
    /// it hidden from the very first frame. Mirrors
    /// <c>SegmentFrameComposer.ApplyCursorAvailability</c> so preview and export agree.
    /// </summary>
    private static CompositionConfig HideCursorWhenNoSamples(CompositionConfig config, MouseRecordingData mouseData)
    {
        if (mouseData.Samples is { Count: > 0 })
            return config;

        return config with
        {
            Cursor = config.Cursor with
            {
                AutoHideEnabled = true,
                AutoHideDelaySeconds = -1f,
                AutoHideFadeDuration = 0f,
            },
        };
    }

    /// <summary>The currently selected primary-track video segment, if any.</summary>
    private VideoSegment? SelectedVideoSegment =>
        _selectedPrimarySegmentId is { } id
            ? ViewModel.Model.Segments.OfType<VideoSegment>().FirstOrDefault(v => v.Id == id)
            : null;

    /// <summary>
    /// Returns the primary-file video segment under the playhead (for choosing the
    /// per-segment frame style/cursor the primary renderer should use), or the first
    /// primary segment as a fallback.
    /// </summary>
    private VideoSegment? ActivePrimaryVideoSegment()
    {
        var model = ViewModel.Model;
        if (model.Segments.Count == 0) return null;
        var primary = PrimaryVideoPath;

        bool IsPrimary(VideoSegment v) =>
            primary is null || string.Equals(v.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase);

        var (seg, _) = model.GetSegmentAtTime(Timeline.PlayheadPosition);
        if (seg is VideoSegment under && IsPrimary(under)) return under;
        return model.Segments.OfType<VideoSegment>().FirstOrDefault(IsPrimary);
    }

    /// <summary>Disposes and clears cached appended-segment preview contexts so they
    /// rebuild with fresh (global or per-segment) config on next render.</summary>
    private void InvalidateSegmentPreviews()
    {
        DisposeSegmentPreviews();
        _lastRenderedSegmentId = null;
    }

    /// <summary>
    /// Ensures the primary renderer was built with the given primary-file segment's
    /// per-segment frame style / cursor override. Rebuilds it when the override
    /// differs (e.g. the playhead crossed into a differently-styled primary split).
    /// No-op for appended segments (they use their own renderers).
    /// </summary>
    private async Task EnsurePrimaryRendererForSegmentAsync(VideoSegment seg)
    {
        var primary = PrimaryVideoPath;
        bool isPrimary = primary is null ||
            string.Equals(seg.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase);
        if (!isPrimary) return;

        var global = ProjectService.Instance.CurrentComposition;
        var wantBg = seg.FrameStyleOverride ?? global.Background;
        var wantCursor = seg.CursorStyleOverride ?? global.Cursor;

        if (Equals(wantBg, _primaryRenderBackground) && Equals(wantCursor, _primaryRenderCursor))
            return;

        // Rebuild FOR THIS SEGMENT. Letting the rebuild re-derive the style from the
        // playhead instead would not converge: whenever the playhead sits on a
        // different segment than the one being rendered (a text slide, a slide↔video
        // crossfade, or simply playback having advanced), the rebuild records segment
        // A's style while this guard keeps testing segment B's, so every frame rebuilt
        // the compositor again — reloading the cursor recording from disk and
        // reallocating render targets on the UI thread until the app froze.
        await RebuildPreviewRendererAsync(global, seg);
    }

    private void ApplyBackgroundStyle(BackgroundStyle bg)
    {
        // Mark preset as Custom if it no longer matches
        _suppressStyleEvents = true;
        PresetCombo.SelectedIndex = FindMatchingPresetIndex(bg);
        _suppressStyleEvents = false;

        // Frame style is per-segment: when a video segment is selected, store the
        // override on it and rebuild only that segment's renderer. Otherwise update
        // the global config so segments without an override follow it.
        if (SelectedVideoSegment is { } seg)
        {
            seg.FrameStyleOverride = bg;
            _ = RebuildForSegmentStyleChangeAsync(seg);
            return;
        }

        var config = ProjectService.Instance.CurrentComposition with { Background = bg };
        ProjectService.Instance.CurrentComposition = config;
        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    /// <summary>
    /// Rebuilds the preview after a per-segment style override change. If the edited
    /// segment is an appended recording, its cached preview is dropped so it rebuilds
    /// with the new override; if it is the primary recording (and currently active),
    /// the primary renderer is rebuilt.
    /// </summary>
    private async Task RebuildForSegmentStyleChangeAsync(VideoSegment seg)
    {
        // A per-segment override is written straight onto the segment — it goes through
        // neither UndoRedoManager nor CurrentComposition, so this is the only place the
        // unsaved-changes flag can learn about it. The override is persisted in the package,
        // so missing it would lose a real edit on close.
        ProjectService.Instance.MarkDirty();

        var primary = PrimaryVideoPath;
        bool isPrimary = primary is null ||
            string.Equals(seg.VideoFilePath, primary, StringComparison.OrdinalIgnoreCase);

        if (isPrimary)
        {
            _primaryRenderBackground = null; // force primary rebuild to pick up the override
            await RebuildPreviewRendererAsync(ProjectService.Instance.CurrentComposition);
        }
        else
        {
            if (_segmentPreviews.Remove(seg.Id, out var ctx))
            {
                DisposeOffUiThread(ctx.Reader);
                ctx.Reader = null;
                ctx.Dispose();
            }
            _lastRenderedSegmentId = null;
            _lastRenderedFrameIndex = -1;
            await UpdatePreviewFrameAsync(Timeline.PlayheadPosition, force: true);
        }
    }

    /// <summary>
    /// Recreates only the PreviewRenderer with updated config, preserving
    /// frame reader, audio, and playhead position.
    /// </summary>
    /// <param name="forSegment">
    /// The primary segment whose per-segment style the renderer must be built for. When
    /// null the segment under the playhead is used. Callers that rebuild in order to
    /// satisfy a specific segment (see <see cref="EnsurePrimaryRendererForSegmentAsync"/>)
    /// must pass it explicitly, otherwise the recorded style and the style their guard
    /// re-tests can disagree forever.
    /// </param>
    private async Task RebuildPreviewRendererAsync(
        CompositionConfig config, VideoSegment? forSegment = null)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        // A rebuild ends by re-rendering, and rendering can ask for another rebuild, so
        // this must not recurse. Requests that arrive mid-rebuild are coalesced rather
        // than dropped: the style handlers call this fire-and-forget, so dropping would
        // lose the last value of a slider drag.
        if (_rebuildingPreviewRenderer)
        {
            _pendingRendererRebuild = (config, forSegment);
            return;
        }

        _rebuildingPreviewRenderer = true;
        try
        {
            await RebuildPreviewRendererCoreAsync(project, config, forSegment);

            // Drain coalesced requests. Bounded purely as a backstop — the segment-scoped
            // rebuild above converges — so a future regression degrades to a stale frame
            // rather than to a frozen app.
            for (int i = 0; i < MaxCoalescedRendererRebuilds && _pendingRendererRebuild is { } pending; i++)
            {
                _pendingRendererRebuild = null;
                if (ProjectService.Instance.CurrentProject is not { } current) break;
                await RebuildPreviewRendererCoreAsync(current, pending.Config, pending.Segment);
            }

            _pendingRendererRebuild = null;
        }
        finally
        {
            _rebuildingPreviewRenderer = false;
        }
    }

    private const int MaxCoalescedRendererRebuilds = 4;
    private bool _rebuildingPreviewRenderer;
    private (CompositionConfig Config, VideoSegment? Segment)? _pendingRendererRebuild;

    private async Task RebuildPreviewRendererCoreAsync(
        Project project, CompositionConfig config, VideoSegment? forSegment)
    {
        // Apply the active primary segment's per-segment frame style / cursor override
        // on top of the global config so the primary recording honors its own style.
        var activePrimary = forSegment ?? ActivePrimaryVideoSegment();
        var effective = config;
        if (activePrimary?.FrameStyleOverride is { } bg) effective = effective with { Background = bg };
        if (activePrimary?.CursorStyleOverride is { } cur) effective = effective with { Cursor = cur };
        _primaryRenderBackground = effective.Background;
        _primaryRenderCursor = effective.Cursor;
        _primaryRendererSegmentId = activePrimary?.Id;

        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch { /* no cursor data */ }
        }

        mouseData ??= new MouseRecordingData();

        // An imported video (or any source missing its cursor log) has no real cursor to draw;
        // the compositor still zooms and styles it, but the invented centre cursor must stay
        // hidden — same rule as the appended-segment and export paths.
        effective = HideCursorWhenNoSamples(effective, mouseData);

        // Every cached alt-style compositor (see GetPrimaryTransitionCompositorAsync) was
        // built by layering the OLD global config onto its own override, and this rebuild
        // is precisely a global-config (or active-segment-style) change — reusing one past
        // this point would composite against stale crop/zoom/aspect settings. Also bumps
        // _primaryPreviewStateGeneration so any transition compose already in flight
        // against the OLD singleton detects this rebuild instead of using it past disposal.
        DisposePrimaryStyleRenderers();

        _compositorReady = false;
        _previewRenderer?.Dispose();

        try
        {
            _previewRenderer = new PreviewRenderer();
            await _previewRenderer.InitializeAsync(
                mouseData, effective,
                project.Width > 0 ? project.Width : 1920,
                project.Height > 0 ? project.Height : 1080,
                project.Duration,
                project.MouseToVideoOffsetSeconds,
                project.CropOffsetX,
                project.CropOffsetY,
                project.DpiScale);

            // Re-sync zoom state from the model. The new compositor has just regenerated
            // auto-zoom from the raw mouse data, so this has to run unconditionally —
            // an empty keyframe list and an empty suppression set are both meaningful.
            SyncZoomStateToRenderer();

            _compositorReady = true;
        }
        catch
        {
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }

        // Re-render at current playhead position
        _lastRenderedFrameIndex = -1;
        var position = Timeline.PlayheadPosition;
        await UpdatePreviewFrameAsync(position, force: true);
    }

    // Tracks the per-segment style the primary renderer was last built with, so it
    // can be rebuilt when the playhead crosses into a primary segment with a
    // different frame style / cursor override.
    private string? _primaryRendererSegmentId;

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = byte.Parse(hex[0..2], NumberStyles.HexNumber);
            byte g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
            byte b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
            return Color.FromArgb(255, r, g, b);
        }
        return Color.FromArgb(255, 26, 26, 46); // fallback
    }

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ─── Webcam Overlay Drag / Resize ──────────────────────────────────

    private void InitializeWebcamOverlay(CompositionConfig config)
    {        _hasWebcamOverlay = _webcamComposition is not null && config.WebcamStyle is not null;
        if (!_hasWebcamOverlay)
        {
            WebcamOverlayRect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Video, false);
            return;
        }

        var style = config.WebcamStyle!;
        int outW = _previewRenderer?.OutputWidth ?? 1920;
        int outH = _previewRenderer?.OutputHeight ?? 1080;

        // Determine initial normalized position from style
        _webcamNormSize = style.Size / outW;
        if (style.NormalizedX.HasValue && style.NormalizedY.HasValue)
        {
            _webcamNormX = style.NormalizedX.Value;
            _webcamNormY = style.NormalizedY.Value;
        }
        else
        {
            float margin = style.Margin;
            float size = style.Size;
            (float px, float py) = style.Position switch
            {
                WebcamPosition.TopLeft => (margin, margin),
                WebcamPosition.TopRight => (outW - size - margin, margin),
                WebcamPosition.BottomLeft => (margin, outH - size - margin),
                _ => (outW - size - margin, outH - size - margin),
            };
            _webcamNormX = px / outW;
            _webcamNormY = py / outH;
        }

        WebcamOverlayRect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Video, true);
        SyncWebcamOverlayUI(style);
        UpdateWebcamOverlayPosition();
    }

    private void UpdateWebcamOverlayPosition()
    {
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || layout.Height <= 0) return;

        double screenX = layout.X + _webcamNormX * layout.Width;
        double screenY = layout.Y + _webcamNormY * layout.Height;
        double screenSize = _webcamNormSize * layout.Width;

        Canvas.SetLeft(WebcamOverlayRect, screenX);
        Canvas.SetTop(WebcamOverlayRect, screenY);
        WebcamOverlayRect.Width = screenSize;
        WebcamOverlayRect.Height = screenSize;
    }

    private void WebcamOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Layout updates are handled by Preview.FrameLayoutChanged
    }

    private void WebcamOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_hasWebcamOverlay) return;

        _webcamDragging = true;
        _webcamDragStart = e.GetCurrentPoint(WebcamOverlayCanvas).Position;
        _webcamDragStartNormX = _webcamNormX;
        _webcamDragStartNormY = _webcamNormY;

        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void WebcamOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_webcamDragging) return;

        var pos = e.GetCurrentPoint(WebcamOverlayCanvas).Position;
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0) return;

        double dx = pos.X - _webcamDragStart.X;
        double dy = pos.Y - _webcamDragStart.Y;

        _webcamNormX = (float)Math.Clamp(
            _webcamDragStartNormX + dx / layout.Width, 0, 1 - _webcamNormSize);
        _webcamNormY = (float)Math.Clamp(
            _webcamDragStartNormY + dy / layout.Height, 0, 1 - _webcamNormSize * layout.Width / layout.Height);

        UpdateWebcamOverlayPosition();

        // Live-update the compositor so the webcam video moves in real-time
        UpdateWebcamStyleLive();

        e.Handled = true;
    }

    private void WebcamOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_webcamDragging) return;

        _webcamDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        ApplyWebcamOverlayChange();
    }

    private void UpdateWebcamStyleLive()
    {
        if (_previewRenderer is null) return;

        int outW = _previewRenderer.OutputWidth;
        float pixelSize = _webcamNormSize * outW;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with
        {
            NormalizedX = _webcamNormX,
            NormalizedY = _webcamNormY,
            Size = pixelSize,
        };

        // Lightweight update — just change the style, re-render current frame
        _previewRenderer.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void ApplyWebcamOverlayChange()
    {
        int outW = _previewRenderer?.OutputWidth ?? 1920;
        float pixelSize = _webcamNormSize * outW;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with
        {
            NormalizedX = _webcamNormX,
            NormalizedY = _webcamNormY,
            Size = pixelSize,
        };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;
    }

    private void WebcamShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (WebcamShapeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        var shape = tag == "RoundedRect" ? WebcamShape.RoundedRect : WebcamShape.Circle;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { Shape = shape };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void WebcamBorderSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { BorderWidth = (float)e.NewValue };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void SyncWebcamOverlayUI(WebcamOverlayStyle style)
    {
        _suppressWebcamEvents = true;
        WebcamShapeCombo.SelectedIndex = style.Shape == WebcamShape.RoundedRect ? 1 : 0;
        WebcamBorderSlider.Value = style.BorderWidth;
        WebcamMirrorToggle.IsOn = style.Mirrored;
        _suppressWebcamEvents = false;
    }

    private void WebcamMirrorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebcamEvents) return;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { Mirrored = WebcamMirrorToggle.IsOn };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    // ─── Aspect Ratio / Fit / Crop Anchor ──────────────────────────────

    private bool _suppressAspectRatioEvents;

    // Set once the user picks a fit mode explicitly, so the Contain default applied when a
    // real aspect ratio is first chosen never overwrites a deliberate choice.
    private bool _fitModeChosenByUser;

    /// <summary>
    /// Initializes the aspect ratio flyout from the current project + composition state.
    /// Safe to call multiple times; uses a suppression flag so event handlers don't
    /// re-write state during initialization.
    /// </summary>
    private void InitializeAspectRatioControls()
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        using var _ = SuppressScope.Enter(ref _suppressAspectRatioEvents);
        SelectRatioRadio(project.AspectRatio);
        SelectFitModeRadio(project.FitMode);
        SelectZoomScopeRadio(project.ZoomScope);
        SelectCropAnchorRadio(project.CropAnchorX, project.CropAnchorY);
        UpdateFitAndAnchorVisibility(project.AspectRatio);
    }

    private void SelectRatioRadio(AspectRatio ratio)
    {
        var target = ratio switch
        {
            AspectRatio.Landscape16x9 => Ratio16x9,
            AspectRatio.Portrait9x16 => Ratio9x16,
            AspectRatio.Square1x1 => Ratio1x1,
            AspectRatio.Instagram4x5 => Ratio4x5,
            AspectRatio.Classic4x3 => Ratio4x3,
            AspectRatio.Tall3x4 => Ratio3x4,
            AspectRatio.Cinematic21x9 => Ratio21x9,
            _ => RatioAuto,
        };
        target.IsChecked = true;
    }

    private void SelectFitModeRadio(FitMode fit)
        => SelectSegmentedByTag(FitModeSegmented, fit.ToString());

    /// <summary>
    /// Selects the segment carrying <paramref name="tag"/>. Matching on the tag rather
    /// than a hard-coded index means the segments can be reordered in XAML without
    /// silently remapping them to the wrong values.
    /// </summary>
    private static void SelectSegmentedByTag(CommunityToolkit.WinUI.Controls.Segmented segmented, string tag)
    {
        for (int i = 0; i < segmented.Items.Count; i++)
        {
            if (segmented.Items[i] is CommunityToolkit.WinUI.Controls.SegmentedItem item
                && item.Tag is string t && t == tag)
            {
                segmented.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>The fit mode currently shown in the segmented control.</summary>
    private FitMode CurrentFitModeSelection()
        => FitModeSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem item
           && item.Tag is string tag
           && Enum.TryParse<FitMode>(tag, out var fit)
            ? fit
            : FitMode.Contain;

    private void SelectZoomScopeRadio(ZoomScope scope)
    {
        ZoomScopeSegmented.SelectedIndex = scope == ZoomScope.Source ? 1 : 0;
    }

    private void SelectCropAnchorRadio(double anchorX, double anchorY)
    {
        // Snap to nearest 0 / 0.5 / 1 on each axis to find the matching radio.
        static double NearestSnap(double v) => v < 0.25 ? 0.0 : v > 0.75 ? 1.0 : 0.5;
        double sx = NearestSnap(anchorX);
        double sy = NearestSnap(anchorY);
        string tag = $"{sx.ToString(CultureInfo.InvariantCulture)},{sy.ToString(CultureInfo.InvariantCulture)}";
        foreach (var child in CropAnchorGrid.Children)
        {
            if (child is RadioButton rb && rb.Tag is string t && t == tag)
            {
                rb.IsChecked = true;
                return;
            }
        }
        CropAnchorCenter.IsChecked = true;
    }

    private void UpdateFitAndAnchorVisibility(AspectRatio ratio)
    {
        bool ratioActive = ratio != AspectRatio.Auto;
        FitModePanel.Visibility = ratioActive ? Visibility.Visible : Visibility.Collapsed;
        bool coverActive = ratioActive && CurrentFitModeSelection() == FitMode.Cover;
        CropAnchorPanel.Visibility = coverActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AspectRatioOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<AspectRatio>(tag, out var ratio)) return;
        ApplyAspectRatio(ratio);
    }

    private void FitModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (FitModeSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<FitMode>(tag, out var fit)) return;

        // Programmatic syncs are suppressed above, so reaching here means the user picked
        // the fit themselves and it must survive an Auto round-trip (see ApplyAspectRatio).
        _fitModeChosenByUser = true;
        ApplyFitMode(fit);
    }

    private void ZoomScopeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (ZoomScopeSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<ZoomScope>(tag, out var scope)) return;
        ApplyZoomScope(scope);
    }

    private void CropAnchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        var parts = tag.Split(',');
        if (parts.Length != 2) return;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)) return;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay)) return;
        ApplyCropAnchor(ax, ay);
    }

    private void ApplyAspectRatio(AspectRatio ratio)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        // Fit mode is ignored while the ratio is Auto, so a value carried over from Auto was
        // never a deliberate choice. Default it to Contain the first time a real ratio makes
        // the control meaningful, so switching ratio keeps the whole frame visible instead
        // of silently cropping it; Cover stays available as the explicit alternative.
        // Once the user has picked a fit themselves it is never overridden, so cycling
        // through Auto and back does not discard it.
        bool fitBecomesRelevant = !_fitModeChosenByUser
            && project.AspectRatio == AspectRatio.Auto
            && ratio != AspectRatio.Auto;

        project.AspectRatio = ratio;
        config = config with { AspectRatio = ratio };

        if (fitBecomesRelevant && project.FitMode != FitMode.Contain)
        {
            project.FitMode = FitMode.Contain;
            config = config with { FitMode = FitMode.Contain };

            using (SuppressScope.Enter(ref _suppressAspectRatioEvents))
                SelectFitModeRadio(FitMode.Contain);
        }

        ProjectService.Instance.CurrentComposition = config;

        UpdateFitAndAnchorVisibility(ratio);

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyFitMode(FitMode fit)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.FitMode = fit;
        config = config with { FitMode = fit };
        ProjectService.Instance.CurrentComposition = config;

        UpdateFitAndAnchorVisibility(project.AspectRatio);

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyCropAnchor(double anchorX, double anchorY)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.CropAnchorX = anchorX;
        project.CropAnchorY = anchorY;
        config = config with { CropAnchorX = anchorX, CropAnchorY = anchorY };
        ProjectService.Instance.CurrentComposition = config;

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyZoomScope(ZoomScope scope)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.ZoomScope = scope;
        config = config with { ZoomScope = scope };
        ProjectService.Instance.CurrentComposition = config;

        InvalidateSegmentPreviews();
        _ = RebuildPreviewRendererAsync(config);
    }
}
