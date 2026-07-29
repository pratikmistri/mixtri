using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Musio_App.Controls;

namespace Musio_App.Pages;

/// <summary>
/// Bridges <see cref="EditorPage"/> to the property panels hosted by
/// <see cref="PropertiesPane"/>.
/// </summary>
/// <remarks>
/// The scene / text slide / mouse / video editors used to be inline toolbar flyouts, so all
/// of their controls lived in the page's own name scope. They now live in separate views, so
/// this file re-exposes each control under its original name and wires the page's event
/// handlers to them from code. Keeping the aliases means the editing logic in
/// <c>EditorPage.xaml.cs</c> is unchanged by the extraction.
/// </remarks>
public sealed partial class EditorPage
{
    // ─── Scene (frame style) panel ──────────────────────────────────────

    private RadioButton RatioAuto => PropertiesPanel.Scene.RatioAuto;
    private RadioButton Ratio16x9 => PropertiesPanel.Scene.Ratio16x9;
    private RadioButton Ratio9x16 => PropertiesPanel.Scene.Ratio9x16;
    private RadioButton Ratio1x1 => PropertiesPanel.Scene.Ratio1x1;
    private RadioButton Ratio4x5 => PropertiesPanel.Scene.Ratio4x5;
    private RadioButton Ratio4x3 => PropertiesPanel.Scene.Ratio4x3;
    private RadioButton Ratio3x4 => PropertiesPanel.Scene.Ratio3x4;
    private RadioButton Ratio21x9 => PropertiesPanel.Scene.Ratio21x9;
    private StackPanel FitModePanel => PropertiesPanel.Scene.FitModePanel;
    private Segmented FitModeSegmented => PropertiesPanel.Scene.FitModeSegmented;
    private Segmented ZoomScopeSegmented => PropertiesPanel.Scene.ZoomScopeSegmented;
    private StackPanel CropAnchorPanel => PropertiesPanel.Scene.CropAnchorPanel;
    private Grid CropAnchorGrid => PropertiesPanel.Scene.CropAnchorGrid;
    private RadioButton CropAnchorCenter => PropertiesPanel.Scene.CropAnchorCenter;
    private ComboBox PresetCombo => PropertiesPanel.Scene.PresetCombo;
    private ComboBox BgTypeCombo => PropertiesPanel.Scene.BgTypeCombo;
    private StackPanel ColorPanel => PropertiesPanel.Scene.ColorPanel;
    private Border BgColorSwatch => PropertiesPanel.Scene.BgColorSwatch;
    private TextBlock BgColorText => PropertiesPanel.Scene.BgColorText;
    private ColorPicker BgColorPicker => PropertiesPanel.Scene.BgColorPicker;
    private StackPanel GradientPanel => PropertiesPanel.Scene.GradientPanel;
    private Border GradEndColorSwatch => PropertiesPanel.Scene.GradEndColorSwatch;
    private TextBlock GradEndColorText => PropertiesPanel.Scene.GradEndColorText;
    private ColorPicker GradEndColorPicker => PropertiesPanel.Scene.GradEndColorPicker;
    private Slider GradAngleSlider => PropertiesPanel.Scene.GradAngleSlider;
    private StackPanel WallpaperPanel => PropertiesPanel.Scene.WallpaperPanel;
    private GridView WallpaperGrid => PropertiesPanel.Scene.WallpaperGrid;
    private Slider PaddingSlider => PropertiesPanel.Scene.PaddingSlider;
    private Slider CornerRadiusSlider => PropertiesPanel.Scene.CornerRadiusSlider;
    private ToggleSwitch ShadowToggle => PropertiesPanel.Scene.ShadowToggle;
    private ToggleSwitch BorderToggle => PropertiesPanel.Scene.BorderToggle;

    // ─── Text slide panel ───────────────────────────────────────────────

