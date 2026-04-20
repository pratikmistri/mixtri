# Learnings

This file tracks approaches tried, what worked, and what didn't for each feature.

---

## Recording Overlay — Acrylic Background & No Border

**Approaches tried:**

1. **`DwmSetWindowAttribute` with `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`** — Partially worked. Removed the DWM-drawn border color but a white border still appeared from the window frame styles.
2. **Stripping `WS_BORDER` and `WS_DLGFRAME` via `GetWindowLongPtrW`/`SetWindowLongPtrW`** — Combined with the DWM approach, this fully eliminated the white border. ✅
3. **`AcrylicBrush` on Grid background** — Worked. Replaced solid `#E01E1E1E` with acrylic (TintColor `#1E1E1E`, TintOpacity 0.8, fallback color for non-composited desktops). ✅

**What worked:** All three combined — DWM color removal + window style stripping + AcrylicBrush.

---

## Recording Overlay — Pill Shape

**Approaches tried:**

1. **`CornerRadius="26"` on Grid + `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`** — Didn't fully work. The Grid content was rounded but the window background (black layer) was only slightly rounded by DWM (~8px), creating a visible two-layer effect.
2. **`CreateRoundRectRgn` + `SetWindowRgn`** — Worked. Clips the actual window to a pill-shaped region using `CreateRoundRectRgn(0, 0, width+1, height+1, height, height)`, eliminating the black background peeking through. ✅

**What worked:** `SetWindowRgn` with a round rect region using the window height as the ellipse radius to create a true pill shape at the OS level.

---

## MSIX Packaging — Switching from Unpackaged to Packaged

**Approaches tried:**

1. **Changed `WindowsPackageType` from `None` to `MSIX`** in `.csproj` — Required for Store submission and packaged debugging. Deploy flags were already present in the `.sln`. ✅
2. **Fixed `graphicsCapture` capability namespace** — `graphicsCapture` is not a valid `uap:Capability`; it requires the `uap6` namespace (`xmlns:uap6="http://schemas.microsoft.com/appx/manifest/uap/windows10/6"`). Changed `uap:Capability` to `uap6:Capability`. ✅

**What worked:** Setting `WindowsPackageType=MSIX` + using `uap6:Capability` for `graphicsCapture` in `Package.appxmanifest`.

**What didn't work:** `uap:Capability Name="graphicsCapture"` — fails manifest validation (DEP0700) because `graphicsCapture` is not in the base `uap` schema enumeration.

---

## Zoom Track — Segment-Based Representation

**Feature/area:** Timeline zoom track (TimelineControl, EditorPage, EditOperation, TimelineModel)

**Approaches tried:**

1. **Added computed `Start`/`End` properties to existing `ZoomKeyframe` record** — Worked. `Start = Timestamp - PreDuration`, `End = Timestamp + HoldDuration + PostDuration`. Preserves backward compatibility with AutoZoomEngine while enabling segment rendering. ✅
2. **Replaced diamond markers with rounded rectangle segment bars** — Worked. Auto-generated segments render in lighter color (not draggable). Manual segments are fully interactive. ✅
3. **Fixed `CutOperation` to use full Start/End span** — Critical fix. Old code only checked `kf.Timestamp`, but a segment can span a cut even if its center is outside. New code handles all overlap cases (keep/trim/shift/remove). ✅

**What worked:**
- Extending `ZoomKeyframe` with computed properties + `FromRange()` factory method for creating segments from drag ranges.
- New `DragMode` values for body/edge/create interactions with 5px threshold to distinguish click-to-scrub from drag-to-create.
- Overlap-aware hit testing (edges only when selected, body always).
- New edit operations: `AddZoomSegmentOperation`, `ResizeZoomSegmentOperation`, `UpdateZoomSegmentPropertiesOperation`.
- Properties panel toggles visibility based on selection state.

**What didn't work:**
- `CanvasGeometry.CreateRoundedRectangle(null, ...)` compiles but throws `COMException: Objects used together must be created from the same factory instance` at runtime. Must pass a valid `ICanvasResourceCreator` (e.g., `ds` from the Draw handler).
- XAML `SelectionChanged`/`ValueChanged` events fire during `InitializeComponent()` before `x:Name` controls are assigned — null guard needed for controls referenced in those handlers.
- After changing XAML element names/structure, stale `.g.cs` files cause `COMException: Element not found` at runtime → blank page. Fix: delete `bin/obj` and clean rebuild.

