# Musio — Accessibility Audit

> **Date:** 2026-04-21
> **Scope:** Full UI accessibility pass — XAML pages, custom controls, code-behind, ViewModels, services, manifest
> **Standard:** WCAG 2.1 Level AA

---

## Summary

| Severity | Count |
|----------|-------|
| Critical | 7     |
| Major    | ~~18~~ 15 (3 fixed) |
| Minor    | 22    |

### Critical issues at a glance

1. Timeline control is entirely pointer-driven with no keyboard access or screen reader exposure
2. Region selector overlay is mouse-only with no keyboard path for creating/resizing selections
3. Zoom region editor is mouse-only for repositioning the viewport
4. Preview canvas has no accessible name, role, or automation peer
5. Play/Pause button has no accessible name (icon-only)
6. Recording overlay stop button has no accessible name (icon-only)
7. Region selector overlay has no automation peers for selection state

---

## 1 · Critical Issues

### 1.1 Timeline control — no keyboard access or screen reader support

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/TimelineControl.xaml` (L48–86) |
| **Code-behind** | `TimelineControl.xaml.cs` (L566–737, L955–1100) |
| **WCAG** | 2.1.1 Keyboard, 2.1.2 No Keyboard Trap, 4.1.2 Name/Role/Value, 1.3.1 Info and Relationships |

All five `CanvasControl` surfaces (`TimeRulerCanvas`, `VideoTrackCanvas`, `CursorTrackCanvas`, `ZoomTrackCanvas`, `AudioTrackCanvas`) are custom-drawn and pointer-driven. There is:

- No `AutomationPeer` exposing clips, zoom segments, playhead position, or trim handles to assistive tech.
- No keyboard interaction for scrubbing, selecting clips, moving the playhead, creating/editing zoom segments, or resizing trim handles.
- No focusable targets or visible focus indicators within the timeline.
- Cursor-shape changes (`SetCursor(...)`) signal actionable areas only visually — no alternate feedback for assistive tech.
- Zoom/scroll is mouse-wheel–only (`Grid_PointerWheelChanged`) with no keyboard equivalent.

### 1.2 Region selector overlay — mouse-only interaction

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/RegionSelectorOverlay.xaml` (L9–78) |
| **Code-behind** | `RegionSelectorOverlay.xaml.cs` (L73–125, L186–383) |
| **WCAG** | 2.1.1 Keyboard, 2.5.1 Pointer Gestures, 4.1.2 Name/Role/Value, 1.3.1 Info and Relationships |

The entire region selection UI — draw, move, resize — is implemented through `PointerPressed/Moved/Released` handlers. Keyboard support is limited to Escape (cancel) and Enter (confirm). There is:

- No keyboard method to create, move, or resize the selection rectangle.
- No custom `AutomationPeer` exposing the selection state (bounds, handles) to screen readers.
- Selection handles and dimension labels are invisible to assistive tech.

### 1.3 Zoom region editor — pointer-only viewport positioning

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/EditorPage.xaml.cs` (L585–796) |
| **WCAG** | 2.1.1 Keyboard, 2.5.1 Pointer Gestures |

The zoom-region edit mode depends on pointer dragging inside the preview canvas to reposition the viewport. No keyboard alternative exists for moving or resizing the zoom viewport rectangle.

### 1.4 Preview canvas — missing accessible name and automation peer

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/PreviewCanvas.xaml` (L12–14) |
| **Code-behind** | `PreviewCanvas.xaml.cs` (L10–180) |
| **WCAG** | 4.1.2 Name/Role/Value, 2.1.1 Keyboard, 2.4.7 Focus Visible |

The `CanvasControl` (`PreviewSurface`) used for video preview has no accessible name, role, or `AutomationPeer`. Screen readers cannot identify or interact with the preview surface. Playback state changes (play/pause, seek position) are not announced.

### 1.5 Play/Pause button — icon-only, no accessible name

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/PreviewCanvas.xaml` (L24–35) |
| **WCAG** | 4.1.2 Name/Role/Value, 2.4.4 Link Purpose, 1.1.1 Non-text Content |

The `PlayPauseButton` contains only a `FontIcon` glyph with no `AutomationProperties.Name`, `ToolTip`, or visible text label. Screen readers will announce it as an unlabeled button.

### 1.6 Recording overlay stop button — icon-only, no accessible name

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/RecordingOverlayWindow.xaml` (L37–50) |
| **WCAG** | 4.1.2 Name/Role/Value, 1.1.1 Non-text Content |