    private TextBox SlideTextBox => PropertiesPanel.TextSlide.SlideTextBox;
    private ComboBox SlideAnimationCombo => PropertiesPanel.TextSlide.SlideAnimationCombo;
    private ComboBox SlideFontCombo => PropertiesPanel.TextSlide.SlideFontCombo;
    private NumberBox SlideDurationBox => PropertiesPanel.TextSlide.SlideDurationBox;
    private NumberBox SlideFontSizeBox => PropertiesPanel.TextSlide.SlideFontSizeBox;
    private Border SlideTextColorSwatch => PropertiesPanel.TextSlide.SlideTextColorSwatch;
    private TextBlock SlideTextColorText => PropertiesPanel.TextSlide.SlideTextColorText;
    private ColorPicker SlideTextColorPicker => PropertiesPanel.TextSlide.SlideTextColorPicker;
    private ToggleButton SlideBoldToggle => PropertiesPanel.TextSlide.SlideBoldToggle;
    private ToggleButton SlideItalicToggle => PropertiesPanel.TextSlide.SlideItalicToggle;
    private Segmented SlideAlignSegmented => PropertiesPanel.TextSlide.SlideAlignSegmented;
    private ComboBox SlideBgTypeCombo => PropertiesPanel.TextSlide.SlideBgTypeCombo;
    private StackPanel SlideColorPanel => PropertiesPanel.TextSlide.SlideColorPanel;
    private TextBlock SlideColorLabel => PropertiesPanel.TextSlide.SlideColorLabel;
    private Border SlideBgColorSwatch => PropertiesPanel.TextSlide.SlideBgColorSwatch;
    private TextBlock SlideBgColorText => PropertiesPanel.TextSlide.SlideBgColorText;
    private ColorPicker SlideBgColorPicker => PropertiesPanel.TextSlide.SlideBgColorPicker;
    private StackPanel SlideGradientPanel => PropertiesPanel.TextSlide.SlideGradientPanel;
    private GridView SlideGradientPresets => PropertiesPanel.TextSlide.SlideGradientPresets;
    private Border SlideGradEndColorSwatch => PropertiesPanel.TextSlide.SlideGradEndColorSwatch;
    private TextBlock SlideGradEndColorText => PropertiesPanel.TextSlide.SlideGradEndColorText;
    private ColorPicker SlideGradEndColorPicker => PropertiesPanel.TextSlide.SlideGradEndColorPicker;
    private Slider SlideGradAngleSlider => PropertiesPanel.TextSlide.SlideGradAngleSlider;
    private StackPanel SlideImagePanel => PropertiesPanel.TextSlide.SlideImagePanel;
    private TextBlock SlideImagePathText => PropertiesPanel.TextSlide.SlideImagePathText;

    // ─── Mouse (cursor) panel ───────────────────────────────────────────

    private RadioButton CursorTypeMouse => PropertiesPanel.Cursor.CursorTypeMouse;
    private RadioButton CursorTypeTouch => PropertiesPanel.Cursor.CursorTypeTouch;
    private Slider CursorSizeSlider => PropertiesPanel.Cursor.CursorSizeSlider;
    private ToggleSwitch CursorTiltToggle => PropertiesPanel.Cursor.CursorTiltToggle;
    private StackPanel CursorColorPanel => PropertiesPanel.Cursor.CursorColorPanel;

    // ─── Video (camera overlay) panel ───────────────────────────────────

    private ComboBox WebcamShapeCombo => PropertiesPanel.Video.WebcamShapeCombo;
    private Slider WebcamBorderSlider => PropertiesPanel.Video.WebcamBorderSlider;
    private ToggleSwitch WebcamMirrorToggle => PropertiesPanel.Video.WebcamMirrorToggle;
    private StackPanel CameraFullscreenPanel => PropertiesPanel.Video.CameraFullscreenPanel;
    private ToggleSwitch CameraFullscreenToggle => PropertiesPanel.Video.CameraFullscreenToggle;
    private ComboBox CameraFullscreenModeCombo => PropertiesPanel.Video.CameraFullscreenModeCombo;
    private TextBlock CameraFullscreenHint => PropertiesPanel.Video.CameraFullscreenHint;
    private Button CameraDeleteButton => PropertiesPanel.Video.CameraDeleteButton;