**Key design decisions:**
- Auto-generated (non-manual) zoom segments from clicks are visible but not editable — they don't match AutoZoomEngine's merged segments exactly.
- Left edge resize changes PreDuration+Timestamp; right edge changes HoldDuration.
- `ZoomKeyframe.MinSegmentDuration` = 200ms prevents degenerate segments.
- Build requires VS MSBuild (`dotnet build` fails due to PriGen tooling issue).

---

## App Logo — Generating MSIX Assets from Source Logo

**Feature/area:** App branding / Assets

**Approaches tried:**

1. **PowerShell + System.Drawing to resize source PNG (640x640) into all required MSIX assets** — Worked. Used `HighQualityBicubic` interpolation for clean downscaling. ✅
2. **Manual ICO generation with PNG-encoded entries** — Worked. Built multi-resolution ICO (16–256px) by writing the ICO binary header + PNG payloads directly via `BinaryWriter`. ✅

**What worked:** Using the largest source image (`Musio.png` 640x640) and resizing to all required targets: StoreLogo (50x50), Square44x44 (88x88, 24x24), Square150x150 (300x300), LockScreen (48x48), Wide310x150 (620x300 centered), SplashScreen (1240x600 centered), AppIcon.ico (multi-res). All existing references in Package.appxmanifest, MainWindow.xaml, csproj, and SystemTrayService.cs already pointed to the standard asset names — no code changes needed.

---

## Region Capture — Crop Not Applied to Recording

**Feature/area:** Recording pipeline (RecordingSession, VideoWriter, Project)

**Approaches tried:**

1. **Crop in `VideoWriter.WriteFrame` using Win2D `CanvasRenderTarget`** — Worked. Added optional `Rect? cropRect` to `VideoWriter` constructor. When set, each frame is cropped via `CanvasRenderTarget.DrawImage(source, destRect, sourceRect)` before saving as JPEG. Reuses a single `CanvasRenderTarget` across frames to avoid per-frame GPU allocation. ✅
2. **DPI-aware crop rect conversion in `RecordingSession.OnFrameCaptured`** — Worked. Used `GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI)` to get the monitor's DPI scale, then multiplied overlay coordinates (logical/DIP pixels) by the scale to get physical pixel coordinates for the capture texture. Clamped to frame bounds for safety. ✅
3. **Added `CropOffsetX/Y` to `Project` model** — Persists the crop origin in physical pixels so downstream cursor/zoom alignment can subtract the offset. ✅

**What worked:**
- `VideoWriter` accepts a crop `Rect?` and applies it using `CanvasRenderTarget.DrawImage(bitmap, destRect, sourceRect)` with a reusable render target.
- `RecordingSession` computes the DPI-scaled physical crop rect on first frame arrival, uses crop dimensions for `_captureWidth/_captureHeight` and passes the crop rect to `VideoWriter`.
- `Project.CropOffsetX/Y` stores the origin for downstream use.

**What didn't work / known limitations:**
- Cursor and zoom alignment in the editor is not yet adjusted for region recordings — `FrameCompositor.GetSystemDpiScale` detects DPI by comparing captured dimensions vs monitor logical size, which fails for region captures (crop width < logical width → returns 1.0). A future fix should pass the DPI scale through Project and subtract `CropOffsetX/Y` from mouse positions.
- The region overlay uses `Stretch="Fill"` on a full virtual desktop screenshot in a single-monitor-maximized window, so region coordinates on multi-monitor setups may be inaccurate (pre-existing issue in the region selector UI, not the recording pipeline).

---

## Zoom Track — Making Auto-Generated Segments Selectable & Editable

**Feature/area:** Timeline zoom track (TimelineControl, EditOperation)

**Approaches tried:**

1. **Removed `IsManual` gate from hit testing + added `IsManual = true` promotion on edit** — Worked. Auto-generated (click-tracking) segments become selectable, draggable, and resizable. On any modification (move, resize, property change), the segment is promoted to `IsManual = true` so it gets sent to the compositor as a manual override. Undo restores the original `IsManual` state. ✅