The `StopButton` is the only interactive control in the recording overlay and is icon-only. Without `AutomationProperties.Name`, screen reader users cannot identify its purpose.

### 1.7 Region selector — no automation peers for selection state

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/RegionSelectorOverlay.xaml.cs` (L41–62, L186–293) |
| **WCAG** | 4.1.2 Name/Role/Value, 1.3.1 Info and Relationships |

The custom overlay surface draws selection bounds and resize handles, but provides no custom `AutomationPeer`. Assistive tech cannot understand the overlay's current selection state, position, or dimensions.

---

## 2 · Major Issues

### 2.1 Editor toolbar — icon-only buttons missing accessible names

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/EditorPage.xaml` (L118–323) |
| **WCAG** | 4.1.2 Name/Role/Value, 1.1.1 Non-text Content |

Multiple toolbar buttons (Undo, Redo, Split, Delete, Cut, Edit Region, Remove Zoom Segment, and export-related buttons) rely only on icons or tooltip text without explicit `AutomationProperties.Name`. Screen readers may announce them poorly or not at all.

### 2.2 Export state changes — no live region announcements

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/EditorPage.xaml` (L41–47, L242–318) |
| **Code-behind** | `EditorPage.xaml.cs` (L824–893) |
| **WCAG** | 4.1.3 Status Messages |

Export progress, success, and error states are toggled via panel visibility (`ShowExportingState`, `ShowExportedState`, `ShowErrorState`) without `AutomationProperties.LiveSetting` or `AutomationPeer.RaiseAutomationEvent`. Screen readers may miss these state changes entirely.

### 2.3 Recording timer/status — no live region

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/RecordingPage.xaml` (L102–108, L231–242) |
| **ViewModel** | `RecordingViewModel.cs` (L169–353) |
| **WCAG** | 4.1.3 Status Messages |

The recording elapsed time, status text, and progress ring update dynamically but lack `AutomationProperties.LiveSetting`. Recording state transitions (countdown → recording → paused → stopped) and errors are not explicitly announced.

### 2.4 Recording overlay elapsed timer — no live region

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/RecordingOverlayWindow.xaml` (L25–33) |
| **Code-behind** | `RecordingOverlayWindow.xaml.cs` (L23–34, L106–115) |
| **WCAG** | 4.1.3 Status Messages |

The `ElapsedText` TextBlock continuously updates with recording duration but has no live region configuration. Screen readers won't announce the changing time.

### 2.5 Recording page — refresh button missing accessible name

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/RecordingPage.xaml` (L196–202) |
| **WCAG** | 4.1.2 Name/Role/Value, 1.1.1 Non-text Content |

The refresh windows button is icon-only and needs `AutomationProperties.Name` (a tooltip alone is insufficient for screen readers).

### 2.6 Settings page — section titles not semantic headings

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/SettingsPage.xaml` (L18–180) |
| **WCAG** | 1.3.1 Info and Relationships |

Section titles (Settings, General, Recording Defaults, Audio, Webcam, Hotkeys, About) are visual `TextBlock` elements without `AutomationProperties.HeadingLevel`. Screen reader users cannot navigate by heading structure.

### 2.7 Preview canvas — playback time not exposed as live region

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/PreviewCanvas.xaml` (L36–41) |
| **WCAG** | 4.1.3 Status Messages |

The `TimeDisplay` TextBlock shows dynamic playback time ("0:00 / 0:00") but is not configured as a live region. Screen readers may not announce position updates.

### ~~2.8 Preview canvas — hard-coded colors, potential High Contrast failure~~ ✅ FIXED

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/PreviewCanvas.xaml` (L17–42) |
| **WCAG** | 1.4.11 Non-text Contrast, 2.4.7 Focus Visible |

~~The bottom control overlay uses hard-coded semi-transparent black background and white text. Focus visibility and contrast may fail in Windows High Contrast mode.~~ **Fixed:** Replaced with `{ThemeResource OverlayDimBrush}` and `{ThemeResource OverlayForegroundBrush}` which adapt in High Contrast mode.

### ~~2.9 Region selector — hard-coded colors may fail contrast~~ ✅ FIXED

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/RegionSelectorOverlay.xaml` (L37–66) |
| **WCAG** | 1.4.11 Non-text Contrast, 1.4.3 Contrast (Minimum) |

