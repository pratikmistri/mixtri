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
    private ITextEditTarget? _editTarget;
    private ITextEditTarget? _dragTarget;
    private bool _textRegionDragging;
    private Point _textDragStart;
    private double _textDragStartX, _textDragStartY;

    // Whether the pointer has moved past TextDragThreshold since PointerPressed. A plain
    // click (no real movement) must be a total no-op — see FinalizeTextDrag/AbortDrag.
    private bool _textDragMoved;

    // Pixel threshold before a press is treated as an actual drag rather than a click,
    // mirroring TimelineControl's ZoomCreateDragThreshold convention.
    private const double TextDragThreshold = 5.0;

    // Per-frame positioning cache for GetActiveEditTarget (Preview.FrameLayoutChanged /
    // ShowTextEditOverlay run every drawn frame) — reused across frames as long as the
    // previewed slide/overlay id hasn't changed, to avoid allocating a new
    // SlideTextEditTarget/OverlayTextEditTarget and re-running the PreviewSlide()/
    // PreviewOverlay() LINQ lookups on every Win2D draw callback. NEVER reused for a
    // gesture that is just starting (PointerPressed/EnterTextEdit use
    // GetActiveEditTargetForNewGesture instead) — OverlayTextEditTarget captures its
    // pre-gesture original text/position/anchor at construction time, so reusing a cached
    // instance from an earlier frame could restore a stale "original" if anything (e.g. the
    // properties pane) changed the overlay since the cache was last built.
    private ITextEditTarget? _cachedEditTarget;
    private string? _cachedEditTargetId;
    private bool _suppressOverlayEvents;

    private TextOverlaySegment? SelectedTextOverlay() =>
        _selectedTextOverlayId is null ? null : ViewModel.Model.TextOverlays
            .FirstOrDefault(o => o.Id == _selectedTextOverlayId);

    /// <summary>
    /// Pushes every property of the overlay identified by <paramref name="overlayId"/> onto
    /// its pane controls (or hides the pane entirely if the overlay no longer exists), then
    /// reveals the pane. Mirrors <see cref="SyncCameraSegmentUI"/>/<see cref="ShowTextSlidePanel"/>.
    /// </summary>
    private void SyncTextOverlayUI(string? overlayId)
    {
        if (OverlayTextBox is null) return;

        var overlay = overlayId is null
            ? null
            : ViewModel.Model.TextOverlays.FirstOrDefault(o => o.Id == overlayId);

        if (overlay is null)
        {
            PropertiesPanel.SetPaneAvailable(PropertyPaneKind.TextOverlay, false);
            return;
        }

        BuildOverlayPresetsIfNeeded();

        _suppressOverlayEvents = true;

        // Presets are a one-shot action, not a persisted field — clear any lingering tile
        // selection so switching overlays (or editing controls directly) never leaves a
        // stale preset looking selected. Guarded above/below by _suppressOverlayEvents since
        // this re-raises OverlayPreset_SelectionChanged.
        OverlayPresets.SelectedItem = null;

        OverlayTextBox.Text = overlay.Text;
        OverlayDurationBox.Value = overlay.Duration.TotalSeconds;
        OverlayFontSizeBox.Value = overlay.FontSize;

        OverlayBoldToggle.IsChecked = overlay.IsBold;
        OverlayItalicToggle.IsChecked = overlay.IsItalic;
        OverlayAlignSegmented.SelectedIndex = overlay.TextAlignment switch
        {
            SlideTextAlignment.Left => 0,
            SlideTextAlignment.Right => 2,
            _ => 1,
        };

        SetOverlayFontSelection(overlay.FontFamily);

        var animName = overlay.Animation.ToString();
        for (int i = 0; i < OverlayAnimationCombo.Items.Count; i++)
        {
            if (OverlayAnimationCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == animName)
            {
                OverlayAnimationCombo.SelectedIndex = i;
                break;
            }
        }

        UpdateSlideColorSwatch(OverlayTextColorSwatch, OverlayTextColorText, OverlayTextColorPicker, overlay.TextColor);
        UpdateSlideColorSwatch(OverlayBgColorSwatch, OverlayBgColorText, OverlayBgColorPicker, overlay.BackgroundColor);
        UpdateSlideColorSwatch(OverlayOutlineColorSwatch, OverlayOutlineColorText, OverlayOutlineColorPicker, overlay.OutlineColor);
        UpdateSlideColorSwatch(OverlayAccentColorSwatch, OverlayAccentColorText, OverlayAccentColorPicker, overlay.AccentColor);

        // Anchor radios (nine-point grid) + the custom-position hint
        bool isCustom = overlay.Anchor == TextOverlayAnchor.Custom;
        var anchorName = overlay.Anchor.ToString();
        foreach (var child in OverlayAnchorGrid.Children)
        {
            if (child is RadioButton rb)
                rb.IsChecked = !isCustom && rb.Tag as string == anchorName;
        }
        OverlayCustomPositionHint.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        OverlayWidthSlider.Value = overlay.WidthFraction * 100.0;
        PropertiesPanel.TextOverlay.OverlayHeightSlider.Value = overlay.HeightFraction * 100.0;
        OverlayMarginSlider.Value = overlay.MarginFraction * 100.0;

        var bgTypeName = overlay.Background.ToString();
        for (int i = 0; i < OverlayBgTypeCombo.Items.Count; i++)
        {
            if (OverlayBgTypeCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == bgTypeName)
            {
                OverlayBgTypeCombo.SelectedIndex = i;
                break;
            }
        }

        OverlayBgOpacitySlider.Value = overlay.BackgroundOpacity * 100.0;
        OverlayCornerRadiusSlider.Value = overlay.CornerRadius;
        OverlayPaddingSlider.Value = overlay.PaddingScale * 100.0;

        OverlayBlurAmountSlider.Value = overlay.BlurAmount;
        OverlayBlurTintSlider.Value = overlay.BlurTintOpacity * 100.0;

        var scrimDirName = overlay.ScrimDirection.ToString();
        for (int i = 0; i < OverlayScrimDirectionCombo.Items.Count; i++)
        {
            if (OverlayScrimDirectionCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == scrimDirName)
            {
                OverlayScrimDirectionCombo.SelectedIndex = i;
                break;
            }
        }
        OverlayScrimStrengthSlider.Value = overlay.ScrimStrength * 100.0;

        OverlayOutlineWidthSlider.Value = overlay.OutlineWidth;
        OverlayShadowStrengthSlider.Value = overlay.ShadowStrength * 100.0;

        OverlayAccentThicknessSlider.Value = overlay.AccentThickness;
        var accentSideName = overlay.AccentSide.ToString();
        for (int i = 0; i < OverlayAccentSideCombo.Items.Count; i++)
        {
            if (OverlayAccentSideCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == accentSideName)
            {
                OverlayAccentSideCombo.SelectedIndex = i;
                break;
            }
        }

        OverlayEnabledToggle.IsOn = overlay.Enabled;

        UpdateOverlayBackgroundPanels(overlay.Background);

        _suppressOverlayEvents = false;

        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.TextOverlay, true);
        PropertiesPanel.ShowPane(PropertyPaneKind.TextOverlay);
    }

    /// <summary>
    /// Shows/hides each background sub-panel for the selected <see cref="TextOverlayBackground"/>.
    /// <see cref="OverlayBoxPanel"/> (color/opacity/corner-radius/padding) backs a filled box
    /// and is shared by Solid, Blur and AccentBar; the other panels each add their own
    /// mode-specific controls on top of it (GradientScrim/OutlineShadow stand alone since
    /// those modes draw no filled box).
    /// </summary>
    private void UpdateOverlayBackgroundPanels(TextOverlayBackground background)
    {
        OverlayBoxPanel.Visibility =
            background is TextOverlayBackground.Solid or TextOverlayBackground.Blur or TextOverlayBackground.AccentBar
                ? Visibility.Visible : Visibility.Collapsed;
        OverlayBlurPanel.Visibility = background == TextOverlayBackground.Blur ? Visibility.Visible : Visibility.Collapsed;
        OverlayScrimPanel.Visibility = background == TextOverlayBackground.GradientScrim ? Visibility.Visible : Visibility.Collapsed;
        OverlayOutlinePanel.Visibility = background == TextOverlayBackground.OutlineShadow ? Visibility.Visible : Visibility.Collapsed;
        OverlayAccentPanel.Visibility = background == TextOverlayBackground.AccentBar ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool _suppressTransitionEvents;

    /// <summary>
    /// Resolves the incoming/outgoing segments flanking the boundary owned by
    /// <paramref name="incomingSegmentId"/>. The first segment (index 0) has no leading
    /// boundary, matching <see cref="TransitionResolver"/>'s own "index &lt;= 0" rule.
    /// </summary>
    private (TimelineSegment? Incoming, TimelineSegment? Outgoing) GetTransitionBoundarySegments(string incomingSegmentId)
    {
        var segments = ViewModel.Model.Segments;
        int index = segments.FindIndex(s => s.Id == incomingSegmentId);
        if (index <= 0) return (null, null);
        return (segments[index], segments[index - 1]);
    }

    /// <summary>
    /// Maps a <see cref="TransitionType"/> to the pane's picker family, mirroring
    /// <see cref="TimelineControl"/>'s chip colour grouping (dissolve / slide+push / wipe /
    /// stylised) except that Slide and Push are kept as two separate families here — the pane
    /// has room to be more specific than a 20px chip glyph does.
    /// </summary>
    private static string TransitionFamilyTagFor(TransitionType type) => type switch
    {
        TransitionType.None => "None",
        TransitionType.Fade or TransitionType.CrossFade or TransitionType.DipToWhite => "Dissolve",
        TransitionType.SlideLeft or TransitionType.SlideRight or TransitionType.SlideUp or TransitionType.SlideDown => "Slide",
        TransitionType.PushLeft or TransitionType.PushRight or TransitionType.PushUp or TransitionType.PushDown => "Push",
        TransitionType.Wipe or TransitionType.WipeRight or TransitionType.WipeUp or TransitionType.WipeDown => "Wipe",
        TransitionType.ZoomBlur or TransitionType.WhipPanLeft or TransitionType.WhipPanRight or TransitionType.Glitch => "Stylised",
        _ => "Dissolve",
    };

    private static readonly (TransitionType Type, string Label)[] TransitionDissolveVariants =
    [
        (TransitionType.Fade, "Fade"),
        (TransitionType.CrossFade, "Cross Dissolve"),
        (TransitionType.DipToWhite, "Dip to White"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionSlideVariants =
    [
        (TransitionType.SlideLeft, "Slide Left"),
        (TransitionType.SlideRight, "Slide Right"),
        (TransitionType.SlideUp, "Slide Up"),
        (TransitionType.SlideDown, "Slide Down"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionPushVariants =
    [
        (TransitionType.PushLeft, "Push Left"),
        (TransitionType.PushRight, "Push Right"),
        (TransitionType.PushUp, "Push Up"),
        (TransitionType.PushDown, "Push Down"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionWipeVariants =
    [
        (TransitionType.Wipe, "Wipe Left \u2192 Right"),
        (TransitionType.WipeRight, "Wipe Right \u2192 Left"),
        (TransitionType.WipeUp, "Wipe Bottom \u2192 Top"),
        (TransitionType.WipeDown, "Wipe Top \u2192 Bottom"),
    ];

    private static readonly (TransitionType Type, string Label)[] TransitionStylisedVariants =
    [
        (TransitionType.ZoomBlur, "Zoom Blur"),
        (TransitionType.WhipPanLeft, "Whip Pan Left"),
        (TransitionType.WhipPanRight, "Whip Pan Right"),
        (TransitionType.Glitch, "Glitch"),
    ];

    private static (TransitionType Type, string Label)[] TransitionVariantsForFamily(string familyTag) => familyTag switch
    {
        "Dissolve" => TransitionDissolveVariants,
        "Slide" => TransitionSlideVariants,
        "Push" => TransitionPushVariants,
        "Wipe" => TransitionWipeVariants,
        "Stylised" => TransitionStylisedVariants,
        _ => [],
    };

    private static void SelectComboItemByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag as string == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    /// <summary>
    /// Ensures <see cref="TransitionVariantCombo"/> lists <paramref name="familyTag"/>'s variants
    /// (its contents depend on which family is selected, so — unlike the other panes' static
    /// XAML-declared combos — it is populated here) and selects <paramref name="selected"/>,
    /// defaulting to the family's first variant if that type isn't a member of it.
    /// </summary>
    /// <remarks>
    /// <b>The item collection is only rebuilt when it actually differs, and this is load-bearing
    /// rather than an optimisation.</b> Selecting a variant runs
    /// <c>TransitionVariantCombo_SelectionChanged</c> -> <c>UndoRedoManager.Execute</c> ->
    /// <c>OnUndoRedoStateChanged</c> -> <see cref="SyncTransitionUI"/> -> here, and that
    /// dispatcher callback lands while this ComboBox's own dropdown is still open or mid-close
    /// animation. Clearing and refilling <c>Items</c> in that state leaves WinUI unable to
    /// reconcile the live popup against the mutated collection, and it fail-fasts the process
    /// with a stowed <c>E_UNEXPECTED</c> (0x8000FFFF) inside <c>Microsoft.UI.Xaml.dll</c> —
    /// observed as an APPCRASH with exception code 0xC000027B. Picking a *family* never hit it
    /// because the popup that is open then belongs to the other combo.
    /// <para>
    /// So for the overwhelmingly common "same family, different variant" resync the collection
    /// is left completely untouched and only the selection moves, which is safe with the popup
    /// open. The destructive rebuild then only happens on a genuine family change, when this
    /// combo's own dropdown is closed.
    /// </para>
    /// </remarks>
    private void PopulateTransitionVariantCombo(string familyTag, TransitionType selected)
    {
        var variants = TransitionVariantsForFamily(familyTag);

        if (!TransitionVariantComboAlreadyLists(variants))
        {
            TransitionVariantCombo.Items.Clear();
            foreach (var (type, label) in variants)
            {
                // Selection is applied after the collection settles, never mid-population:
                // assigning SelectedItem while Items is still being mutated is the same class
                // of reconciliation hazard described above.
                TransitionVariantCombo.Items.Add(new ComboBoxItem { Content = label, Tag = type.ToString() });
            }
        }

        SelectComboItemByTag(TransitionVariantCombo, selected.ToString());
        if (TransitionVariantCombo.SelectedIndex < 0 && TransitionVariantCombo.Items.Count > 0)
            TransitionVariantCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Whether <see cref="TransitionVariantCombo"/> already lists exactly <paramref name="variants"/>,
    /// in order — i.e. whether a rebuild can be skipped. Compared by the items' <c>Tag</c>
    /// (the <see cref="TransitionType"/> name), which is what selection is driven by.
    /// </summary>
    private bool TransitionVariantComboAlreadyLists((TransitionType Type, string Label)[] variants)
    {
        if (TransitionVariantCombo.Items.Count != variants.Length)
            return false;

        for (int i = 0; i < variants.Length; i++)
        {
            if (TransitionVariantCombo.Items[i] is not ComboBoxItem item ||
                item.Tag as string != variants[i].Type.ToString())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Pushes the boundary's current <see cref="TransitionConfig"/> (or the "Automatic"/unset
    /// state) onto the pane controls, or hides the pane when there is no such boundary, then
    /// reveals it. Mirrors <see cref="SyncTextOverlayUI"/>.
    /// </summary>
    /// <remarks>
    /// Purely reads state onto the controls under <see cref="_suppressTransitionEvents"/> —
    /// it must never itself call <see cref="UndoRedoManager.Execute"/>, or simply selecting an
    /// unconfigured ("Automatic", <c>InTransition == null</c>) boundary would silently turn it
    /// into an explicit config the first time the pane happened to redraw.
    /// </remarks>
    private void SyncTransitionUI(string? incomingSegmentId)
    {
        if (TransitionFamilyCombo is null) return;

        TimelineSegment? incoming = null;
        TimelineSegment? outgoing = null;
        if (incomingSegmentId is not null)
            (incoming, outgoing) = GetTransitionBoundarySegments(incomingSegmentId);

        if (incoming is null || outgoing is null)
        {
            PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Transition, false);
            return;
        }

        _suppressTransitionEvents = true;

        var config = incoming.InTransition;
        string familyTag = config is null ? "Unset" : TransitionFamilyTagFor(config.Type);
        bool hasEffect = familyTag is not ("Unset" or "None");

        SelectComboItemByTag(TransitionFamilyCombo, familyTag);
        TransitionAutomaticHint.Visibility = familyTag == "Unset" ? Visibility.Visible : Visibility.Collapsed;

        TransitionVariantCombo.Visibility = hasEffect ? Visibility.Visible : Visibility.Collapsed;
        if (hasEffect)
            PopulateTransitionVariantCombo(familyTag, config!.Type);

        // Clamp the usable maximum LIVE to half of each neighbour's current duration, exactly
        // as TransitionResolver will clamp the actual dissolve — so the slider can never be
        // dragged to a value that would visibly lie about what will actually play.
        double halfIncoming = incoming.Duration.TotalSeconds / 2.0;
        double halfOutgoing = outgoing.Duration.TotalSeconds / 2.0;
        double clampSeconds = Math.Min(halfIncoming, halfOutgoing);
        double effectiveMax = Math.Min(2.0, clampSeconds);

        // A very short neighbouring segment (TrimSegmentEdgeOperation's own 100ms floor halves
        // to 50ms) can push effectiveMax below what the slider's Minimum can even express.
        // Raising Slider.Maximum back up to Minimum in that case would let the user drag to,
        // and persist, a value the resolver will silently shorten anyway — exactly the
        // "dial lies about what will play" bug the clamp exists to prevent. Disable the
        // slider instead and always state the true effectiveMax in the hint, never a
        // substituted slider maximum.
        bool tooShortForSlider = effectiveMax < TransitionDurationSlider.Minimum;
        double sliderMax = tooShortForSlider ? TransitionDurationSlider.Minimum : effectiveMax;

        TransitionDurationSlider.Maximum = sliderMax;
        bool isClamped = effectiveMax < 2.0;
        TransitionDurationClampHint.Visibility = isClamped ? Visibility.Visible : Visibility.Collapsed;
        if (tooShortForSlider)
        {
            TransitionDurationClampHint.Text = string.Format(CultureInfo.InvariantCulture,
                "This boundary is too short for an adjustable transition — a neighbouring segment limits it to {0:0.00}s, and it will render at exactly that length regardless of the value shown above.",
                effectiveMax);
        }
        else if (isClamped)
        {
            TransitionDurationClampHint.Text = string.Format(CultureInfo.InvariantCulture,
                "Limited to {0:0.00}s by a neighbouring segment's length — dragging further will not make the dissolve any longer.",
                effectiveMax);
        }

        double durationSeconds = config?.Duration.TotalSeconds ?? 0.5;
        TransitionDurationSlider.Value = Math.Clamp(durationSeconds, TransitionDurationSlider.Minimum, sliderMax);
        TransitionDurationSlider.IsEnabled = hasEffect && !tooShortForSlider;

        SelectComboItemByTag(TransitionEasingCombo, (config?.Easing ?? TransitionEasing.EaseInOut).ToString());
        TransitionEasingCombo.IsEnabled = hasEffect;

        RemoveTransitionButton.IsEnabled = config is not null;

        _suppressTransitionEvents = false;

        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.Transition, true);
        PropertiesPanel.ShowPane(PropertyPaneKind.Transition);
    }

    private void TransitionFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;
        if (TransitionFamilyCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string familyTag) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming is null) return;

        TransitionConfig? newConfig = familyTag switch
        {
            "Unset" => null,
            "None" => (incoming.InTransition ?? new TransitionConfig()) with { Type = TransitionType.None },
            _ => ApplyTransitionFamilyDefaultVariant(incoming.InTransition, familyTag),
        };

        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Type"));
        SyncTransitionUI(id);
        InvalidatePreview();
    }

    private static TransitionConfig ApplyTransitionFamilyDefaultVariant(TransitionConfig? existing, string familyTag)
    {
        var variants = TransitionVariantsForFamily(familyTag);
        var type = variants.Length > 0 ? variants[0].Type : TransitionType.None;
        return existing is null ? new TransitionConfig { Type = type } : existing with { Type = type };
    }

    private void TransitionVariantCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;
        if (TransitionVariantCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string typeName) return;
        if (!Enum.TryParse<TransitionType>(typeName, out var type)) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming?.InTransition is null) return;

        var newConfig = incoming.InTransition with { Type = type };
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Variant"));
        InvalidatePreview();
    }

    private void TransitionDurationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming?.InTransition is null) return;

        var newConfig = incoming.InTransition with { Duration = TimeSpan.FromSeconds(e.NewValue) };
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Duration"));
        InvalidatePreview();
    }

    private void TransitionEasingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTransitionEvents) return;
        if (_selectedTransitionId is not { } id) return;
        if (TransitionEasingCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string easingName) return;
        if (!Enum.TryParse<TransitionEasing>(easingName, out var easing)) return;

        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming?.InTransition is null) return;

        var newConfig = incoming.InTransition with { Easing = easing };
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(id, newConfig, "Change Transition Easing"));
        InvalidatePreview();
    }

    /// <summary>
    /// Applies the selected boundary's current config (which may legitimately be "Automatic" /
    /// null, clearing every other boundary) to every boundary on the timeline, as a single undo
    /// entry — see <see cref="ApplyTransitionToAllBoundariesOperation"/>.
    /// </summary>
    private void ApplyTransitionToAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTransitionId is not { } id) return;
        var (incoming, _) = GetTransitionBoundarySegments(id);
        if (incoming is null) return;

        var config = incoming.InTransition;
        ViewModel.UndoRedoManager.Execute(
            new ApplyTransitionToAllBoundariesOperation(config, "Apply Transition to All Boundaries"));
        InvalidatePreview();
    }

    private void RemoveTransitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTransitionId is { } id)
            DeleteTransition(id);
    }

    /// <summary>Clears a boundary's transition back to "Automatic" (null), keeping it selected
    /// so the pane re-syncs to show the now-unconfigured state rather than closing.</summary>
    private void DeleteTransition(string incomingSegmentId)
    {
        ViewModel.UndoRedoManager.Execute(new UpdateTransitionOperation(incomingSegmentId, null, "Remove Transition"));
        if (_selectedTransitionId == incomingSegmentId)
            SyncTransitionUI(incomingSegmentId);
        InvalidatePreview();
    }

    /// <summary>
    /// Re-syncs every renderer's overlay list, then forces a preview re-render at the current
    /// playhead. <see cref="UndoRedoManager"/>'s state-changed event already drives the same
    /// re-sync through <see cref="InvalidatePreview"/>, but that path is skipped whenever
    /// <c>_compositorReady</c> is false or a segment renderer hasn't been (re)built yet, so this
    /// explicit call guarantees adding/editing/removing/undoing an overlay updates the preview
    /// immediately rather than only after a full renderer rebuild — property edits mutate the
    /// live segment in place, so a compositor that already has the list synced (see
    /// <see cref="SyncTextOverlaysToRenderer"/>) would pick the change up on its own next
    /// repaint anyway, but add/remove need the list itself re-fetched.
    /// </summary>
    private void RefreshOverlayPreview()
    {
        if (_previewRenderer is not null)
        {
            SyncTextOverlaysToRenderer(_previewRenderer, null);
        }
        foreach (var (segmentId, ctx) in _segmentPreviews)
        {
            if (ctx.Renderer is null) continue;
            var seg = ViewModel.Model.Segments.OfType<VideoSegment>().FirstOrDefault(v => v.Id == segmentId);
            if (seg is null) continue;
            SyncTextOverlaysToRenderer(ctx.Renderer, seg.VideoFilePath);
        }

        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    /// <summary>
    /// <see cref="RefreshOverlayPreview"/> plus a timeline canvas redraw, for edits that also
    /// change what a track renders on the timeline itself (a preset swap, duration change, or
    /// enabled/disabled toggle all alter the overlay's on-track label/extent). Most overlay
    /// property edits (color, font, position, blur, etc.) do NOT affect the track's rendered
    /// label, so they call <see cref="RefreshOverlayPreview"/> alone — do not add a timeline
    /// redraw to those without confirming the track visual actually changed.
    /// </summary>
    private void RefreshOverlayPreviewAndTimeline()
    {
        Timeline.InvalidateAllCanvases();
        RefreshOverlayPreview();
    }

    /// <summary>
    /// Debounces <see cref="RefreshOverlayPreview"/> for the high-frequency
    /// <see cref="OverlayTextBox_TextChanged"/> handler: the model is committed on every
    /// keystroke (so undo/redo and export always see the latest text), but repainting the
    /// preview on every keystroke would be wasteful, so only that part is deferred — mirrors
    /// <see cref="ScheduleMotionPreviewRebuild"/>'s split between an immediate model commit
    /// and a debounced preview rebuild.
    /// </summary>
    private void ScheduleOverlayPreviewRefresh()
    {
        _overlayPreviewDebouncer ??= new Debouncer(RefreshOverlayPreview);
        _overlayPreviewDebouncer.Schedule();
    }

    private void OverlayPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayPresets.SelectedItem is not Border { Tag: string presetName }) return;

        var preset = TextOverlayPresets.ByName(presetName);
        if (preset is null) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, preset.Apply, $"Apply {preset.Name} Preset"));

        // Re-sync every control (anchor grid, background sub-panels, etc.) so the pane
        // reflects the preset's full style rather than just the fields a targeted handler
        // would have touched. SyncTextOverlayUI clears OverlayPresets.SelectedItem again,
        // which is why this handler must be re-entrancy-guarded above.
        SyncTextOverlayUI(id);
        RefreshOverlayPreviewAndTimeline();
    }

    private void OverlayTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var text = OverlayTextBox.Text;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Text = text, "Change Overlay Text"));

        Timeline.InvalidateAllCanvases();
        ScheduleOverlayPreviewRefresh();
    }

    private void OverlayAnimationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayAnimationCombo.SelectedItem is not ComboBoxItem item) return;
        if (!Enum.TryParse<TextSlideAnimation>(item.Tag?.ToString(), out var anim)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Animation = anim, "Change Overlay Animation"));
        RefreshOverlayPreview();
    }

    private void OverlayFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayFontCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string font ||
            string.IsNullOrWhiteSpace(font)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.FontFamily = font, "Change Overlay Font"));
        RefreshOverlayPreview();
    }

    private void OverlayDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (double.IsNaN(args.NewValue)) return;

        double seconds = Math.Max(args.NewValue, TrimTextOverlayOperation.MinDuration.TotalSeconds);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Duration = TimeSpan.FromSeconds(seconds), "Change Overlay Duration"));

        RefreshOverlayPreviewAndTimeline();
    }

    private void OverlayFontSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (double.IsNaN(args.NewValue)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.FontSize = args.NewValue, "Change Overlay Font Size"));
        RefreshOverlayPreview();
    }

    private void OverlayTextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.TextColor = hex, "Change Overlay Text Color"));

        OverlayTextColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayTextColorText.Text = hex;
        RefreshOverlayPreview();
    }

    private void OverlayBold_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        bool isBold = OverlayBoldToggle.IsChecked == true;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.IsBold = isBold, "Change Overlay Bold"));
        RefreshOverlayPreview();
    }

    private void OverlayItalic_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        bool isItalic = OverlayItalicToggle.IsChecked == true;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.IsItalic = isItalic, "Change Overlay Italic"));
        RefreshOverlayPreview();
    }

    private void OverlayAlignSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayAlignSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item ||
            !Enum.TryParse<SlideTextAlignment>(item.Tag?.ToString(), out var align)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.TextAlignment = align, "Change Overlay Alignment"));
        RefreshOverlayPreview();
    }

    private void OverlayAnchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<TextOverlayAnchor>(tag, out var anchor)) return;

        var overlay = SelectedTextOverlay();
        if (overlay is null) return;

        // The box is an explicit rectangle now, so the anchor's true centre is exact
        // arithmetic over the authored width/height — no glyph measurement or estimate
        // needed. ResolveCenter ignores the passed-in X/Y for every non-Custom anchor, so
        // this stored value only matters once the user drags the overlay back to Custom,
        // where it gives it the right starting point.
        var (x, y) = TextOverlaySegment.ResolveCenter(
            anchor, overlay.X, overlay.Y, overlay.MarginFraction,
            overlay.WidthFraction, overlay.HeightFraction);

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(id, o =>
        {
            o.Anchor = anchor;
            o.X = x;
            o.Y = y;
        }, "Change Overlay Anchor"));

        OverlayCustomPositionHint.Visibility = Visibility.Collapsed;
        RefreshOverlayPreview();
    }

    private void OverlayWidthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double fraction = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.WidthFraction = fraction, "Change Overlay Width"));
        RefreshOverlayPreview();
    }

    private void OverlayHeightSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double fraction = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.HeightFraction = fraction, "Change Overlay Height"));
        RefreshOverlayPreview();
    }

    private void OverlayMarginSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double fraction = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.MarginFraction = fraction, "Change Overlay Margin"));
        RefreshOverlayPreview();
    }

    private void OverlayBgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayBgTypeCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<TextOverlayBackground>(item.Tag?.ToString(), out var background)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Background = background, "Change Overlay Background Type"));

        UpdateOverlayBackgroundPanels(background);
        RefreshOverlayPreview();
    }

    private void OverlayBgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.BackgroundColor = hex, "Change Overlay Background Color"));

        OverlayBgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayBgColorText.Text = hex;
        RefreshOverlayPreview();
    }

    /// <summary>
    /// Shared by <see cref="OverlayBgOpacitySlider"/>, <see cref="OverlayCornerRadiusSlider"/>
    /// and <see cref="OverlayPaddingSlider"/> — all three live in <see cref="OverlayBoxPanel"/>
    /// and drive the box's style regardless of which background type is selected (mirrors how
    /// Scene's <c>StyleSlider_ValueChanged</c> is shared by several sliders).
    /// </summary>
    private void OverlayBoxSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double value = e.NewValue;
        if (ReferenceEquals(sender, OverlayBgOpacitySlider))
        {
            double opacity = value / 100.0;
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.BackgroundOpacity = opacity, "Change Overlay Background Opacity"));
        }
        else if (ReferenceEquals(sender, OverlayCornerRadiusSlider))
        {
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.CornerRadius = value, "Change Overlay Corner Radius"));
        }
        else if (ReferenceEquals(sender, OverlayPaddingSlider))
        {
            double padding = value / 100.0;
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.PaddingScale = padding, "Change Overlay Padding"));
        }
        else
        {
            return;
        }

        RefreshOverlayPreview();
    }

    /// <summary>Shared by <see cref="OverlayBlurAmountSlider"/> and <see cref="OverlayBlurTintSlider"/>.</summary>
    private void OverlayBlurSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double value = e.NewValue;
        if (ReferenceEquals(sender, OverlayBlurAmountSlider))
        {
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.BlurAmount = value, "Change Overlay Blur Amount"));
        }
        else if (ReferenceEquals(sender, OverlayBlurTintSlider))
        {
            double tint = value / 100.0;
            ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                id, o => o.BlurTintOpacity = tint, "Change Overlay Blur Tint"));
        }
        else
        {
            return;
        }

        RefreshOverlayPreview();
    }

    private void OverlayScrimDirectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayScrimDirectionCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<ScrimDirection>(item.Tag?.ToString(), out var direction)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.ScrimDirection = direction, "Change Overlay Scrim Direction"));
        RefreshOverlayPreview();
    }

    private void OverlayScrimStrengthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double strength = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.ScrimStrength = strength, "Change Overlay Scrim Strength"));
        RefreshOverlayPreview();
    }

    private void OverlayOutlineWidthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double width = e.NewValue;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.OutlineWidth = width, "Change Overlay Outline Width"));
        RefreshOverlayPreview();
    }

    private void OverlayOutlineColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.OutlineColor = hex, "Change Overlay Outline Color"));

        OverlayOutlineColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayOutlineColorText.Text = hex;
        RefreshOverlayPreview();
    }

    private void OverlayShadowStrengthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double strength = e.NewValue / 100.0;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.ShadowStrength = strength, "Change Overlay Shadow Strength"));
        RefreshOverlayPreview();
    }

    private void OverlayAccentColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        var hex = ColorToHex(args.NewColor);
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.AccentColor = hex, "Change Overlay Accent Color"));

        OverlayAccentColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        OverlayAccentColorText.Text = hex;
        RefreshOverlayPreview();
    }

    private void OverlayAccentThicknessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        double thickness = e.NewValue;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.AccentThickness = thickness, "Change Overlay Accent Thickness"));
        RefreshOverlayPreview();
    }

    private void OverlayAccentSideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;
        if (OverlayAccentSideCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<AccentSide>(item.Tag?.ToString(), out var side)) return;

        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.AccentSide = side, "Change Overlay Accent Side"));
        RefreshOverlayPreview();
    }

    private void OverlayEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;
        if (_selectedTextOverlayId is not { } id) return;

        bool enabled = OverlayEnabledToggle.IsOn;
        ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
            id, o => o.Enabled = enabled, "Change Overlay Enabled"));

        RefreshOverlayPreviewAndTimeline();
    }

    /// <summary>
    /// Selects the combo item matching <paramref name="fontFamily"/>; if the overlay uses a
    /// font that isn't in the curated list, it is inserted at the top so the actual font is
    /// represented (and not silently changed). Mirrors <see cref="SetSlideFontSelection"/>.
    /// </summary>
    private void SetOverlayFontSelection(string fontFamily)
    {
        for (int i = 0; i < OverlayFontCombo.Items.Count; i++)
        {
            if (OverlayFontCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), fontFamily, StringComparison.OrdinalIgnoreCase))
            {
                OverlayFontCombo.SelectedIndex = i;
                return;
            }
        }

        var custom = new ComboBoxItem { Content = fontFamily, Tag = fontFamily };
        try { custom.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontFamily); }
        catch { /* unknown family — fall back to default rendering */ }
        OverlayFontCombo.Items.Insert(0, custom);
        OverlayFontCombo.SelectedIndex = 0;
    }

    private bool _overlayPresetsBuilt;

    /// <summary>
    /// Populates <see cref="OverlayPresets"/> from <see cref="TextOverlayPresets.All"/>, one
    /// tile per preset showing its glyph and name — mirrors <see cref="BuildGradientPresetsIfNeeded"/>'s
    /// tile-construction approach for the text slide pane's gradient presets.
    /// </summary>
    private void BuildOverlayPresetsIfNeeded()
    {
        if (_overlayPresetsBuilt) return;
        _overlayPresetsBuilt = true;

        foreach (var preset in TextOverlayPresets.All)
        {
            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = preset.Glyph,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = preset.Name,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var tile = new Border
            {
                Width = 84,
                Height = 64,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Tag = preset.Name,
                Child = stack,
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(tile, preset.Name);
            OverlayPresets.Items.Add(tile);
        }
    }

    private void RemoveTextSlide_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTextSlideId is null) return;
        var operation = new RemoveSegmentOperation(_selectedTextSlideId);
        ViewModel.UndoRedoManager.Execute(operation);
        _selectedTextSlideId = null;
        HideTextSlidePanel();
        Timeline.InvalidateAllCanvases();
    }

    private void SlideTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedTextSlideId is null) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;
        slide.Text = SlideTextBox.Text;
        RefreshSlidePreview();
    }

    private void SlideAnimationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null ||
            SlideAnimationCombo.SelectedItem is not ComboBoxItem item) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        if (Enum.TryParse<TextSlideAnimation>(item.Tag?.ToString(), out var anim))
            slide.Animation = anim;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void SlideDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null || double.IsNaN(args.NewValue)) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        slide.Duration = TimeSpan.FromSeconds(args.NewValue);
        ViewModel.Model.RecalculateSegmentPositions();
        SyncTextSlideWindowControls(slide);
        Timeline.InvalidateAllCanvases();
        InvalidatePreview();
    }

    /// <summary>
    /// Debounces the animation-window sliders. They emit a tick per <c>StepFrequency</c> step,
    /// so committing on every tick would push dozens of operations onto the undo stack for a
    /// single thumb drag (making one drag take dozens of Ctrl+Z to reverse, and clearing the
    /// redo stack each time) and fire an un-debounced forced recompose on the UI thread.
    /// Matches how every other continuous control here behaves (<c>_cursorDebouncer</c>,
    /// <c>_motionDebouncer</c>, <c>_styleDebouncer</c>).
    /// </summary>
    private void ScheduleSlideTextWindowCommit()
    {
        _slideTextWindowDebouncer ??= new Debouncer(CommitSlideTextWindowFromControls);
        _slideTextWindowDebouncer.Schedule();
    }

    private void SlideTextWindowSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null || double.IsNaN(args.NewValue)) return;
        if (PropertiesPanel is null || PropertiesPanel.TextSlide is null) return;

        ScheduleSlideTextWindowCommit();
    }

    private void SlideTextRampSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null || double.IsNaN(args.NewValue)) return;
        if (PropertiesPanel is null || PropertiesPanel.TextSlide is null) return;

        ScheduleSlideTextWindowCommit();
    }

    /// <summary>
    /// Reads all four animation-window sliders and commits them as ONE undoable edit. Values
    /// are re-read here rather than captured at schedule time so a burst of ticks that
    /// coalesced into a single commit always reflects the newest thumb positions.
    /// </summary>
    private void CommitSlideTextWindowFromControls()
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null) return;
        if (PropertiesPanel is null || PropertiesPanel.TextSlide is null) return;

        var slide = SelectedSlide();
        if (slide is null) return;

        var pane = PropertiesPanel.TextSlide;
        if (pane.SlideTextInAtSlider is null || pane.SlideTextOutBySlider is null ||
            pane.SlideTextInRampSlider is null || pane.SlideTextOutRampSlider is null)
        {
            return;
        }

        var inStart = TimeSpan.FromSeconds(Math.Max(0, pane.SlideTextInAtSlider.Value));
        var outEnd = TimeSpan.FromSeconds(Math.Max(0, pane.SlideTextOutBySlider.Value));
        var inDuration = TimeSpan.FromSeconds(Math.Max(0, pane.SlideTextInRampSlider.Value));
        var outDuration = TimeSpan.FromSeconds(Math.Max(0, pane.SlideTextOutRampSlider.Value));

        // One operation carries both the window and the ramps, so a drag that touched either
        // is a single undo step.
        ViewModel.UndoRedoManager.Execute(new UpdateTextSlideOperation(
            slide.Id,
            slide.Text, slide.FontFamily, slide.FontSize,
            slide.IsBold, slide.IsItalic,
            slide.TextColor, slide.BackgroundColor,
            slide.Duration, slide.Animation,
            inStart, inDuration,
            outEnd, outDuration));

        SyncTextSlideWindowControls(slide);
        RefreshSlidePreview();
    }

    private void SlideFontSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null || double.IsNaN(args.NewValue)) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;
        slide.FontSize = args.NewValue;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private bool _suppressSlideEvents;

    private void ShowTextSlidePanel(TextSlideSegment slide)
    {
        if (PropertiesPanel is null) return;

        _suppressSlideEvents = true;

        PropertiesPanel.SetPaneAvailable(PropertyPaneKind.TextSlide, true);

        SlideTextBox.Text = slide.Text;
        SlideDurationBox.Value = slide.Duration.TotalSeconds;
        SlideFontSizeBox.Value = slide.FontSize;
        SyncTextSlideWindowControls(slide);

        // Formatting toggles
        SlideBoldToggle.IsChecked = slide.IsBold;
        SlideItalicToggle.IsChecked = slide.IsItalic;
        SlideAlignSegmented.SelectedIndex = slide.TextAlignment switch
        {
            SlideTextAlignment.Left => 0,
            SlideTextAlignment.Right => 2,
            _ => 1,
        };

        // Font dropdown
        SetSlideFontSelection(slide.FontFamily);

        // Set animation combo
        var animName = slide.Animation.ToString();
        for (int i = 0; i < SlideAnimationCombo.Items.Count; i++)
        {
            if (SlideAnimationCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == animName)
            {
                SlideAnimationCombo.SelectedIndex = i;
                break;
            }
        }

        // Color swatches + pickers
        UpdateSlideColorSwatch(SlideTextColorSwatch, SlideTextColorText, SlideTextColorPicker, slide.TextColor);
        UpdateSlideColorSwatch(SlideBgColorSwatch, SlideBgColorText, SlideBgColorPicker, slide.BackgroundColor);
        UpdateSlideColorSwatch(SlideGradEndColorSwatch, SlideGradEndColorText, SlideGradEndColorPicker, slide.GradientEndColor);
        SlideGradAngleSlider.Value = slide.GradientAngle;

        // Background type combo
        var bgTypeName = slide.BackgroundType.ToString();
        for (int i = 0; i < SlideBgTypeCombo.Items.Count; i++)
        {
            if (SlideBgTypeCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == bgTypeName)
            {
                SlideBgTypeCombo.SelectedIndex = i;
                break;
            }
        }

        SlideImagePathText.Text = string.IsNullOrEmpty(slide.BackgroundImagePath)
            ? "No image selected" : System.IO.Path.GetFileName(slide.BackgroundImagePath);

        BuildGradientPresetsIfNeeded();
        UpdateSlideBgPanels(slide.BackgroundType);

        _suppressSlideEvents = false;

        // Reveal the text slide panel so properties are immediately editable on selection.
        PropertiesPanel.ShowPane(PropertyPaneKind.TextSlide);
    }

    private void SyncTextSlideWindowControls(TextSlideSegment slide)
    {
        if (PropertiesPanel is null || PropertiesPanel.TextSlide is null) return;

        var pane = PropertiesPanel.TextSlide;
        if (pane.SlideTextInAtSlider is null ||
            pane.SlideTextOutBySlider is null ||
            pane.SlideTextInRampSlider is null ||
            pane.SlideTextOutRampSlider is null)
        {
            return;
        }

        var wasSuppressed = _suppressSlideEvents;
        _suppressSlideEvents = true;
        try
        {
            // A Slider with Maximum == Minimum is degenerate (the thumb cannot move and the
            // control reports 0), so a zero-length slide still gets a usable range. Maximum
            // is assigned before Value because the Slider clamps Value into the range.
            var max = Math.Max(0.1, slide.Duration.TotalSeconds);
            pane.SlideTextInAtSlider.Maximum = max;
            pane.SlideTextOutBySlider.Maximum = max;
            pane.SlideTextInRampSlider.Maximum = max;
            pane.SlideTextOutRampSlider.Maximum = max;
            pane.SlideTextInAtSlider.Value = slide.ResolveTextInStart().TotalSeconds;
            pane.SlideTextOutBySlider.Value = slide.ResolveTextOutEnd().TotalSeconds;
            pane.SlideTextInRampSlider.Value = slide.ResolveTextInDuration().TotalSeconds;
            pane.SlideTextOutRampSlider.Value = slide.ResolveTextOutDuration().TotalSeconds;
        }
        finally
        {
            _suppressSlideEvents = wasSuppressed;
        }
    }

    private void HideTextSlidePanel()
    {
        PropertiesPanel?.SetPaneAvailable(PropertyPaneKind.TextSlide, false);
    }

    private static void UpdateSlideColorSwatch(
        Border swatch, TextBlock text, ColorPicker picker, string hex)
    {
        var color = ParseHexColor(hex);
        swatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        text.Text = hex;
        picker.Color = color;
    }

    private void SlideTextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        slide.TextColor = ColorToHex(args.NewColor);
        SlideTextColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        SlideTextColorText.Text = slide.TextColor;
        RefreshSlidePreview();
    }

    private void SlideBgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressSlideEvents || _selectedTextSlideId is null) return;
        var slide = ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);
        if (slide is null) return;

        slide.BackgroundColor = ColorToHex(args.NewColor);
        SlideBgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        SlideBgColorText.Text = slide.BackgroundColor;
        RefreshSlidePreview();
    }

    private TextSlideSegment? SelectedSlide() =>
        _selectedTextSlideId is null ? null : ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _selectedTextSlideId);

    private void SlideBold_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        slide.IsBold = SlideBoldToggle.IsChecked == true;
        RefreshSlidePreview();
    }

    private void SlideItalic_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        slide.IsItalic = SlideItalicToggle.IsChecked == true;
        RefreshSlidePreview();
    }

    private void SlideAlignSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        if (SlideAlignSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem item
            && Enum.TryParse<SlideTextAlignment>(item.Tag?.ToString(), out var align))
        {
            slide.TextAlignment = align;
            RefreshSlidePreview();
        }
    }

    private void SlideFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;
        if (SlideFontCombo.SelectedItem is ComboBoxItem item && item.Tag is string font
            && !string.IsNullOrWhiteSpace(font))
        {
            slide.FontFamily = font;
            RefreshSlidePreview();
        }
    }

    /// <summary>
    /// Selects the combo item matching <paramref name="fontFamily"/>; if the slide
    /// uses a font that isn't in the curated list, it is inserted at the top so the
    /// actual font is represented (and not silently changed).
    /// </summary>
    private void SetSlideFontSelection(string fontFamily)
    {
        for (int i = 0; i < SlideFontCombo.Items.Count; i++)
        {
            if (SlideFontCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), fontFamily, StringComparison.OrdinalIgnoreCase))
            {
                SlideFontCombo.SelectedIndex = i;
                return;
            }
        }

        var custom = new ComboBoxItem { Content = fontFamily, Tag = fontFamily };
        try { custom.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(fontFamily); }
        catch { /* unknown family — fall back to default rendering */ }
        SlideFontCombo.Items.Insert(0, custom);
        SlideFontCombo.SelectedIndex = 0;
    }

    private void RefreshSlidePreview()
    {
        // Slide content/style just changed. This used to also release a page-owned
        // crossfade-outgoing cache composed from the pre-edit slide (T3 retired that cache:
        // under the rolling transition model the outgoing side composes a different image on
        // almost every tick, so a single cached frame no longer helps — see the remarks on
        // ComposePreviewFrameAtOffsetAsync). There is nothing left to invalidate here; the next
        // dissolve tick simply recomposes from the now-current slide state.
        // Several slide property handlers mutate the segment directly instead of going
        // through UndoRedoManager, so the edit signal that normally rides on it never fires
        // for them. This is the one call they all share.
        ProjectService.Instance.MarkDirty();

        Timeline.InvalidateAllCanvases();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void UpdateSlideBgPanels(SlideBackgroundType type)
    {
        SlideColorPanel.Visibility = type is SlideBackgroundType.Solid or SlideBackgroundType.Gradient
            ? Visibility.Visible : Visibility.Collapsed;
        SlideColorLabel.Text = type == SlideBackgroundType.Gradient ? "Start Color" : "Color";
        SlideGradientPanel.Visibility = type == SlideBackgroundType.Gradient ? Visibility.Visible : Visibility.Collapsed;
        SlideImagePanel.Visibility = type == SlideBackgroundType.Image ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SlideBgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents || SlideBgTypeCombo.SelectedItem is not ComboBoxItem item) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        if (Enum.TryParse<SlideBackgroundType>(item.Tag?.ToString(), out var type))
        {
            slide.BackgroundType = type;
            UpdateSlideBgPanels(type);
            RefreshSlidePreview();
        }
    }

    private void SlideGradEndColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        slide.GradientEndColor = ColorToHex(args.NewColor);
        SlideGradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        SlideGradEndColorText.Text = slide.GradientEndColor;
        RefreshSlidePreview();
    }

    private void SlideGradAngleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        slide.GradientAngle = e.NewValue;
        RefreshSlidePreview();
    }

    private bool _slideGradientPresetsBuilt;

    private void BuildGradientPresetsIfNeeded()
    {
        if (_slideGradientPresetsBuilt) return;
        _slideGradientPresetsBuilt = true;

        foreach (var preset in Musio.Core.Settings.DefaultBrandPresets.All)
        {
            var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            { Color = ParseHexColor(preset.BackgroundColor), Offset = 0 });
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            { Color = ParseHexColor(preset.GradientEndColor), Offset = 1 });

            var tile = new Border
            {
                Width = 56,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Background = brush,
                Tag = preset,
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(tile, preset.Name);
            SlideGradientPresets.Items.Add(tile);
        }
    }

    private void SlideGradientPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlideEvents) return;
        if (SlideGradientPresets.SelectedItem is not Border { Tag: Musio.Core.Settings.BrandPreset preset }) return;
        var slide = SelectedSlide();
        if (slide is null) return;

        _suppressSlideEvents = true;
        slide.BackgroundColor = preset.BackgroundColor;
        slide.GradientEndColor = preset.GradientEndColor;
        slide.GradientAngle = preset.GradientAngle;

        UpdateSlideColorSwatch(SlideBgColorSwatch, SlideBgColorText, SlideBgColorPicker, slide.BackgroundColor);
        UpdateSlideColorSwatch(SlideGradEndColorSwatch, SlideGradEndColorText, SlideGradEndColorPicker, slide.GradientEndColor);
        SlideGradAngleSlider.Value = slide.GradientAngle;
        _suppressSlideEvents = false;

        RefreshSlidePreview();
    }

    private async void ChooseSlideImage_Click(object sender, RoutedEventArgs e)
    {
        var slide = SelectedSlide();
        if (slide is null) return;

        Windows.Storage.StorageFile? file = null;
        try
        {
            file = await PickerHelper.PickSingleFileAsync(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                new[] { ".jpg", ".jpeg", ".png", ".bmp" },
                Windows.Storage.Pickers.PickerViewMode.Thumbnail);
        }
        catch { }
        if (file is null) return;

        slide.BackgroundImagePath = file.Path;
        SlideImagePathText.Text = System.IO.Path.GetFileName(file.Path);
        RefreshSlidePreview();
    }

    // ─── In-preview text editing & repositioning ────────────────────────
    //
    // Both full-screen text slides and text overlays support the same two preview
    // gestures — drag-to-reposition and double-click-to-edit — via ITextEditTarget, a
    // small adapter that lets the shared gesture code below (originally written only for
    // TextSlideSegment) work against either kind of segment without duplicating it. The
    // two kinds differ only in how a gesture is *persisted* (see SlideTextEditTarget vs.
    // OverlayTextEditTarget): slides mutate the live segment directly (no undo, matching
    // their pre-existing behaviour); overlays commit exactly once per gesture through
    // UpdateTextOverlayPropertiesOperation so drags/edits are each a single undo step.

    private TextSlideSegment? PreviewSlide() =>
        _previewSlideId is null ? null : ViewModel.Model.Segments
            .OfType<TextSlideSegment>()
            .FirstOrDefault(s => s.Id == _previewSlideId);

    private TextOverlaySegment? PreviewOverlay() =>
        _previewOverlayId is null ? null : ViewModel.Model.TextOverlays
            .FirstOrDefault(o => o.Id == _previewOverlayId);

    /// <summary>
    /// Resolves whichever segment the shared text-edit canvas should currently track: the
    /// previewed slide if one is showing, otherwise the previewed overlay. A slide always
    /// wins — the render pipeline never sets both _previewSlideId and _previewOverlayId at
    /// once (see RenderFrameAtAsync/RenderTextSlidePreviewAsync/UpdateOverlayEditPreview),
    /// but the priority keeps the two concerns cleanly separated regardless.
    ///
    /// Called every drawn frame (Preview.FrameLayoutChanged / ShowTextEditOverlay), so this
    /// reuses the cached target instance (see _cachedEditTarget) instead of re-running the
    /// PreviewSlide()/PreviewOverlay() LINQ lookups and allocating a fresh adapter each
    /// time — it trusts _previewSlideId/_previewOverlayId directly (both are recomputed
    /// every render, see RenderFrameAtAsync/UpdateOverlayEditPreview) and only rebuilds the
    /// cached instance when the id (or slide-vs-overlay kind) actually changes. A gesture
    /// that is just starting must NOT use this cached instance — see
    /// GetActiveEditTargetForNewGesture.
    /// </summary>
    private ITextEditTarget? GetActiveEditTarget()
    {
        if (_previewSlideId is { } slideId)
        {
            if (_cachedEditTarget is not SlideTextEditTarget || _cachedEditTargetId != slideId)
                _cachedEditTarget = new SlideTextEditTarget(this, slideId);
            _cachedEditTargetId = slideId;
            return _cachedEditTarget;
        }

        if (_previewOverlayId is { } overlayId)
        {
            if (_cachedEditTarget is not OverlayTextEditTarget || _cachedEditTargetId != overlayId)
                _cachedEditTarget = new OverlayTextEditTarget(this, overlayId);
            _cachedEditTargetId = overlayId;
            return _cachedEditTarget;
        }

        _cachedEditTarget = null;
        _cachedEditTargetId = null;
        return null;
    }

    /// <summary>
    /// Constructs a brand-new edit-target adapter for whatever <see cref="GetActiveEditTarget"/>
    /// currently resolves to, WITHOUT touching the per-frame cache above. Used only at the
    /// moment a gesture begins (<see cref="TextEditRegion_PointerPressed"/>/
    /// <see cref="EnterTextEdit"/>) — <see cref="OverlayTextEditTarget"/> captures its
    /// pre-gesture original text/position/anchor at construction time, so reusing the
    /// per-frame cached instance here could restore a stale "original" (e.g. if the
    /// properties pane changed the overlay's position/anchor since the cache was last
    /// (re)built) instead of what is actually in effect right now.
    /// </summary>
    private ITextEditTarget? GetActiveEditTargetForNewGesture()
    {
        if (PreviewSlide() is { } slide) return new SlideTextEditTarget(this, slide.Id);
        if (PreviewOverlay() is { } overlay) return new OverlayTextEditTarget(this, overlay.Id);
        return null;
    }

    /// <summary>
    /// Recomputes which text overlay (if any) is under the playhead and should show the
    /// shared drag/edit region: the *selected* overlay, but only while the playhead's
    /// source time actually falls inside its active range and it belongs to the source
    /// video on screen (mirrors <see cref="TimelineModel.GetActiveTextOverlays"/>'s
    /// per-recording ownership rule) — otherwise the region would let you drag an overlay
    /// that isn't actually visible. Called once per rendered video frame (see
    /// RenderFrameAtAsync's VideoSegment/legacy branches).
    /// </summary>
    private void UpdateOverlayEditPreview(string? videoFilePath, TimeSpan sourceTime)
    {
        _previewOverlayId = null;
        if (_selectedTextOverlayId is { } selectedId
            && ViewModel.Model.GetActiveTextOverlays(sourceTime, videoFilePath).Any(o => o.Id == selectedId))
        {
            _previewOverlayId = selectedId;
        }

        var (w, h) = GetPreviewCanvasSize();
        _previewFrameW = w;
        _previewFrameH = h;

        // Handles both cases: shows+positions the region when _previewOverlayId (above)
        // resolved to something, or hides it when it didn't.
        ShowTextEditOverlay();
    }

    /// <summary>Commits an in-progress WYSIWYG text edit, if any, without touching the
    /// shared canvas's Visibility — used where the caller will immediately decide the
    /// canvas's Show/Hide state itself (see UpdateOverlayEditPreview).</summary>
    private void CommitActiveTextEditIfAny()
    {
        if (_editingTextId is not null)
            CommitTextEdit();
    }

    /// <summary>
    /// Shows and positions the shared text-edit overlay over whatever <see cref="GetActiveEditTarget"/>
    /// currently resolves to. Hidden during playback or zoom-region edit mode. Safe to call
    /// from any thread.
    /// </summary>
    private void ShowTextEditOverlay()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ShowTextEditOverlay);
            return;
        }

        if (Preview.IsPlaying || _zoomRegionEditMode || GetActiveEditTarget() is not { } target)
        {
            HideTextEditOverlay();
            return;
        }

        TextEditCanvas.Visibility = Visibility.Visible;
        PositionTextEditControls(target);
    }

    private void HideTextEditOverlay()
    {
        if (TextEditCanvas is null) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(HideTextEditOverlay);
            return;
        }
        CommitActiveTextEditIfAny();
        FinalizeTextBoxResize();
        TextEditCanvas.Visibility = Visibility.Collapsed;
    }

    // Last text styling pushed onto the in-place editor. PositionTextEditControls runs
    // from Preview.FrameLayoutChanged, which fires inside the Win2D draw callback, so
    // assigning a fresh FontFamily/SolidColorBrush there allocated new XAML+COM objects on
    // every drawn frame and dirtied text layout each time. One cache serves both slide and
    // overlay targets since it's keyed on the resolved style values, not the target kind.
    private (string Family, double Size, bool Bold, bool Italic, string Color, SlideTextAlignment Align)? _textEditStyle;

    private void PositionTextEditControls(ITextEditTarget target)
    {
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || _previewFrameW <= 0) return;

        double scaleX = layout.Width / _previewFrameW;
        double scaleY = layout.Height / _previewFrameH;

        var textRect = target.ComputeRect(_previewFrameW, _previewFrameH);
        double left = layout.X + textRect.X * scaleX;
        double top = layout.Y + textRect.Y * scaleY;
        double w = textRect.Width * scaleX;
        double h = textRect.Height * scaleY;

        Canvas.SetLeft(TextEditRegion, left);
        Canvas.SetTop(TextEditRegion, top);
        TextEditRegion.Width = w;
        TextEditRegion.Height = h;

        Canvas.SetLeft(TextEditBox, left);
        Canvas.SetTop(TextEditBox, top);
        TextEditBox.Width = w;
        TextEditBox.Height = h;

        PositionTextBoxHandles(target, left, top, w, h);

        // Match the target's text styling so editing looks WYSIWYG.
        double fontSize = Math.Max(8, target.FontSize * scaleY);
        var style = (target.FontFamily, fontSize, target.IsBold, target.IsItalic,
            target.TextColor, target.TextAlignment);
        if (_textEditStyle == style)
            return;
        _textEditStyle = style;

        TextEditBox.FontSize = fontSize;
        TextEditBox.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(target.FontFamily);
        TextEditBox.FontWeight = target.IsBold
            ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        TextEditBox.FontStyle = target.IsItalic
            ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        TextEditBox.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseHexColor(target.TextColor));
        TextEditBox.TextAlignment = target.TextAlignment switch
        {
            SlideTextAlignment.Left => TextAlignment.Left,
            SlideTextAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
    }

    /// <summary>
    /// Places the 8 corner/edge grabbers (TL/T/TR/L/R/BL/B/BR — same convention as
    /// <see cref="Musio_App.Controls.RegionSelectorOverlay"/>'s handles) around the box,
    /// and hides them for targets with no user-resizable box (text slides) or while the
    /// text is being edited in place — a handle overlapping the edit box would steal the
    /// pointer from text selection. Called every drawn frame (see
    /// <see cref="PositionTextEditControls"/>, which runs from the Win2D draw callback),
    /// so each write is guarded to avoid dirtying layout when nothing actually moved.
    /// </summary>
    private void PositionTextBoxHandles(ITextEditTarget target, double left, double top, double w, double h)
    {
        bool show = target.CanResizeBox && _editingTextId is null;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SetHandleVisibility(TextEditHandleTL, visibility);
        SetHandleVisibility(TextEditHandleT, visibility);
        SetHandleVisibility(TextEditHandleTR, visibility);
        SetHandleVisibility(TextEditHandleL, visibility);
        SetHandleVisibility(TextEditHandleR, visibility);
        SetHandleVisibility(TextEditHandleBL, visibility);
        SetHandleVisibility(TextEditHandleB, visibility);
        SetHandleVisibility(TextEditHandleBR, visibility);
        if (!show) return;

        const double hh = 4; // half of the 8px handle, so it centres on its point
        SetHandlePosition(TextEditHandleTL, left - hh, top - hh);
        SetHandlePosition(TextEditHandleT, left + w / 2 - hh, top - hh);
        SetHandlePosition(TextEditHandleTR, left + w - hh, top - hh);
        SetHandlePosition(TextEditHandleL, left - hh, top + h / 2 - hh);
        SetHandlePosition(TextEditHandleR, left + w - hh, top + h / 2 - hh);
        SetHandlePosition(TextEditHandleBL, left - hh, top + h - hh);
        SetHandlePosition(TextEditHandleB, left + w / 2 - hh, top + h - hh);
        SetHandlePosition(TextEditHandleBR, left + w - hh, top + h - hh);
    }

    private static void SetHandleVisibility(Border handle, Visibility visibility)
    {
        if (handle.Visibility != visibility) handle.Visibility = visibility;
    }

    private static void SetHandlePosition(Border handle, double x, double y)
    {
        if (Canvas.GetLeft(handle) != x) Canvas.SetLeft(handle, x);
        if (Canvas.GetTop(handle) != y) Canvas.SetTop(handle, y);
    }

    // ── Box resize gesture ──

    private bool _textBoxResizing;
    private bool _textBoxResizeMoved;
    private ITextEditTarget? _resizeTarget;

    // Which handle is being dragged — "TL"/"T"/"TR"/"L"/"R"/"BL"/"B"/"BR", the same tag
    // convention RegionSelectorOverlay uses for HitTestHandle/ApplyResize/GetCursorForHandle.
    private string _resizeHandle = "";

    // The box being dragged, in FRAME pixels (not canvas pixels), mutated incrementally by
    // ApplyBoxResize on every PointerMoved tick — mirrors RegionSelectorOverlay's _selX/Y/W/H
    // working state. Frame space (rather than canvas space) is used because the final result
    // must convert directly into the segment's normalized X/Y/WidthFraction/HeightFraction.
    private double _resizeLeft, _resizeTop, _resizeWidth, _resizeHeight;

    // Canvas-space pointer position as of the last tick, used to compute this tick's delta
    // — mirrors RegionSelectorOverlay's incremental _resizeStart-updated-every-tick model.
    private Windows.Foundation.Point _resizeLastPointerCanvas;

    // The box's normalized fields as of gesture start (captured right after PrepareForDrag,
    // so it reflects any anchor-to-Custom snap), used only to decide whether the gesture
    // actually changed anything (see FinalizeTextBoxResize).
    private (double X, double Y, double W, double H) _resizeStartBox;

    /// <summary>
    /// Floor for a dragged box's width/height, as a fraction of the frame in each axis.
    /// Mirrors the old width-only handle's <c>MinTextWidthFraction</c> intent — below this
    /// the text wraps to almost nothing and the box's own handles start to collide, leaving
    /// no way to drag back out.
    /// </summary>
    private const double MinBoxFraction = 0.04;

    private void TextEditHandle_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is not null) return;
        if (sender is not Border { Tag: string tag }) return;
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(GetCursorForBoxHandle(tag));
    }

    private void TextEditHandle_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textBoxResizing)
            ProtectedCursor = null;
    }

    /// <summary>Same TL/BR, TR/BL, T/B, L/R cursor mapping as
    /// <see cref="Musio_App.Controls.RegionSelectorOverlay"/>'s GetCursorForHandle.</summary>
    private static Microsoft.UI.Input.InputSystemCursorShape GetCursorForBoxHandle(string handle) => handle switch
    {
        "TL" or "BR" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
        "TR" or "BL" => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
        "T" or "B" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
        "L" or "R" => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
        _ => Microsoft.UI.Input.InputSystemCursorShape.SizeAll,
    };

    private void TextEditHandle_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is not null) return;
        if (sender is not Border { Tag: string handleTag } handle) return;

        // A gesture is starting: build a FRESH target so its pre-gesture snapshot is the
        // state as of right now — the per-frame cached instance may predate other edits.
        var target = GetActiveEditTargetForNewGesture();
        if (target is null || !target.CanResizeBox) return;

        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || _previewFrameW <= 0) return;

        // Snap an anchored box to Custom at its current on-screen centre (without moving
        // it) exactly like a position drag does — see PrepareForDrag's remarks. Otherwise
        // every tick below would be silently overridden by ResolveCenter re-deriving the
        // centre from the (still non-Custom) anchor instead of the edges being dragged.
        target.PrepareForDrag();

        var rect = target.ComputeRect(_previewFrameW, _previewFrameH);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        _resizeHandle = handleTag;
        _resizeLeft = rect.X;
        _resizeTop = rect.Y;
        _resizeWidth = rect.Width;
        _resizeHeight = rect.Height;
        _resizeStartBox = target.Box;
        _resizeLastPointerCanvas = e.GetCurrentPoint(TextEditCanvas).Position;

        _resizeTarget = target;
        _textBoxResizing = true;
        _textBoxResizeMoved = false;
        handle.CapturePointer(e.Pointer);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(GetCursorForBoxHandle(handleTag));
        e.Handled = true;
    }

    private void TextEditHandle_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textBoxResizing || _resizeTarget is null) return;

        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || layout.Height <= 0 || _previewFrameW <= 0 || _previewFrameH <= 0) return;

        var pos = e.GetCurrentPoint(TextEditCanvas).Position;
        double scaleX = layout.Width / _previewFrameW;
        double scaleY = layout.Height / _previewFrameH;

        // Convert this tick's incremental pointer movement from canvas space to frame-pixel
        // space — mirrors RegionSelectorOverlay's Grid_PointerMoved/ApplyResize incremental
        // delta model (dx/dy since the LAST tick, not since gesture start).
        double dx = (pos.X - _resizeLastPointerCanvas.X) / scaleX;
        double dy = (pos.Y - _resizeLastPointerCanvas.Y) / scaleY;
        _resizeLastPointerCanvas = pos;

        ApplyBoxResize(_resizeHandle, dx, dy);

        double newX = (_resizeLeft + _resizeWidth / 2.0) / _previewFrameW;
        double newY = (_resizeTop + _resizeHeight / 2.0) / _previewFrameH;
        double newW = _resizeWidth / _previewFrameW;
        double newH = _resizeHeight / _previewFrameH;

        if (!_textBoxResizeMoved &&
            (Math.Abs(newX - _resizeStartBox.X) > 0.001 ||
             Math.Abs(newY - _resizeStartBox.Y) > 0.001 ||
             Math.Abs(newW - _resizeStartBox.W) > 0.001 ||
             Math.Abs(newH - _resizeStartBox.H) > 0.001))
        {
            _textBoxResizeMoved = true;
        }

        _resizeTarget.Box = (newX, newY, newW, newH);
        _resizeTarget.OnLivePositionChanged();
        e.Handled = true;
    }

    /// <summary>
    /// Resizes the working frame-pixel rect for one handle, exactly mirroring
    /// <see cref="Musio_App.Controls.RegionSelectorOverlay"/>'s ApplyResize: a corner moves
    /// both axes, an edge moves one, and in every case the OPPOSITE edge/corner is left
    /// untouched so it stays put while the dragged one follows the pointer. Clamped to a
    /// minimum size per axis and then to stay fully inside the frame.
    /// </summary>
    private void ApplyBoxResize(string handle, double dx, double dy)
    {
        switch (handle)
        {
            case "TL": _resizeLeft += dx; _resizeTop += dy; _resizeWidth -= dx; _resizeHeight -= dy; break;
            case "T": _resizeTop += dy; _resizeHeight -= dy; break;
            case "TR": _resizeTop += dy; _resizeWidth += dx; _resizeHeight -= dy; break;
            case "L": _resizeLeft += dx; _resizeWidth -= dx; break;
            case "R": _resizeWidth += dx; break;
            case "BL": _resizeLeft += dx; _resizeWidth -= dx; _resizeHeight += dy; break;
            case "B": _resizeHeight += dy; break;
            case "BR": _resizeWidth += dx; _resizeHeight += dy; break;
        }

        double minW = MinBoxFraction * _previewFrameW;
        double minH = MinBoxFraction * _previewFrameH;
        if (_resizeWidth < minW) _resizeWidth = minW;
        if (_resizeHeight < minH) _resizeHeight = minH;

        _resizeLeft = Math.Clamp(_resizeLeft, 0, Math.Max(0, _previewFrameW - _resizeWidth));
        _resizeTop = Math.Clamp(_resizeTop, 0, Math.Max(0, _previewFrameH - _resizeHeight));
    }

    private void TextEditHandle_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border handle) handle.ReleasePointerCapture(e.Pointer);
        FinalizeTextBoxResize();
        e.Handled = true;
    }

    private void TextEditHandle_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextBoxResize();

    private void TextEditHandle_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextBoxResize();

    /// <summary>
    /// Ends a box resize exactly once, however it ended (release, capture loss or
    /// cancellation), committing a single undo entry only when the box actually changed
    /// past a small threshold and otherwise restoring the pre-gesture box.
    /// </summary>
    private void FinalizeTextBoxResize()
    {
        if (!_textBoxResizing) return;
        _textBoxResizing = false;
        ProtectedCursor = null;

        var target = _resizeTarget;
        _resizeTarget = null;
        if (target is null) return;

        if (_textBoxResizeMoved)
        {
            var (x, y, w, h) = target.Box;
            target.CommitBox(x, y, w, h);
        }
        else
        {
            target.AbortResize();
        }

        _textBoxResizeMoved = false;
    }

    private void TextEditRegion_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is null)
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
    }

    private void TextEditRegion_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textRegionDragging)
            ProtectedCursor = null;
    }

    private void TextEditRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_editingTextId is not null) return; // editing — let the textbox handle it
        // A gesture is starting: always build a FRESH target rather than reusing the
        // per-frame cache — see GetActiveEditTargetForNewGesture's remarks.
        var target = GetActiveEditTargetForNewGesture();
        if (target is null) return;

        _textRegionDragging = true;
        _textDragMoved = false;
        _dragTarget = target; // pins the target (and, for overlays, its captured pre-drag
                               // original) for the whole gesture — see OverlayTextEditTarget

        // For an anchored overlay, snap it to Custom at its CURRENT on-screen centre
        // without visually moving it, so a live drag actually follows the pointer and a
        // plain click's release path never flips the anchor using stale raw X/Y — see
        // OverlayTextEditTarget.PrepareForDrag. A no-op for slides (they have no anchor).
        target.PrepareForDrag();

        _textDragStart = e.GetCurrentPoint(TextEditCanvas).Position;
        (_textDragStartX, _textDragStartY) = target.Center;
        TextEditRegion.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TextEditRegion_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textRegionDragging || _dragTarget is not { } target) return;
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0) return;

        var pos = e.GetCurrentPoint(TextEditCanvas).Position;

        // Only a real drag (past TextDragThreshold) is ever committed/undoable — see
        // FinalizeTextDrag. Sticky once set so a jittery pointer that crosses the
        // threshold and comes back still counts as a drag, not a click.
        if (!_textDragMoved &&
            (Math.Abs(pos.X - _textDragStart.X) >= TextDragThreshold ||
             Math.Abs(pos.Y - _textDragStart.Y) >= TextDragThreshold))
        {
            _textDragMoved = true;
        }

        double dx = (pos.X - _textDragStart.X) / layout.Width;
        double dy = (pos.Y - _textDragStart.Y) / layout.Height;

        // Live preview only — mutates the model directly with no undo entry. The overlay
        // target's CommitPosition (called once on release, below) restores this back to
        // the pre-drag position before committing a single undoable step; the slide target
        // just leaves it as the final, already-persisted value (matching its pre-refactor
        // direct-mutation behaviour).
        target.Center = (
            Math.Clamp(_textDragStartX + dx, 0.0, 1.0),
            Math.Clamp(_textDragStartY + dy, 0.0, 1.0));

        PositionTextEditControls(target);
        target.OnLivePositionChanged();
        e.Handled = true;
    }

    private void TextEditRegion_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_textRegionDragging) return;
        TextEditRegion.ReleasePointerCapture(e.Pointer);
        FinalizeTextDrag();
        e.Handled = true;
    }

    /// <summary>
    /// Pointer capture can be lost without a matching PointerReleased — another window
    /// steals focus, a touch/pen gesture is cancelled by the system, or the preview
    /// re-layouts under the pointer. Without this handler the live model mutations made
    /// during PointerMoved would stay applied with no undo entry and a stale properties
    /// pane. Routes through the same idempotent <see cref="FinalizeTextDrag"/> as a normal
    /// release.
    /// </summary>
    private void TextEditRegion_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextDrag();

    /// <summary>Touch/pen gestures can be cancelled outright (distinct from capture loss);
    /// finalize the same way so an interrupted drag is never left uncommitted.</summary>
    private void TextEditRegion_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => FinalizeTextDrag();

    /// <summary>
    /// Ends the in-progress text-region drag exactly once, whether it finished normally
    /// (PointerReleased) or was interrupted (PointerCaptureLost/PointerCanceled). Guarded by
    /// <see cref="_textRegionDragging"/>, so it is safe to call from more than one of those
    /// event handlers for the same gesture — only the first call does anything. A real
    /// drag (pointer moved past <see cref="TextDragThreshold"/>) is committed as one undo
    /// step; anything else — a plain click, or an interrupted gesture that never moved — is
    /// aborted, restoring the model to its exact pre-gesture state, so only a genuine move
    /// ever becomes an undo entry or a persisted change.
    /// </summary>
    private void FinalizeTextDrag()
    {
        if (!_textRegionDragging) return;
        _textRegionDragging = false;
        ProtectedCursor = null;

        if (_dragTarget is { } target)
        {
            if (_textDragMoved)
            {
                var (finalX, finalY) = target.Center;
                target.CommitPosition(finalX, finalY);
            }
            else
            {
                target.AbortDrag();
            }
        }
        _dragTarget = null;
        _textDragMoved = false;
    }

    private void TextEditRegion_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        EnterTextEdit();
        e.Handled = true;
    }

    private void EnterTextEdit()
    {
        // The taps that opened the editor may have started a region drag (and captured
        // the pointer) without a matching PointerReleased, because the region gets
        // collapsed below. Finalize that gesture first — via the same idempotent path a
        // normal release or an interrupted drag uses — so any live PrepareForDrag mutation
        // (e.g. an anchored overlay flipped to Custom by the opening press) is committed or
        // reverted instead of silently left in place, then release any capture outright.
        FinalizeTextDrag();
        FinalizeTextBoxResize();
        TextEditRegion.ReleasePointerCaptures();
        ProtectedCursor = null;

        // A gesture is starting: always build a FRESH target rather than reusing the
        // per-frame cache — see GetActiveEditTargetForNewGesture's remarks.
        var target = GetActiveEditTargetForNewGesture();
        if (target is null) return;

        _editingTextId = target.Id;
        _editTarget = target; // pins the target (and, for overlays, its captured
                               // pre-edit original text) for the whole edit session
        TextEditRegion.Visibility = Visibility.Collapsed;
        SetHandleVisibility(TextEditHandleTL, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleT, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleTR, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleL, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleR, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleBL, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleB, Visibility.Collapsed);
        SetHandleVisibility(TextEditHandleBR, Visibility.Collapsed);
        TextEditBox.Visibility = Visibility.Visible;
        TextEditBox.Text = target.Text;
        TextEditBox.Focus(FocusState.Programmatic);
        TextEditBox.SelectAll();

        target.BeginTextEdit();
    }

    private void CommitTextEdit()
    {
        if (_editingTextId is null) return;
        _editingTextId = null;
        var target = _editTarget;
        _editTarget = null;

        TextEditBox.Visibility = Visibility.Collapsed;
        TextEditRegion.Visibility = Visibility.Visible;

        target?.CommitText(TextEditBox.Text);
    }

    private void TextEditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editTarget is not { } target) return;

        // Live model update; committed as one undoable op on exit — see CommitTextEdit.
        target.Text = TextEditBox.Text;

        // Keep whichever properties-pane textbox mirrors this target in sync. The slide
        // flyout's own TextChanged mutates the slide directly (idempotent, no undo) so it
        // needs no guard; the overlay pane's does push an undoable operation per keystroke,
        // so the programmatic assignment must be suppressed to avoid a second undo entry.
        if (target is SlideTextEditTarget)
        {
            if (SlideTextBox is not null && SlideTextBox.Text != TextEditBox.Text)
                SlideTextBox.Text = TextEditBox.Text;
        }
        else if (target is OverlayTextEditTarget)
        {
            if (OverlayTextBox is not null && OverlayTextBox.Text != TextEditBox.Text)
            {
                using (SuppressScope.Enter(ref _suppressOverlayEvents))
                    OverlayTextBox.Text = TextEditBox.Text;
            }
        }

        // Re-measure so the edit box grows/shrinks to hug the wrapped text live
        // instead of staying at the height it had when editing started.
        PositionTextEditControls(target);
        target.OnLiveTextChanged();
        Timeline.InvalidateAllCanvases();
    }

    private void TextEditBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Enter commits (Shift+Enter inserts a newline); Esc commits too.
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if ((e.Key == Windows.System.VirtualKey.Enter && !shift)
            || e.Key == Windows.System.VirtualKey.Escape)
        {
            CommitTextEdit();
            e.Handled = true;
        }
    }

    private void TextEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
    }

    /// <summary>
    /// Unifies <see cref="TextSlideSegment"/> and <see cref="TextOverlaySegment"/> behind
    /// one shape so the shared drag/double-click-to-edit gesture code above (originally
    /// written only for slides) works for both without duplicating it. Implementations
    /// resolve the live segment by <see cref="Id"/> on every access rather than caching the
    /// record itself, since undo/redo and property-pane edits replace/mutate segments out
    /// from under any snapshot reference.
    /// </summary>
    private interface ITextEditTarget
    {
        string Id { get; }

        /// <summary>The segment's text. The setter is a live, non-undoable mutation used to
        /// preview keystrokes as they happen; see <see cref="CommitText"/> for the one-shot
        /// persisted change.</summary>
        string Text { get; set; }

        /// <summary>Normalized (0..1) centre of the text box. The setter is a live,
        /// non-undoable mutation used to preview a drag as it happens; see
        /// <see cref="CommitPosition"/> for the one-shot persisted change.</summary>
        (double X, double Y) Center { get; set; }

        string FontFamily { get; }
        double FontSize { get; }
        bool IsBold { get; }
        bool IsItalic { get; }
        string TextColor { get; }
        SlideTextAlignment TextAlignment { get; }

        /// <summary>The text box, in frame pixels, for a canvas of the given size.</summary>
        Rect ComputeRect(int frameWidth, int frameHeight);

        /// <summary>Called once when WYSIWYG editing begins (after <see cref="Text"/> has
        /// been read into the edit box, before any keystroke).</summary>
        void BeginTextEdit();

        /// <summary>Called once when a drag gesture begins, before <see cref="Center"/> is
        /// first read as the drag origin. A no-op for targets whose rendered position always
        /// matches <see cref="Center"/> (e.g. slides); for an anchored text overlay this
        /// snaps it to <see cref="Musio.Core.Timeline.TextOverlayAnchor.Custom"/> at its
        /// current on-screen centre WITHOUT visually moving it, so the subsequent live drag
        /// actually follows the pointer instead of being silently overridden by anchor-based
        /// rendering — see <see cref="OverlayTextEditTarget.PrepareForDrag"/>.</summary>
        void PrepareForDrag();

        /// <summary>Called after every keystroke, once <see cref="Text"/> has already been
        /// live-mutated, so implementations can nudge whatever preview needs it.</summary>
        void OnLiveTextChanged();

        /// <summary>Called after every drag tick, once <see cref="Center"/> has already been
        /// live-mutated, so implementations can repaint immediately (a drag must feel live).</summary>
        void OnLivePositionChanged();

        /// <summary>Persists <paramref name="newText"/> as the final value of a finished
        /// edit session (Enter/Esc/focus loss).</summary>
        void CommitText(string newText);

        /// <summary>Persists <paramref name="x"/>/<paramref name="y"/> as the final value of
        /// a finished drag that actually moved past the drag threshold.</summary>
        void CommitPosition(double x, double y);

        /// <summary>
        /// Whether the target's text box has a user-resizable rectangle, i.e. whether the
        /// preview should offer the 8 corner/edge grabbers. False for text slides, whose box
        /// is a fixed fraction of the slide.
        /// </summary>
        bool CanResizeBox { get; }

        /// <summary>
        /// The text box's normalized fields against the frame: <c>X</c>/<c>Y</c> are the
        /// box's centre (0..1, same semantics as <see cref="Center"/>) and <c>W</c>/<c>H</c>
        /// are its width/height fraction. The setter is a live, non-undoable mutation used
        /// to preview a resize as it happens; see <see cref="CommitBox"/> for the one-shot
        /// persisted change.
        /// </summary>
        (double X, double Y, double W, double H) Box { get; set; }

        /// <summary>Persists <paramref name="x"/>/<paramref name="y"/>/<paramref name="w"/>/
        /// <paramref name="h"/> as the final value of a finished resize that actually moved
        /// past the drag threshold.</summary>
        void CommitBox(double x, double y, double w, double h);

        /// <summary>Ends a resize gesture that never moved past the drag threshold by
        /// restoring the pre-gesture box, with no undo entry recorded.</summary>
        void AbortResize();

        /// <summary>Ends a drag gesture that never moved past the drag threshold (e.g. a
        /// plain click) by restoring the model to its exact pre-gesture state — undoing
        /// whatever <see cref="PrepareForDrag"/>/<see cref="Center"/>'s setter mutated live,
        /// with no undo entry recorded. A no-op for targets that had nothing to restore.</summary>
        void AbortDrag();
    }

    /// <summary>
    /// Adapts a <see cref="TextSlideSegment"/> to <see cref="ITextEditTarget"/>. Slides have
    /// no undo path for in-place edits today (see <see cref="SlideTextBox_TextChanged"/> et
    /// al., which all mutate the live segment directly) — every Commit* here is a plain
    /// assignment plus the same <see cref="RefreshSlidePreview"/> call the pre-refactor code
    /// used, so slide behaviour is unchanged by this abstraction.
    /// </summary>
    private sealed class SlideTextEditTarget : ITextEditTarget
    {
        private readonly EditorPage _page;
        public string Id { get; }

        public SlideTextEditTarget(EditorPage page, string id)
        {
            _page = page;
            Id = id;
        }

        private TextSlideSegment? Segment => _page.ViewModel.Model.Segments
            .OfType<TextSlideSegment>().FirstOrDefault(s => s.Id == Id);

        public string Text
        {
            get => Segment?.Text ?? string.Empty;
            set { if (Segment is { } s) s.Text = value; }
        }

        public (double X, double Y) Center
        {
            get => Segment is { } s ? (s.TextX, s.TextY) : (0.5, 0.5);
            set { if (Segment is { } s) { s.TextX = value.X; s.TextY = value.Y; } }
        }

        public string FontFamily => Segment?.FontFamily ?? "Segoe UI";
        public double FontSize => Segment?.FontSize ?? 72;
        public bool IsBold => Segment?.IsBold ?? false;
        public bool IsItalic => Segment?.IsItalic ?? false;
        public string TextColor => Segment?.TextColor ?? "#FFFFFF";
        public SlideTextAlignment TextAlignment => Segment?.TextAlignment ?? SlideTextAlignment.Center;

        public Rect ComputeRect(int frameWidth, int frameHeight) =>
            Segment is { } s ? TextSlideRenderer.ComputeTextRect(s, frameWidth, frameHeight) : default;

        public void BeginTextEdit() => _page.RefreshSlidePreview();

        // A slide's text box is always a fixed fraction of the slide (see
        // TextSlideRenderer.ComputeTextRect), so there is no rectangle for the user to
        // define and no resize handles are offered.
        public bool CanResizeBox => false;
        public (double X, double Y, double W, double H) Box { get => (Center.X, Center.Y, 1.0, 1.0); set { } }
        public void CommitBox(double x, double y, double w, double h) { }
        public void AbortResize() { }

        // Slides have no anchor concept — Center is always authoritative, so there is
        // nothing to snap/restore around a drag.
        public void PrepareForDrag() { }

        // Slides render text directly onto the preview bitmap themselves (see
        // RenderTextSlidePreviewAsync's drawText suppression while _editingTextId is set),
        // so nothing further is needed per keystroke — matches pre-refactor behaviour.
        public void OnLiveTextChanged() { }

        public void OnLivePositionChanged() => _page.RefreshSlidePreview();

        public void CommitText(string newText)
        {
            if (Segment is { } s) s.Text = newText;
            _page.RefreshSlidePreview(); // switches drawText back on for the finished text
        }

        public void CommitPosition(double x, double y)
        {
            // Already fully persisted and repainted by the last PointerMoved tick (see
            // OnLivePositionChanged) — nothing more to do, matching the pre-refactor
            // PointerReleased handler, which did nothing beyond cursor/capture cleanup.
            if (Segment is { } s) { s.TextX = x; s.TextY = y; }
        }

        // Slides mutate TextX/TextY directly and undoably-never — a sub-threshold move is
        // already the final, persisted value (same as the pre-refactor PointerReleased
        // handler, which always applied whatever the last live tick left behind). Nothing
        // to restore here, matching pre-existing behaviour exactly.
        public void AbortDrag() { }
    }

    /// <summary>
    /// Adapts a <see cref="TextOverlaySegment"/> to <see cref="ITextEditTarget"/>. Unlike
    /// slides, every persisted change here goes through a single
    /// <see cref="UpdateTextOverlayPropertiesOperation"/> so drags and text edits are each
    /// exactly one undo step, even though the live <see cref="Text"/>/<see cref="Center"/>
    /// setters mutate the model on every keystroke/mouse-move tick for a live preview.
    /// </summary>
    private sealed class OverlayTextEditTarget : ITextEditTarget
    {
        private readonly EditorPage _page;
        public string Id { get; }

        // Captured once at construction — i.e. when a gesture begins, since
        // GetActiveEditTargetForNewGesture() is called fresh at TextEditRegion_PointerPressed
        // and EnterTextEdit, and the resulting instance is then pinned in
        // _dragTarget/_editTarget for the rest of that gesture — so Commit*/AbortDrag can
        // restore the model to its exact pre-gesture state (including the anchor, which
        // PrepareForDrag may live-flip to Custom) right before executing the single
        // undoable operation, regardless of how many live in-between mutations
        // Center/Text/PrepareForDrag made.
        private readonly (double X, double Y) _originalCenter;
        private readonly string _originalText;
        private readonly double _originalWidthFraction;
        private readonly double _originalHeightFraction;
        private readonly TextOverlayAnchor _originalAnchor;

        public OverlayTextEditTarget(EditorPage page, string id)
        {
            _page = page;
            Id = id;
            var seg = Segment;
            _originalCenter = seg is { } s ? (s.X, s.Y) : (0.5, 0.85);
            _originalText = seg?.Text ?? string.Empty;
            _originalWidthFraction = seg?.WidthFraction ?? 0.6;
            _originalHeightFraction = seg?.HeightFraction ?? 0.14;
            _originalAnchor = seg?.Anchor ?? TextOverlayAnchor.BottomCenter;
        }

        private TextOverlaySegment? Segment => _page.ViewModel.Model.TextOverlays.FirstOrDefault(o => o.Id == Id);

        public string Text
        {
            get => Segment?.Text ?? string.Empty;
            set { if (Segment is { } o) o.Text = value; } // live typing preview; see CommitText
        }

        public (double X, double Y) Center
        {
            get => Segment is { } o ? (o.X, o.Y) : (0.5, 0.85);
            set { if (Segment is { } o) { o.X = value.X; o.Y = value.Y; } } // live drag preview; see CommitPosition
        }

        public string FontFamily => Segment?.FontFamily ?? "Segoe UI";
        public double FontSize => Segment?.FontSize ?? 42;
        public bool IsBold => Segment?.IsBold ?? false;
        public bool IsItalic => Segment?.IsItalic ?? false;
        public string TextColor => Segment?.TextColor ?? "#FFFFFF";
        public SlideTextAlignment TextAlignment => Segment?.TextAlignment ?? SlideTextAlignment.Center;

        public Rect ComputeRect(int frameWidth, int frameHeight) =>
            Segment is { } o ? o.ComputeBox(frameWidth, frameHeight) : default;

        // Overlays render via the always-on compositor, which has no drawText-suppression
        // switch the way slides do — see this feature's completion report for the known
        // cosmetic follow-up (the rendered overlay text can show through under the edit box
        // while WYSIWYG editing). Nothing to do here.
        public void BeginTextEdit() { }

        public void PrepareForDrag()
        {
            if (Segment is not { } o) return;
            if (o.Anchor == TextOverlayAnchor.Custom) return; // X/Y already authoritative

            // An anchored overlay renders at TextOverlaySegment.ResolveCenter(...), not its
            // raw X/Y, so a drag must first snap it to Custom at its CURRENT on-screen
            // centre (computed exactly like the renderer/hit-region do — see
            // TextOverlaySegment.ComputeBox) before it can move at all. Skipping this would mean
            // (a) a plain click's release path flips Anchor to Custom using the stale raw
            // X/Y, visibly jumping the overlay, and (b) every drag tick's Center-setter
            // mutation is silently overridden by ResolveCenter re-deriving the centre from
            // the (still non-Custom) anchor instead of the live X/Y. This mutation is live/
            // non-undoable, exactly like every other live-preview mutation in this class —
            // CommitPosition/AbortDrag below decide whether it (and any further movement)
            // becomes permanent or is reverted.
            var rect = o.ComputeBox(_page._previewFrameW, _page._previewFrameH);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            double cx = (rect.X + rect.Width / 2.0) / _page._previewFrameW;
            double cy = (rect.Y + rect.Height / 2.0) / _page._previewFrameH;

            o.Anchor = TextOverlayAnchor.Custom;
            o.X = Math.Clamp(cx, 0.0, 1.0);
            o.Y = Math.Clamp(cy, 0.0, 1.0);
        }

        public void OnLiveTextChanged() => _page.ScheduleOverlayPreviewRefresh();
        public void OnLivePositionChanged() => _page.RefreshOverlayPreview();

        public void CommitText(string newText)
        {
            if (Segment is not { } o) return;

            // Restore-then-execute: the model currently holds whatever the last live
            // keystroke left behind (see Text's setter above); reset it to what it was
            // before the edit session started so UpdateTextOverlayPropertiesOperation's own
            // "before" snapshot (taken inside Execute) captures the true original rather
            // than an intermediate keystroke — giving one clean undo entry for the whole
            // edit instead of one per character typed.
            o.Text = _originalText;
            _page.ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                Id, ov => ov.Text = newText, "Change Overlay Text"));

            _page.SyncTextOverlayUI(Id);
            _page.RefreshOverlayPreview();
        }

        public void CommitPosition(double x, double y)
        {
            if (Segment is not { } o) return;

            // Same restore-then-execute trick as CommitText, for the drag case: put the
            // model back to exactly where it was before the gesture began — including the
            // anchor, which PrepareForDrag may have live-flipped to Custom — before
            // Execute() takes its "before" snapshot, giving one clean undo entry for the
            // whole drag instead of one per PointerMoved tick / the PrepareForDrag
            // mutation. A real drag is no longer edge-anchored, so this also flips the
            // overlay to Custom placement (mirrors OverlayAnchor_Checked's own comment
            // about Custom being "the user positioned this by hand").
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            _page.ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                Id, ov => { ov.X = x; ov.Y = y; ov.Anchor = TextOverlayAnchor.Custom; }, "Move Text Overlay"));

            _page.SyncTextOverlayUI(Id);
            _page.RefreshOverlayPreview();
        }

        // The overlay's box is what decides where its text wraps and sits, so it is worth
        // resizing directly on the preview rather than guessing with the pane's sliders.
        public bool CanResizeBox => true;

        public (double X, double Y, double W, double H) Box
        {
            get => Segment is { } o ? (o.X, o.Y, o.WidthFraction, o.HeightFraction)
                : (_originalCenter.X, _originalCenter.Y, _originalWidthFraction, _originalHeightFraction);
            set
            {
                // live resize preview; see CommitBox for the one-shot persisted change
                if (Segment is { } o)
                {
                    o.X = value.X;
                    o.Y = value.Y;
                    o.WidthFraction = value.W;
                    o.HeightFraction = value.H;
                }
            }
        }

        public void CommitBox(double x, double y, double w, double h)
        {
            if (Segment is not { } o) return;

            // Restore-then-execute, exactly as CommitPosition/CommitText do, so the whole
            // resize is one undo entry rather than one per PointerMoved tick. A hand-resized
            // box is no longer edge-anchored — mirrors CommitPosition flipping Anchor to
            // Custom once a real drag has happened.
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            o.WidthFraction = _originalWidthFraction;
            o.HeightFraction = _originalHeightFraction;
            _page.ViewModel.UndoRedoManager.Execute(new UpdateTextOverlayPropertiesOperation(
                Id, ov =>
                {
                    ov.Anchor = TextOverlayAnchor.Custom;
                    ov.X = x;
                    ov.Y = y;
                    ov.WidthFraction = w;
                    ov.HeightFraction = h;
                }, "Resize Text Overlay"));

            _page.SyncTextOverlayUI(Id);
            _page.RefreshOverlayPreview();
        }

        public void AbortResize()
        {
            if (Segment is not { } o) return;
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            o.WidthFraction = _originalWidthFraction;
            o.HeightFraction = _originalHeightFraction;
            _page.RefreshOverlayPreview();
        }

        public void AbortDrag()
        {
            if (Segment is not { } o) return;

            // No real movement happened (e.g. a plain click) — restore the exact
            // pre-gesture state, undoing whatever PrepareForDrag/Center's setter mutated
            // live, with NO undo entry. This is what keeps a plain click on an anchored
            // overlay from silently flipping it to Custom.
            o.Anchor = _originalAnchor;
            o.X = _originalCenter.X;
            o.Y = _originalCenter.Y;
            _page.RefreshOverlayPreview();
        }
    }
}
