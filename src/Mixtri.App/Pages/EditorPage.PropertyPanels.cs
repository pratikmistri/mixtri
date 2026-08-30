using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Mixtri_App.Controls;

namespace Mixtri_App.Pages;

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
    private ToggleSwitch MotionBlurToggle => PropertiesPanel.Scene.MotionBlurToggle;
    private Slider MotionBlurSlider => PropertiesPanel.Scene.MotionBlurSlider;

    // ─── Zoom segment panel ─────────────────────────────────────────────

    private Slider ZoomLevelSlider => PropertiesPanel.Zoom.ZoomLevelSlider;
    private ToggleSwitch ZoomDriftToggle => PropertiesPanel.Zoom.ZoomDriftToggle;
    private Slider ZoomDriftSlider => PropertiesPanel.Zoom.ZoomDriftSlider;

    // ─── Text slide panel ───────────────────────────────────────────────

    private TextBox SlideTextBox => PropertiesPanel.TextSlide.SlideTextBox;
    private ComboBox SlideAnimationCombo => PropertiesPanel.TextSlide.SlideAnimationCombo;
    private Slider SlideTextInAtSlider => PropertiesPanel.TextSlide.SlideTextInAtSlider;
    private Slider SlideTextOutBySlider => PropertiesPanel.TextSlide.SlideTextOutBySlider;
    private Slider SlideTextInRampSlider => PropertiesPanel.TextSlide.SlideTextInRampSlider;
    private Slider SlideTextOutRampSlider => PropertiesPanel.TextSlide.SlideTextOutRampSlider;
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
    private RadioButton CursorTypeHidden => PropertiesPanel.Cursor.CursorTypeHidden;
    private Slider CursorSizeSlider => PropertiesPanel.Cursor.CursorSizeSlider;
    private ToggleSwitch CursorTiltToggle => PropertiesPanel.Cursor.CursorTiltToggle;
    private StackPanel CursorColorPanel => PropertiesPanel.Cursor.CursorColorPanel;
    private StackPanel CursorColorSection => PropertiesPanel.Cursor.CursorColorSection;
    private StackPanel CursorAnchorSection => PropertiesPanel.Cursor.CursorAnchorSection;
    private Button CursorAnchorEditButton => PropertiesPanel.Cursor.CursorAnchorEditButton;
    private TextBlock CursorAnchorUnavailableText => PropertiesPanel.Cursor.CursorAnchorUnavailableText;

    // ─── Video (camera overlay) panel ───────────────────────────────────

    private ComboBox WebcamShapeCombo => PropertiesPanel.Video.WebcamShapeCombo;
    private Slider WebcamBorderSlider => PropertiesPanel.Video.WebcamBorderSlider;
    private ToggleSwitch WebcamMirrorToggle => PropertiesPanel.Video.WebcamMirrorToggle;
    private StackPanel CameraFullscreenPanel => PropertiesPanel.Video.CameraFullscreenPanel;
    private ToggleSwitch CameraFullscreenToggle => PropertiesPanel.Video.CameraFullscreenToggle;
    private ComboBox CameraFullscreenModeCombo => PropertiesPanel.Video.CameraFullscreenModeCombo;
    private TextBlock CameraFullscreenHint => PropertiesPanel.Video.CameraFullscreenHint;
    private Button CameraDeleteButton => PropertiesPanel.Video.CameraDeleteButton;

    // ─── Text overlay panel ─────────────────────────────────────────────

    private GridView OverlayPresets => PropertiesPanel.TextOverlay.OverlayPresets;
    private TextBox OverlayTextBox => PropertiesPanel.TextOverlay.OverlayTextBox;
    private ComboBox OverlayAnimationCombo => PropertiesPanel.TextOverlay.OverlayAnimationCombo;
    private ComboBox OverlayFontCombo => PropertiesPanel.TextOverlay.OverlayFontCombo;
    private NumberBox OverlayDurationBox => PropertiesPanel.TextOverlay.OverlayDurationBox;
    private NumberBox OverlayFontSizeBox => PropertiesPanel.TextOverlay.OverlayFontSizeBox;
    private Border OverlayTextColorSwatch => PropertiesPanel.TextOverlay.OverlayTextColorSwatch;
    private TextBlock OverlayTextColorText => PropertiesPanel.TextOverlay.OverlayTextColorText;
    private ColorPicker OverlayTextColorPicker => PropertiesPanel.TextOverlay.OverlayTextColorPicker;
    private ToggleButton OverlayBoldToggle => PropertiesPanel.TextOverlay.OverlayBoldToggle;
    private ToggleButton OverlayItalicToggle => PropertiesPanel.TextOverlay.OverlayItalicToggle;
    private Segmented OverlayAlignSegmented => PropertiesPanel.TextOverlay.OverlayAlignSegmented;
    private Grid OverlayAnchorGrid => PropertiesPanel.TextOverlay.OverlayAnchorGrid;
    private RadioButton OverlayAnchorBottomCenter => PropertiesPanel.TextOverlay.OverlayAnchorBottomCenter;
    private TextBlock OverlayCustomPositionHint => PropertiesPanel.TextOverlay.OverlayCustomPositionHint;
    private Slider OverlayWidthSlider => PropertiesPanel.TextOverlay.OverlayWidthSlider;
    private Slider OverlayMarginSlider => PropertiesPanel.TextOverlay.OverlayMarginSlider;
    private ComboBox OverlayBgTypeCombo => PropertiesPanel.TextOverlay.OverlayBgTypeCombo;
    private StackPanel OverlayBoxPanel => PropertiesPanel.TextOverlay.OverlayBoxPanel;
    private Border OverlayBgColorSwatch => PropertiesPanel.TextOverlay.OverlayBgColorSwatch;
    private TextBlock OverlayBgColorText => PropertiesPanel.TextOverlay.OverlayBgColorText;
    private ColorPicker OverlayBgColorPicker => PropertiesPanel.TextOverlay.OverlayBgColorPicker;
    private Slider OverlayBgOpacitySlider => PropertiesPanel.TextOverlay.OverlayBgOpacitySlider;
    private Slider OverlayCornerRadiusSlider => PropertiesPanel.TextOverlay.OverlayCornerRadiusSlider;
    private Slider OverlayPaddingSlider => PropertiesPanel.TextOverlay.OverlayPaddingSlider;
    private StackPanel OverlayBlurPanel => PropertiesPanel.TextOverlay.OverlayBlurPanel;
    private Slider OverlayBlurAmountSlider => PropertiesPanel.TextOverlay.OverlayBlurAmountSlider;
    private Slider OverlayBlurTintSlider => PropertiesPanel.TextOverlay.OverlayBlurTintSlider;
    private StackPanel OverlayScrimPanel => PropertiesPanel.TextOverlay.OverlayScrimPanel;
    private ComboBox OverlayScrimDirectionCombo => PropertiesPanel.TextOverlay.OverlayScrimDirectionCombo;
    private Slider OverlayScrimStrengthSlider => PropertiesPanel.TextOverlay.OverlayScrimStrengthSlider;
    private StackPanel OverlayOutlinePanel => PropertiesPanel.TextOverlay.OverlayOutlinePanel;
    private Slider OverlayOutlineWidthSlider => PropertiesPanel.TextOverlay.OverlayOutlineWidthSlider;
    private Border OverlayOutlineColorSwatch => PropertiesPanel.TextOverlay.OverlayOutlineColorSwatch;
    private TextBlock OverlayOutlineColorText => PropertiesPanel.TextOverlay.OverlayOutlineColorText;
    private ColorPicker OverlayOutlineColorPicker => PropertiesPanel.TextOverlay.OverlayOutlineColorPicker;
    private Slider OverlayShadowStrengthSlider => PropertiesPanel.TextOverlay.OverlayShadowStrengthSlider;
    private StackPanel OverlayAccentPanel => PropertiesPanel.TextOverlay.OverlayAccentPanel;
    private Border OverlayAccentColorSwatch => PropertiesPanel.TextOverlay.OverlayAccentColorSwatch;
    private TextBlock OverlayAccentColorText => PropertiesPanel.TextOverlay.OverlayAccentColorText;
    private ColorPicker OverlayAccentColorPicker => PropertiesPanel.TextOverlay.OverlayAccentColorPicker;
    private Slider OverlayAccentThicknessSlider => PropertiesPanel.TextOverlay.OverlayAccentThicknessSlider;
    private ComboBox OverlayAccentSideCombo => PropertiesPanel.TextOverlay.OverlayAccentSideCombo;
    private ToggleSwitch OverlayEnabledToggle => PropertiesPanel.TextOverlay.OverlayEnabledToggle;
    private Button RemoveOverlayButton => PropertiesPanel.TextOverlay.RemoveOverlayButton;

    // ─── Transition panel ───────────────────────────────────────────────

    private ComboBox TransitionFamilyCombo => PropertiesPanel.Transition.TransitionFamilyCombo;
    private ComboBox TransitionVariantCombo => PropertiesPanel.Transition.TransitionVariantCombo;
    private TextBlock TransitionAutomaticHint => PropertiesPanel.Transition.TransitionAutomaticHint;
    private Slider TransitionDurationSlider => PropertiesPanel.Transition.TransitionDurationSlider;
    private TextBlock TransitionDurationClampHint => PropertiesPanel.Transition.TransitionDurationClampHint;
    private ComboBox TransitionEasingCombo => PropertiesPanel.Transition.TransitionEasingCombo;
    private Button ApplyTransitionToAllButton => PropertiesPanel.Transition.ApplyTransitionToAllButton;
    private Button RemoveTransitionButton => PropertiesPanel.Transition.RemoveTransitionButton;

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
        MotionBlurToggle.Toggled += MotionToggle_Toggled;
        MotionBlurSlider.ValueChanged += MotionSlider_ValueChanged;

        // Zoom segment panel
        ZoomLevelSlider.ValueChanged += ZoomLevelSlider_ValueChanged;
        PropertiesPanel.Zoom.EditZoomRegionButton.Click += EditZoomRegion_Click;
        PropertiesPanel.Zoom.RemoveZoomSegmentButton.Click += RemoveZoomSegment_Click;
        ZoomDriftToggle.Toggled += ZoomDriftToggle_Toggled;
        ZoomDriftSlider.ValueChanged += ZoomDriftSlider_ValueChanged;

        // The zoom level a newly drawn segment is created at. Set here rather than as a
        // XAML Value (playbook: a XAML default fires ValueChanged during
        // InitializeComponent, before the suppress flags exist) — and it has to be set
        // SOMEWHERE, because a slider left at its Minimum would create every new segment
        // at 1x, i.e. no zoom at all.
        ZoomLevelSlider.Value = DefaultNewSegmentZoom;
        UpdateZoomLevelReadout(DefaultNewSegmentZoom);

        // Both sliders' drag-start/end are picked up here rather than via XAML
        // PointerPressed/PointerCaptureLost attributes: Slider/RangeBase marks those routed
        // events Handled once its own track/thumb pointer handling claims them, so a plain
        // declarative hook on the Slider itself commonly never fires. AddHandler with
        // handledEventsToo:true still gets them, which is what the ValueChanged handlers
        // rely on to tell a drag from a single committed change (see the field docs on
        // _zoomDriftDragging / _zoomLevelDragging in EditorPage.Timeline.cs).
        ZoomDriftSlider.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ZoomDriftSlider_PointerPressed), handledEventsToo: true);
        ZoomDriftSlider.AddHandler(Microsoft.UI.Xaml.UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ZoomDriftSlider_PointerCaptureLost), handledEventsToo: true);
        ZoomLevelSlider.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ZoomLevelSlider_PointerPressed), handledEventsToo: true);
        ZoomLevelSlider.AddHandler(Microsoft.UI.Xaml.UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ZoomLevelSlider_PointerCaptureLost), handledEventsToo: true);

        // Text slide
        SlideTextBox.TextChanged += SlideTextBox_TextChanged;
        SlideAnimationCombo.SelectionChanged += SlideAnimationCombo_SelectionChanged;
        SlideTextInAtSlider.ValueChanged += SlideTextWindowSlider_ValueChanged;
        SlideTextOutBySlider.ValueChanged += SlideTextWindowSlider_ValueChanged;
        SlideTextInRampSlider.ValueChanged += SlideTextRampSlider_ValueChanged;
        SlideTextOutRampSlider.ValueChanged += SlideTextRampSlider_ValueChanged;
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
        CursorTypeHidden.Checked += CursorType_Checked;
        CursorSizeSlider.ValueChanged += CursorSizeSlider_ValueChanged;
        CursorTiltToggle.Toggled += CursorTiltToggle_Toggled;
        CursorAnchorEditButton.Click += CursorAnchorEdit_Click;
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

        // Text overlay
        OverlayPresets.SelectionChanged += OverlayPreset_SelectionChanged;
        OverlayTextBox.TextChanged += OverlayTextBox_TextChanged;
        OverlayAnimationCombo.SelectionChanged += OverlayAnimationCombo_SelectionChanged;
        OverlayFontCombo.SelectionChanged += OverlayFontCombo_SelectionChanged;
        OverlayDurationBox.ValueChanged += OverlayDurationBox_ValueChanged;
        OverlayFontSizeBox.ValueChanged += OverlayFontSizeBox_ValueChanged;
        OverlayTextColorPicker.ColorChanged += OverlayTextColorPicker_ColorChanged;
        OverlayBoldToggle.Click += OverlayBold_Click;
        OverlayItalicToggle.Click += OverlayItalic_Click;
        OverlayAlignSegmented.SelectionChanged += OverlayAlignSegmented_SelectionChanged;

        foreach (var child in OverlayAnchorGrid.Children)
        {
            if (child is RadioButton cell)
                cell.Checked += OverlayAnchor_Checked;
        }

        OverlayWidthSlider.ValueChanged += OverlayWidthSlider_ValueChanged;
        OverlayMarginSlider.ValueChanged += OverlayMarginSlider_ValueChanged;
        OverlayBgTypeCombo.SelectionChanged += OverlayBgTypeCombo_SelectionChanged;
        OverlayBgColorPicker.ColorChanged += OverlayBgColorPicker_ColorChanged;

        // OverlayBgOpacitySlider / OverlayCornerRadiusSlider / OverlayPaddingSlider all live
        // in the shared OverlayBoxPanel (see its XAML comment) and drive the same "box"
        // properties regardless of which background type is selected, so they share one
        // handler — mirroring how Scene's GradAngleSlider/PaddingSlider/CornerRadiusSlider
        // share StyleSlider_ValueChanged.
        OverlayBgOpacitySlider.ValueChanged += OverlayBoxSlider_ValueChanged;
        OverlayCornerRadiusSlider.ValueChanged += OverlayBoxSlider_ValueChanged;
        OverlayPaddingSlider.ValueChanged += OverlayBoxSlider_ValueChanged;

        // OverlayBlurAmountSlider / OverlayBlurTintSlider likewise share one handler.
        OverlayBlurAmountSlider.ValueChanged += OverlayBlurSlider_ValueChanged;
        OverlayBlurTintSlider.ValueChanged += OverlayBlurSlider_ValueChanged;

        OverlayScrimDirectionCombo.SelectionChanged += OverlayScrimDirectionCombo_SelectionChanged;
        OverlayScrimStrengthSlider.ValueChanged += OverlayScrimStrengthSlider_ValueChanged;
        OverlayOutlineWidthSlider.ValueChanged += OverlayOutlineWidthSlider_ValueChanged;
        OverlayOutlineColorPicker.ColorChanged += OverlayOutlineColorPicker_ColorChanged;
        OverlayShadowStrengthSlider.ValueChanged += OverlayShadowStrengthSlider_ValueChanged;
        OverlayAccentColorPicker.ColorChanged += OverlayAccentColorPicker_ColorChanged;
        OverlayAccentThicknessSlider.ValueChanged += OverlayAccentThicknessSlider_ValueChanged;
        OverlayAccentSideCombo.SelectionChanged += OverlayAccentSideCombo_SelectionChanged;
        OverlayEnabledToggle.Toggled += OverlayEnabledToggle_Toggled;
        RemoveOverlayButton.Click += RemoveTextOverlay_Click;

        // Transition
        TransitionFamilyCombo.SelectionChanged += TransitionFamilyCombo_SelectionChanged;
        TransitionVariantCombo.SelectionChanged += TransitionVariantCombo_SelectionChanged;
        TransitionDurationSlider.ValueChanged += TransitionDurationSlider_ValueChanged;
        TransitionEasingCombo.SelectionChanged += TransitionEasingCombo_SelectionChanged;
        ApplyTransitionToAllButton.Click += ApplyTransitionToAllButton_Click;
        RemoveTransitionButton.Click += RemoveTransitionButton_Click;
    }
}