~~Fixed stroke/fill colors (`#FF0078D4`, `White`, `#CC000000`) may not meet contrast requirements in High Contrast mode or on certain backgrounds.~~ **Fixed:** All colors replaced with theme resources (`RegionSelectionStrokeBrush`, `RegionHandleFillBrush`, `RegionLabelBackgroundBrush`, `OverlayForegroundBrush`) with High Contrast variants.

### 2.10 Region selector — screenshot image not accessible

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/RegionSelectorOverlay.xaml` (L24–26) |
| **WCAG** | 1.1.1 Non-text Content, 4.1.2 Name/Role/Value |

The `ScreenshotImage` has no `AutomationProperties.Name` and is not marked as decorative. Screen readers receive no context about the displayed image.

### ~~2.11 Timeline control — hard-coded colors, High Contrast failure~~ ✅ FIXED

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/TimelineControl.xaml` (L33–45) |
| **WCAG** | 1.4.3 Contrast (Minimum), 1.4.11 Non-text Contrast |

~~Fixed background colors (`#FF282828`, `#FF1E1E1E`) and text colors (`#FFC8C8C8`) are hard-coded. These may fail contrast requirements and will not adapt to High Contrast themes.~~ **Fixed:** All timeline XAML and Win2D drawing colors moved to theme resources (`Themes/AppColors.xaml`) with High Contrast system color variants. Colors re-resolve on theme change.

### 2.12 Timeline control — no focus indicators

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/TimelineControl.xaml` (L48–86) |
| **WCAG** | 2.4.7 Focus Visible, 2.4.3 Focus Order |

There are no focusable targets or focus indicators within the timeline surfaces. Keyboard users cannot tell where focus is within the timeline.

### 2.13 Timeline control — zoom/scroll is mouse-wheel-only

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/TimelineControl.xaml` (L11–12) |
| **WCAG** | 2.1.1 Keyboard |

`PointerWheelChanged` on the root Grid provides zoom/navigation, but no keyboard equivalent (e.g., `Ctrl+Plus`/`Ctrl+Minus`) is available.

### 2.14 Export progress — ViewModel lacks announcement mechanism

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/ViewModels/ExportViewModel.cs` (L391–457) |
| **WCAG** | 4.1.3 Status Messages |

Export progress (`ProgressPercent`, `ProgressStatus`, `EstimatedTimeRemaining`) and errors (`ErrorMessage`) are only updated through bound properties. No accessible announcement mechanism ensures screen readers are notified of progress or failure.

### 2.15 Recording errors — no accessible announcement

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/ViewModels/RecordingViewModel.cs` (L319–332) |
| **WCAG** | 4.1.3 Status Messages |

`OnSessionError` sets `RecordingStatus = $"Error: {message}"` only via property binding. No explicit accessibility announcement surfaces the error to assistive tech.

### 2.16 System tray — not keyboard accessible

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Services/SystemTrayService.cs` (L38–180) |
| **WCAG** | 2.1.1 Keyboard, 2.1.2 No Keyboard Trap |

The system tray icon and context menu are implemented with Win32 APIs (`WM_LBUTTONUP`, `WM_RBUTTONUP`, `TrackPopupMenuEx`). The tray icon is not keyboard-focusable, and the popup menu may be poorly exposed to assistive tech compared to XAML-based menus.

### 2.17 System tray context menu — poor assistive tech exposure

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Services/SystemTrayService.cs` (L165–180) |
| **WCAG** | 4.1.2 Name/Role/Value |

The dynamically-created native popup menu may not expose proper accessible names/roles compared to WinUI XAML menus. Screen reader users may struggle to identify menu items.

### 2.18 Editor info bar — dynamic content lacks live region

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/EditorPage.xaml` (L41–47) |
| **WCAG** | 4.1.3 Status Messages |

The `EditorInfoBar` shows dynamic status messages but does not use `AutomationProperties.LiveSetting` to ensure screen readers announce updates.

---

## 3 · Minor Issues

### 3.1 Missing AutomationProperties.AutomationId — editor toolbar

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/EditorPage.xaml` (L118–323) |
| **WCAG** | 4.1.2 Name/Role/Value |

Toolbar buttons, `ZoomLevelCombo`, `ExportButton`, and flyout action buttons lack `AutomationProperties.AutomationId` for UI automation and testing.

### 3.2 Missing AutomationProperties.AutomationId — recording page

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/RecordingPage.xaml` (L60–202) |
| **WCAG** | 4.1.2 Name/Role/Value |

`StartRecordButton`, `StopRecordButton`, `CaptureModeSelector`, `SelectRegionButton`, `WindowComboBox`, and `RecordingProgressRing` lack automation IDs.

### 3.3 Missing AutomationProperties.AutomationId — settings page

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/SettingsPage.xaml` (L42–52) |
| **WCAG** | 4.1.2 Name/Role/Value |