    /// <summary>
    /// Attaches the page's editing handlers to the extracted property panel controls.
    /// The panels are plain markup, so this replaces the event attributes the controls
    /// carried while they lived in the page's XAML.
    /// </summary>
    private void WirePropertyPanels()
    {
        // Scene — aspect ratio / fit / crop
        foreach (var ratio in new[]
                 {
                     RatioAuto, Ratio16x9, Ratio9x16, Ratio1x1,
                     Ratio4x5, Ratio4x3, Ratio3x4, Ratio21x9,
                 })
        {
            ratio.Checked += AspectRatioOption_Checked;
        }

        FitModeSegmented.SelectionChanged += FitModeSegmented_SelectionChanged;
        ZoomScopeSegmented.SelectionChanged += ZoomScopeSegmented_SelectionChanged;

        foreach (var child in CropAnchorGrid.Children)
        {
            if (child is RadioButton cell)
                cell.Checked += CropAnchor_Checked;
        }

        // Scene — background
        PresetCombo.SelectionChanged += PresetCombo_SelectionChanged;
        BgTypeCombo.SelectionChanged += BgTypeCombo_SelectionChanged;
        BgColorPicker.ColorChanged += BgColorPicker_ColorChanged;
        GradEndColorPicker.ColorChanged += GradEndColorPicker_ColorChanged;
        GradAngleSlider.ValueChanged += StyleSlider_ValueChanged;
        WallpaperGrid.SelectionChanged += WallpaperGrid_SelectionChanged;
        WallpaperGrid.SizeChanged += WallpaperGrid_SizeChanged;
        PaddingSlider.ValueChanged += StyleSlider_ValueChanged;
        CornerRadiusSlider.ValueChanged += StyleSlider_ValueChanged;
        ShadowToggle.Toggled += StyleToggle_Toggled;
        BorderToggle.Toggled += StyleToggle_Toggled;

        // Text slide
        SlideTextBox.TextChanged += SlideTextBox_TextChanged;
        SlideAnimationCombo.SelectionChanged += SlideAnimationCombo_SelectionChanged;
        SlideFontCombo.SelectionChanged += SlideFontCombo_SelectionChanged;
        SlideDurationBox.ValueChanged += SlideDurationBox_ValueChanged;
        SlideFontSizeBox.ValueChanged += SlideFontSizeBox_ValueChanged;
        SlideTextColorPicker.ColorChanged += SlideTextColorPicker_ColorChanged;
        SlideBoldToggle.Click += SlideBold_Click;
        SlideItalicToggle.Click += SlideItalic_Click;
        SlideAlignSegmented.SelectionChanged += SlideAlignSegmented_SelectionChanged;
        SlideBgTypeCombo.SelectionChanged += SlideBgTypeCombo_SelectionChanged;
        SlideBgColorPicker.ColorChanged += SlideBgColorPicker_ColorChanged;
        SlideGradientPresets.SelectionChanged += SlideGradientPreset_SelectionChanged;
        SlideGradEndColorPicker.ColorChanged += SlideGradEndColorPicker_ColorChanged;
        SlideGradAngleSlider.ValueChanged += SlideGradAngleSlider_ValueChanged;
        PropertiesPanel.TextSlide.ChooseSlideImageButton.Click += ChooseSlideImage_Click;
        PropertiesPanel.TextSlide.RemoveSlideButton.Click += RemoveTextSlide_Click;

        // Mouse
        CursorTypeMouse.Checked += CursorType_Checked;
        CursorTypeTouch.Checked += CursorType_Checked;
        CursorSizeSlider.ValueChanged += CursorSizeSlider_ValueChanged;
        CursorTiltToggle.Toggled += CursorTiltToggle_Toggled;
        foreach (var child in CursorColorPanel.Children)
        {
            if (child is RadioButton swatch)
                swatch.Checked += CursorColor_Checked;
        }

        // Video
        WebcamShapeCombo.SelectionChanged += WebcamShapeCombo_SelectionChanged;
        WebcamBorderSlider.ValueChanged += WebcamBorderSlider_ValueChanged;
        WebcamMirrorToggle.Toggled += WebcamMirrorToggle_Toggled;
        CameraFullscreenToggle.Toggled += CameraFullscreenToggle_Toggled;
        CameraFullscreenModeCombo.SelectionChanged += CameraFullscreenModeCombo_SelectionChanged;
        CameraDeleteButton.Click += CameraDeleteButton_Click;
    }
}