**What worked:**
- Removing `if (!kf.IsManual) continue;` from `HitTestZoomSegment` to allow selection.
- Changing resize handle condition from `isSelected && isEditable` to `isSelected`.
- Adding `IsManual = true` to all `with` expressions in `MoveZoomKeyframeOperation`, `ResizeZoomSegmentOperation`, and `UpdateZoomSegmentPropertiesOperation`.
- Saving `_previousIsManual` in `MoveZoomKeyframeOperation` for proper undo (resize/update ops already save the full previous keyframe).

**Key design decisions:**
- Auto segments keep their lighter fill color (`ZoomSegmentAutoFill`) until modified, at which point promotion to `IsManual` gives them the standard fill.
- Promotion to manual means the compositor receives the segment as a user override that takes priority over the auto-zoom engine's generated segments.

---

## Region Capture — MediaEncodingProfile Invalid Width/Height

**Feature/area:** Recording pipeline (RecordingSession, VideoWriter)

**Approaches tried:**

1. **Round capture dimensions to even numbers (`& ~1`) after DPI scaling** — Necessary fix. H.264 encoding requires width and height to be multiples of 2. After DPI-scaling the logical crop rect and truncating to int, dimensions could be odd (e.g., 150% DPI on a 746px logical region → 1119px physical). Applied `& ~1` rounding on all capture dimension paths (region crop, monitor, window) in `RecordingSession.OnFrameCaptured`. Also added matching rounding in `VideoWriter.FinalizeAsync` profile dimensions. ✅
2. **Changed `MediaEncodingProfile.CreateMp4(HD1080p)` to `Auto` quality** — Done. Using `Auto` avoids the profile being constrained to a preset resolution when custom dimensions are applied. Set `Subtype = "H264"` explicitly. ✅
3. **Made `FinalizeAsync` failure non-fatal in `RecordingSession.StopAsync`** — Wrapped `FinalizeAsync()` in try/catch so the recording session still completes and the project is created even if MP4 encoding fails. The editor can work directly from the `.frames/` JPEG directory. ✅

**What worked:**
- Even-dimension rounding with `& ~1` and minimum of 2 in both `RecordingSession` and `VideoWriter`.
- Using `VideoEncodingQuality.Auto` instead of `HD1080p` for the recording MP4 profile, then explicitly setting all video properties.
- Making `FinalizeAsync` failure non-fatal so region recordings always produce a usable project.

**What didn't work / pitfalls:**
- `Add-AppxPackage -Register` can silently fail if the previous registration's files were deleted (clean rebuild). Must `Remove-AppxPackage` first, then re-register.
- Deleting `bin/obj` for clean rebuild requires NuGet restore (`/restore` flag or separate `msbuild /t:Restore`).
- The `.frames/` JPEG directory confirmed the actual cropped dimensions (1119×454 = odd width from 150% DPI on a 746px region). This was the smoking gun proving H.264 rejection of odd dimensions.

---

## Compositor — Skip Background Fill for Non-Window Captures

**Feature/area:** Compositor pipeline (FrameCompositor, BackgroundCompositor, EditorPage, Project)

**Approaches tried:**

1. **Added `CaptureTargetType CaptureType` to `Project`, override `BackgroundStyle` in editor** — Worked. In `EditorPage.xaml.cs`, when `project.CaptureType != CaptureTargetType.Window`, the background style is overridden to `Padding=0, ShadowEnabled=false, CornerRadius=0, BorderEnabled=false`. Updated config is stored back to `ProjectService.CurrentComposition` so export uses the same settings. ✅

**What worked:**
- `Project.CaptureType` set from `_config.Target.Type` in `RecordingSession.StopAsync`.
- `EditorPage` conditionally strips background styling for region/full-screen recordings.
- `ProjectService.CurrentComposition` is updated so the export pipeline inherits the same config.

**Key design decisions:**
- Background fill (padding, shadow, rounded corners, border) only applies to window captures. Region and full-screen captures render raw content.

---

## Region Recording — Border Highlight Around Selected Region

**Feature/area:** Recording overlay (RecordingPage, RegionBorderHighlight)

**Approaches tried:**