`ThemeSelector` control is referenced by name but lacks a stable automation ID.

### 3.4 Missing AutomationProperties.AutomationId — recording overlay

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/RecordingOverlayWindow.xaml` (L37–50) |
| **WCAG** | 4.1.2 Name/Role/Value |

`StopButton` lacks `AutomationProperties.AutomationId`.

### 3.5 Missing AutomationProperties.AutomationId — preview canvas

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/PreviewCanvas.xaml` (L24–35) |
| **WCAG** | 4.1.2 Name/Role/Value |

`PlayPauseButton` lacks `AutomationProperties.AutomationId`.

### 3.6 Missing AutomationProperties.AutomationId — region selector

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/RegionSelectorOverlay.xaml` (L89–105) |
| **WCAG** | 4.1.2 Name/Role/Value |

`ConfirmButton`, `UseLastButton`, and `CancelButton` lack automation IDs.

### 3.7 Editor page — no semantic heading/landmark structure

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/EditorPage.xaml` (L182–197, L336–341) |
| **WCAG** | 1.3.1 Info and Relationships |

Editor toolbar labels and teaching tip content lack heading levels/landmarks for screen reader navigation.

### 3.8 Settings page — visual grouping without semantic structure

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/SettingsPage.xaml` (L35–153) |
| **WCAG** | 1.3.1 Info and Relationships |

Settings sections use Borders and StackPanels for visual grouping but no semantic grouping (e.g., `AutomationProperties.LandmarkType`) for assistive tech.

### 3.9 Settings page — verify High Contrast support

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/SettingsPage.xaml` (L24–196) |
| **WCAG** | 1.4.3 Contrast (Minimum), 1.4.11 Non-text Contrast |

While mostly using theme resources, custom card layouts should be verified in Windows High Contrast mode to ensure text and borders remain visible.

### 3.10 MainWindow — navigation items could use AutomationIds

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/MainWindow.xaml` (L41–57) |
| **WCAG** | 4.1.2 Name/Role/Value |

`NavigationViewItem` elements for Record and Editor should have `AutomationProperties.AutomationId` for UI test support.

### 3.11 MainWindow — custom title bar accessibility

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/MainWindow.xaml` (L21–31) |
| **WCAG** | 4.1.2 Name/Role/Value |

Custom title bar controls (back button, pane toggle) may need explicit accessible labeling for screen readers.

### 3.12 MainWindow — app icon image not marked decorative

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/MainWindow.xaml` (L28–30) |
| **WCAG** | 1.1.1 Non-text Content |

`ImageIconSource` for the app icon lacks accessible description or decorative marking.

### 3.13 Recording overlay — status indicator dot not labeled

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/RecordingOverlayWindow.xaml` (L18–22) |
| **WCAG** | 1.1.1 Non-text Content |

The `Ellipse` recording indicator dot is visual-only and should be marked as decorative or given an accessible name if it conveys recording status.

### 3.14 Recording page — segmented control items need verification

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/RecordingPage.xaml` (L126–145) |
| **WCAG** | 4.1.2 Name/Role/Value |

`Segmented` / `SegmentedItem` controls use icon + text but should be verified to expose meaningful names to screen readers (custom control semantics can be inconsistent).

### 3.15 Recording page — window list item template image

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/RecordingPage.xaml` (L170–195) |
| **WCAG** | 1.1.1 Non-text Content |

The `Image` inside the ComboBox item template for the window list should be marked as decorative or given an accessible name.

### 3.16 Region selector — instruction text lacks heading/announcement

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/RegionSelectorOverlay.xaml` (L70–78) |
| **WCAG** | 2.4.6 Headings and Labels, 2.4.7 Focus Visible |

The instruction text ("Click and drag to select region…") is visual-only with no heading structure or live announcement for state changes.

### 3.17 Timeline control — track labels not semantic headings

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/TimelineControl.xaml` (L33–45) |
| **WCAG** | 1.3.1 Info and Relationships, 2.4.6 Headings and Labels |

Track labels (Video, Cursor, Zoom, Audio) are visual TextBlocks, not exposed as headings or group labels for screen readers.

### 3.18 Timeline control — playhead indicator not accessible

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Controls/TimelineControl.xaml` (L89–97) |
| **WCAG** | 1.4.11 Non-text Contrast, 4.1.2 Name/Role/Value |

The `PlayheadLine` Border is a purely visual indicator with hard-coded red color and no accessible representation.

### 3.19 Recording start — focus context loss

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/Pages/RecordingPage.xaml.cs` (L51–110) |
| **WCAG** | 2.4.3 Focus Order, 4.1.3 Status Messages |

Recording start minimizes the main window and opens the overlay without explicit focus transfer or accessibility announcement. Users may lose context.

### 3.20 Recording overlay — focus management between windows

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/RecordingOverlayWindow.xaml.cs` (L36–86, L117–129) |
| **WCAG** | 2.4.3 Focus Order, 2.4.7 Focus Visible |

No explicit focus restoration/management when the overlay opens or closes. Focus flow between the main window and overlay may be inconsistent.

### 3.21 Global hotkeys — discoverability and conflict potential

| Detail | Value |
|--------|-------|
| **File** | `src/Musio.App/App.xaml.cs` (L79–93) |
| **WCAG** | 2.1.4 Character Key Shortcuts |

Global hotkeys (`Ctrl+Shift+R/P/S`) are registered without discoverability UI or conflict handling. Users cannot discover or customize these shortcuts.

### 3.22 ViewModel actions — no accessible confirmation

| Detail | Value |
|--------|-------|
| **Files** | `EditorViewModel.cs` (L59–156), `ExportViewModel.cs` (L321–351) |
| **WCAG** | 4.1.3 Status Messages |

Destructive actions (split, delete, cut, apply speed) and preset changes (save/delete) lack accessible confirmation feedback. Undo/redo availability changes are not announced.

---

## 4 · Recommendations by Priority

### Immediate (Critical)

1. **Add custom `AutomationPeer`s** for `TimelineControl` and `RegionSelectorOverlay` to expose interactive content to screen readers.
2. **Implement keyboard interaction** for the timeline (arrow keys for scrubbing, Tab for clip selection, keyboard shortcuts for split/delete/zoom).
3. **Implement keyboard interaction** for the region selector (arrow keys for position, Shift+arrow for resize).
4. **Add keyboard support** for the zoom-region editor (arrow keys to reposition the viewport).
5. **Add `AutomationProperties.Name`** to all icon-only buttons: Play/Pause, Stop (overlay), Undo, Redo, Split, Delete, Cut, Refresh, etc.

### Short-term (Major)

6. **Add `AutomationProperties.LiveSetting="Polite"`** to dynamic status areas: recording timer, export progress, editor info bar, preview time display.
7. **Add `AutomationProperties.HeadingLevel`** to section titles in SettingsPage.
8. **Replace hard-coded colors** with theme resources or add High Contrast overrides for TimelineControl, PreviewCanvas, and RegionSelectorOverlay.
9. **Surface export/recording errors** via `AutomationPeer.RaiseAutomationEvent` or accessible notifications.
10. **Ensure system tray** has a keyboard-accessible alternative (e.g., a menu bar or Settings page with the same commands).

### Long-term (Minor)

11. **Add `AutomationProperties.AutomationId`** to all named/interactive controls for UI test automation.
12. **Add semantic landmarks** (`AutomationProperties.LandmarkType`) to page regions.
13. **Mark decorative images** (app icon, window list thumbnails, recording indicator dot) appropriately.
14. **Add keyboard shortcut discoverability** (e.g., a Shortcuts section in Settings or tooltips).
15. **Verify all pages** in Windows High Contrast mode and fix any contrast failures.
16. **Add focus management** for window transitions (recording start/stop, overlay open/close).

---

## 5 · WCAG Criteria Coverage

| WCAG Criterion | Issues Found |
|----------------|-------------|
| 1.1.1 Non-text Content | 6 |
| 1.3.1 Info and Relationships | 8 |
| 1.4.3 Contrast (Minimum) | 4 |
| 1.4.11 Non-text Contrast | 5 |
| 2.1.1 Keyboard | 7 |
| 2.1.2 No Keyboard Trap | 2 |
| 2.1.4 Character Key Shortcuts | 1 |
| 2.4.3 Focus Order | 3 |
| 2.4.4 Link Purpose | 1 |
| 2.4.6 Headings and Labels | 2 |
| 2.4.7 Focus Visible | 4 |
| 2.5.1 Pointer Gestures | 3 |
| 4.1.2 Name, Role, Value | 14 |
| 4.1.3 Status Messages | 10 |