1. **Four thin native Win32 popup windows (top, right, bottom, left edges)** — Worked. Each window is `WS_POPUP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED`, with `WDA_EXCLUDEFROMCAPTURE` so the border doesn't appear in the recording. Click-through via `WS_EX_TRANSPARENT`. ✅

**What worked:**
- `RegionBorderHighlight` class in `Musio.App\Services` creates 4 thin (3px) native windows with a red (#FF3030) background brush, positioned along the edges of the selected region.
- Shown in `RecordingPage.ShowRecordingOverlay` when `CaptureMode.CustomRegion` is active.
- Disposed in `CloseRecordingOverlay` when recording stops.

**Key design decisions:**
- Used native Win32 windows instead of WinUI windows — avoids transparency/compositor complexity and is lightweight.
- Click-through (`WS_EX_TRANSPARENT`) so the user can still interact with content under the border.
- Excluded from capture so the border never appears in the recorded video.

---

## Microsoft Store Submission — Manifest Cleanup

**Feature/area:** Package.appxmanifest / Store readiness

**Approaches tried:**

1. **Removed `systemAIModels` capability** — Was declared but never used in code. Restricted capabilities require justification and delay certification. ✅
2. **Removed `mp:PhoneIdentity` element and `mp` namespace** — Boilerplate for phone apps, not applicable to desktop WinUI 3. ✅
3. **Removed `Windows.Universal` TargetDeviceFamily** — WinUI 3 with `runFullTrust` is desktop-only; keeping `Windows.Universal` can cause Store validation issues. Kept only `Windows.Desktop`. ✅

**What worked:** All three changes applied cleanly. Build succeeded with no new errors.

**Key notes for Store submission (non-code):**
- Must associate app with Store in VS (right-click → Publish → Associate App with the Store) to update Identity Name/Publisher.
- Privacy policy URL required (app uses webcam, microphone, screen capture).
- `runFullTrust` requires justification in Partner Center (screen capture needs full trust).
- Store signs MSIX packages automatically on submission — no `.pfx` needed for Store builds.

---

## Main Window — Minimize Instead of Hide from Capture

**Feature/area:** MainWindow capture behavior during recording

**Approaches tried:**

1. **Removed `WDA_EXCLUDEFROMCAPTURE` from MainWindow** — The main window was permanently hidden from all screen capture (screenshots, recordings, etc.). Removed `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` and the unused `WDA_EXCLUDEFROMCAPTURE` constant / `SetWindowDisplayAffinity` P/Invoke from `MainWindow.xaml.cs`. The minimize-on-record and restore-on-stop behavior already existed in `RecordingPage.xaml.cs`. ✅

**What worked:** Simply removing the capture exclusion from `MainWindow`. The existing minimize/restore logic in `RecordingPage.ShowRecordingOverlay` / `CloseRecordingOverlay` already handles keeping the window out of the way during recording. The recording overlay and region border highlight retain their `WDA_EXCLUDEFROMCAPTURE` since they are recording-specific UI.

---

## Recording — Minimize Before Capture Starts

**Feature/area:** Recording startup sequence (RecordingPage)

**Approaches tried:**

1. **Moved minimize logic from `ShowRecordingOverlay` to a new `StartRecordButton_Click` handler with a 600ms delay** — Worked. Changed the Start Recording button from `Command="{x:Bind ViewModel.StartRecordingCommand}"` to `Click="StartRecordButton_Click"`. The click handler minimizes the main window first, waits 600ms for the animation to complete, then calls `ViewModel.StartRecordingCommand.Execute(null)`. This ensures the minimize animation is never captured in the recording. ✅

**What worked:** Decoupling minimize from the `ShowRecordingOverlay` method (which fires after recording starts) and moving it to a pre-recording click handler with an animation delay. The `ShowRecordingOverlay` now only handles the overlay and region border.

---

## Timeline Editor — Video Clip Selection, Speed Width, Split/Cut Fixes

**Feature/area:** Timeline editor (TimelineControl, EditorPage, EditorViewModel, EditOperation, TimelineMapper)

**Approaches tried:**

1. **Video clip selection via hit-testing in `VideoTrack_PointerPressed`** — Worked. Added `SelectedClipIndex` property and `VideoClipSelected` event to `TimelineControl`. Clips are hit-tested by converting click X to time and checking against clip `Start/End` ranges. Selected clip is highlighted with a brighter fill and white border. ✅
2. **Speed panel conditional visibility** — Worked. Wrapped speed controls in a `SpeedPanel` StackPanel (initially `Collapsed`). `UpdateSpeedPanelVisibility()` toggles based on `SelectedClipIndex`. Speed ComboBox updates to match selected clip's current SpeedFactor. ✅
3. **Speed changes clip width via `ApplyClipSpeedOperation`** — Worked. New operation adjusts clip `End` based on speed, shifts subsequent clips/zoom keyframes/speed segments, and updates `Duration`. Full state snapshot for undo. Added `SpeedFactor` and `SourceStart` properties to `TimelineClip` record for source-time mapping. ✅
4. **Split fix: create default clip if `Clips` is empty** — Worked. `SplitOperation.Execute` now creates a `[0, Duration]` default clip when `model.Clips.Count == 0` before splitting. Undo removes the default clip if it was created. ✅
5. **Cut fix: `RippleDeleteOperation` at playhead** — Worked. Changed `CutSelection()` to find the clip at the playhead position and remove it with `RippleDeleteOperation`, which also shifts subsequent clips to close the gap. ✅
6. **TimelineMapper: clips as kept regions** — Worked. When `Clips` exist, `BuildFromClips()` creates output segments directly from clip data (using `EffectiveSourceStart` and `SpeedFactor`). When no clips exist, falls back to original trim-range logic. Fixed `IsDeleted()` to check if source time is outside any clip's source range. ✅

**What worked:**
- Adding `SpeedFactor` (default 1.0) and `SourceStart` (nullable, defaults to `Start`) to `TimelineClip` preserves backward compatibility — all existing `new TimelineClip(start, end, label)` calls continue to work.
- Full state snapshot (Clips, ZoomKeyframes, SpeedSegments, Duration, TrimEnd, PlayheadPosition) in both `ApplyClipSpeedOperation` and `RippleDeleteOperation` makes undo reliable.
- Scaling zoom keyframes within sped-up clips + shifting those after maintains zoom timing consistency.
- The mapper's two-mode approach (clips-based vs trim-range) avoids breaking the no-clips case while fixing the clips-as-kept-regions semantics.

**What didn't work / pitfalls:**
- Original `CutOperation` used `TrimStart`/`TrimEnd` as the cut range, which defaulted to the full timeline — cutting everything instead of just a segment.
- Original `SplitOperation` silently did nothing when `Clips` was empty (no error, just no-op).
- `TimelineMapper` originally treated Clips as *deleted* regions (inverted semantics) — any content inside a clip was skipped in output. This contradicted the editor's visual representation where clips are drawn as *kept* content.
- Mixing source-time and output-time in the same model fields is dangerous. The solution keeps positions in output time after speed changes, with `SourceStart`/`SpeedFactor` providing the mapping back to source time for the mapper.

**Key design decisions:**
- Speed controls only appear when a video clip is selected (split first, then apply speed).
- Cut (Ctrl+X) = ripple delete (removes clip + closes gap). Delete = simple removal.
- `CutSelection` auto-creates a default clip if none exist, so Ctrl+X always works.

---

## Zoom Region — Draggable Visual Positioning

**Feature/area:** Zoom region editor (EditorPage, AutoZoomEngine, FrameCompositor)

**Approaches tried:**

1. **X/Y Sliders in toolbar** — Original approach. Two problems: (a) every slider ValueChanged fired UndoRedoManager.Execute, which triggered StateChanged → ClearZoomSelection, dismissing the controls; (b) FrameCompositor always overrode manual zoom center with cursor position, so changes were invisible in preview. ✗
2. **Draggable zoom region overlay on PreviewCanvas** — Worked. Shows raw source frame (no compositor) with a XAML rectangle overlay representing the zoom viewport. 4 dim rectangles surround the viewport for contrast. User drags to reposition, clicks Apply to commit via single `UpdateZoomSegmentPropertiesOperation`. ✅

**What worked:**
- `ZoomState.IsManualOverride` flag set in `AutoZoomEngine.GetZoomState()` when manual keyframe is active. `FrameCompositor.ComposeFrame` skips cursor-position override when flag is true.
- `_zoomRegionEditMode` flag in EditorPage skips compositor in `UpdatePreviewFrameAsync` to show raw frame during positioning.
- Zoom region rectangle accounts for output aspect ratio via `ComputeEffectiveViewport` logic (duplicated as `GetAspectRatioValue` helper).
- Viewport coordinates are clamped during drag so the rectangle stays within source bounds.
- `OnUndoRedoStateChanged` now only clears zoom selection if the selected keyframe no longer exists in the model (was previously clearing unconditionally).
- Auto-enters edit mode on segment creation; existing segments use "Edit Region" button.

**What didn't work / pitfalls:**
- Auto-entering edit mode on every segment selection is too disruptive (pauses playback, seeks playhead). Better to use explicit "Edit Region" button for existing segments and only auto-enter on creation.
- `AspectRatio` enum lives in `Musio.Core.Settings` namespace — must add `using Musio.Core.Settings;` when referencing it from the App layer.
- Canvas dim rectangles must use `IsHitTestVisible="False"` so pointer events pass through to the viewport rectangle.

---

## Auto-Zoom Segment Deletion — Suppressing Click-Driven Auto-Zoom

**Feature/area:** AutoZoomEngine, EditOperations, FrameCompositor, PreviewRenderer, ExportEngine, VideoEncoder

**Approaches tried:**

1. **Track suppressed source click ticks** — Auto-generated ZoomKeyframes now carry `SourceClickTicks` (the raw `ClickEvent.TimestampTicks`). When deleted or converted to manual via edit operations, the source tick is added to `TimelineModel.SuppressedClickTicks`. `AutoZoomEngine.BuildZoomTimeline()` caches build parameters and `SetSuppressedClickTicks()` rebuilds auto segments, filtering out suppressed clicks before merging. This is robust because `long` ticks are exact — no floating-point tolerance needed. ✅

**What worked:** Storing source click identity on auto-generated keyframes and suppressing at the click level before segment merging. The fix covers preview (via `InvalidatePreview` syncing), export (both `ExportEngine` GIF path and `VideoEncoder` MP4 path), and undo/redo (operations add/remove from suppressed set symmetrically).

**What didn't work / pitfalls:**
- Using `TimeSpan`-based timestamps as suppression keys is fragile — round-trip through `TimeSpan.FromSeconds()` introduces 100ns-tick quantization, and timeline edits (cut/speed) shift display timestamps without affecting source clicks. Use `TimestampTicks` (raw `long`) instead.
- The export path (`VideoEncoder.ExportAsync`) previously didn't sync any zoom state at all — needed `TimelineModel?` parameter added.
- Edit operations that set `IsManual = true` (Move, Resize, UpdateProperties) must also suppress the original auto click, otherwise both the manual keyframe and the auto segment fire.

---

## Export — H.264 Odd Dimensions Failure

**Feature/area:** VideoEncoder (export pipeline)

**Approaches tried:**

1. **Enforce even dimensions with `& ~1` at the target dimension assignment** — Worked. Applied the same fix pattern already used in `VideoWriter.FinalizeAsync` (recording pipeline) to `VideoEncoder.ExportAsync`. Reuses the existing scaling path (`needsScaling`) to downscale compositor frames when odd→even adjustment changes dimensions. ✅

**What worked:** Rounding `targetWidth` and `targetHeight` down to even with `& ~1` right where they're assigned from compositor output (lines 118-121 of VideoEncoder.cs). The existing `needsScaling` branch already handles the dimension mismatch, so no new rendering code was needed.

**What didn't work / pitfalls:**
- The recording pipeline (`VideoWriter.FinalizeAsync`) had this fix but the export pipeline (`VideoEncoder`) did not — the fix must be applied in BOTH places.
- Region captures at fractional DPI (125%, 150%, 175%) produce physical pixel dimensions that are often odd. Aspect ratio cropping via `Math.Round()` in `ComputeContentDimensions` can also produce odd content dimensions.
- H.264 silently rejects odd dimensions — the `MediaTranscoder.PrepareMediaStreamSourceTranscodeAsync` returns `CanTranscode = false` with no descriptive message, making root cause hard to diagnose.

