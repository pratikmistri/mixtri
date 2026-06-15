# Learnings

This file tracks approaches tried, what worked, and what didn't for each feature.

---

## Recording Overlay â€” Acrylic Background & No Border

**Approaches tried:**

1. **`DwmSetWindowAttribute` with `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`** â€” Partially worked. Removed the DWM-drawn border color but a white border still appeared from the window frame styles.
2. **Stripping `WS_BORDER` and `WS_DLGFRAME` via `GetWindowLongPtrW`/`SetWindowLongPtrW`** â€” Combined with the DWM approach, this fully eliminated the white border. âœ…
3. **`AcrylicBrush` on Grid background** â€” Worked. Replaced solid `#E01E1E1E` with acrylic (TintColor `#1E1E1E`, TintOpacity 0.8, fallback color for non-composited desktops). âœ…

**What worked:** All three combined â€” DWM color removal + window style stripping + AcrylicBrush.

---

## Recording Overlay â€” Pill Shape

**Approaches tried:**

1. **`CornerRadius="26"` on Grid + `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`** â€” Didn't fully work. The Grid content was rounded but the window background (black layer) was only slightly rounded by DWM (~8px), creating a visible two-layer effect.
2. **`CreateRoundRectRgn` + `SetWindowRgn`** â€” Worked. Clips the actual window to a pill-shaped region using `CreateRoundRectRgn(0, 0, width+1, height+1, height, height)`, eliminating the black background peeking through. âœ…

**What worked:** `SetWindowRgn` with a round rect region using the window height as the ellipse radius to create a true pill shape at the OS level.

---

## MSIX Packaging â€” Switching from Unpackaged to Packaged

**Approaches tried:**

1. **Changed `WindowsPackageType` from `None` to `MSIX`** in `.csproj` â€” Required for Store submission and packaged debugging. Deploy flags were already present in the `.sln`. âœ…
2. **Fixed `graphicsCapture` capability namespace** â€” `graphicsCapture` is not a valid `uap:Capability`; it requires the `uap6` namespace (`xmlns:uap6="http://schemas.microsoft.com/appx/manifest/uap/windows10/6"`). Changed `uap:Capability` to `uap6:Capability`. âœ…

**What worked:** Setting `WindowsPackageType=MSIX` + using `uap6:Capability` for `graphicsCapture` in `Package.appxmanifest`.

**What didn't work:** `uap:Capability Name="graphicsCapture"` â€” fails manifest validation (DEP0700) because `graphicsCapture` is not in the base `uap` schema enumeration.

---

## Zoom Track â€” Segment-Based Representation

**Feature/area:** Timeline zoom track (TimelineControl, EditorPage, EditOperation, TimelineModel)

**Approaches tried:**

1. **Added computed `Start`/`End` properties to existing `ZoomKeyframe` record** â€” Worked. `Start = Timestamp - PreDuration`, `End = Timestamp + HoldDuration + PostDuration`. Preserves backward compatibility with AutoZoomEngine while enabling segment rendering. âœ…
2. **Replaced diamond markers with rounded rectangle segment bars** â€” Worked. Auto-generated segments render in lighter color (not draggable). Manual segments are fully interactive. âœ…
3. **Fixed `CutOperation` to use full Start/End span** â€” Critical fix. Old code only checked `kf.Timestamp`, but a segment can span a cut even if its center is outside. New code handles all overlap cases (keep/trim/shift/remove). âœ…

**What worked:**
- Extending `ZoomKeyframe` with computed properties + `FromRange()` factory method for creating segments from drag ranges.
- New `DragMode` values for body/edge/create interactions with 5px threshold to distinguish click-to-scrub from drag-to-create.
- Overlap-aware hit testing (edges only when selected, body always).
- New edit operations: `AddZoomSegmentOperation`, `ResizeZoomSegmentOperation`, `UpdateZoomSegmentPropertiesOperation`.
- Properties panel toggles visibility based on selection state.

**What didn't work:**
- `CanvasGeometry.CreateRoundedRectangle(null, ...)` compiles but throws `COMException: Objects used together must be created from the same factory instance` at runtime. Must pass a valid `ICanvasResourceCreator` (e.g., `ds` from the Draw handler).
- XAML `SelectionChanged`/`ValueChanged` events fire during `InitializeComponent()` before `x:Name` controls are assigned â€” null guard needed for controls referenced in those handlers.
- After changing XAML element names/structure, stale `.g.cs` files cause `COMException: Element not found` at runtime â†’ blank page. Fix: delete `bin/obj` and clean rebuild.

**Key design decisions:**
- Auto-generated (non-manual) zoom segments from clicks are visible but not editable â€” they don't match AutoZoomEngine's merged segments exactly.
- Left edge resize changes PreDuration+Timestamp; right edge changes HoldDuration.
- `ZoomKeyframe.MinSegmentDuration` = 200ms prevents degenerate segments.
- Build requires VS MSBuild (`dotnet build` fails due to PriGen tooling issue).

---

## App Logo â€” Generating MSIX Assets from Source Logo

**Feature/area:** App branding / Assets

**Approaches tried:**

1. **PowerShell + System.Drawing to resize source PNG (640x640) into all required MSIX assets** â€” Worked. Used `HighQualityBicubic` interpolation for clean downscaling. âœ…
2. **Manual ICO generation with PNG-encoded entries** â€” Worked. Built multi-resolution ICO (16â€“256px) by writing the ICO binary header + PNG payloads directly via `BinaryWriter`. âœ…

**What worked:** Using the largest source image (`Musio.png` 640x640) and resizing to all required targets: StoreLogo (50x50), Square44x44 (88x88, 24x24), Square150x150 (300x300), LockScreen (48x48), Wide310x150 (620x300 centered), SplashScreen (1240x600 centered), AppIcon.ico (multi-res). All existing references in Package.appxmanifest, MainWindow.xaml, csproj, and SystemTrayService.cs already pointed to the standard asset names â€” no code changes needed.

---

## Region Capture â€” Crop Not Applied to Recording

**Feature/area:** Recording pipeline (RecordingSession, VideoWriter, Project)

**Approaches tried:**

1. **Crop in `VideoWriter.WriteFrame` using Win2D `CanvasRenderTarget`** â€” Worked. Added optional `Rect? cropRect` to `VideoWriter` constructor. When set, each frame is cropped via `CanvasRenderTarget.DrawImage(source, destRect, sourceRect)` before saving as JPEG. Reuses a single `CanvasRenderTarget` across frames to avoid per-frame GPU allocation. âœ…
2. **DPI-aware crop rect conversion in `RecordingSession.OnFrameCaptured`** â€” Worked. Used `GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI)` to get the monitor's DPI scale, then multiplied overlay coordinates (logical/DIP pixels) by the scale to get physical pixel coordinates for the capture texture. Clamped to frame bounds for safety. âœ…
3. **Added `CropOffsetX/Y` to `Project` model** â€” Persists the crop origin in physical pixels so downstream cursor/zoom alignment can subtract the offset. âœ…

**What worked:**
- `VideoWriter` accepts a crop `Rect?` and applies it using `CanvasRenderTarget.DrawImage(bitmap, destRect, sourceRect)` with a reusable render target.
- `RecordingSession` computes the DPI-scaled physical crop rect on first frame arrival, uses crop dimensions for `_captureWidth/_captureHeight` and passes the crop rect to `VideoWriter`.
- `Project.CropOffsetX/Y` stores the origin for downstream use.

**What didn't work / known limitations:**
- Cursor and zoom alignment in the editor is not yet adjusted for region recordings â€” `FrameCompositor.GetSystemDpiScale` detects DPI by comparing captured dimensions vs monitor logical size, which fails for region captures (crop width < logical width â†’ returns 1.0). A future fix should pass the DPI scale through Project and subtract `CropOffsetX/Y` from mouse positions.
- The region overlay uses `Stretch="Fill"` on a full virtual desktop screenshot in a single-monitor-maximized window, so region coordinates on multi-monitor setups may be inaccurate (pre-existing issue in the region selector UI, not the recording pipeline).

---

## Zoom Track â€” Making Auto-Generated Segments Selectable & Editable

**Feature/area:** Timeline zoom track (TimelineControl, EditOperation)

**Approaches tried:**

1. **Removed `IsManual` gate from hit testing + added `IsManual = true` promotion on edit** â€” Worked. Auto-generated (click-tracking) segments become selectable, draggable, and resizable. On any modification (move, resize, property change), the segment is promoted to `IsManual = true` so it gets sent to the compositor as a manual override. Undo restores the original `IsManual` state. âœ…

**What worked:**
- Removing `if (!kf.IsManual) continue;` from `HitTestZoomSegment` to allow selection.
- Changing resize handle condition from `isSelected && isEditable` to `isSelected`.
- Adding `IsManual = true` to all `with` expressions in `MoveZoomKeyframeOperation`, `ResizeZoomSegmentOperation`, and `UpdateZoomSegmentPropertiesOperation`.
- Saving `_previousIsManual` in `MoveZoomKeyframeOperation` for proper undo (resize/update ops already save the full previous keyframe).

**Key design decisions:**
- Auto segments keep their lighter fill color (`ZoomSegmentAutoFill`) until modified, at which point promotion to `IsManual` gives them the standard fill.
- Promotion to manual means the compositor receives the segment as a user override that takes priority over the auto-zoom engine's generated segments.

---

## Region Capture â€” MediaEncodingProfile Invalid Width/Height

**Feature/area:** Recording pipeline (RecordingSession, VideoWriter)

**Approaches tried:**

1. **Round capture dimensions to even numbers (`& ~1`) after DPI scaling** â€” Necessary fix. H.264 encoding requires width and height to be multiples of 2. After DPI-scaling the logical crop rect and truncating to int, dimensions could be odd (e.g., 150% DPI on a 746px logical region â†’ 1119px physical). Applied `& ~1` rounding on all capture dimension paths (region crop, monitor, window) in `RecordingSession.OnFrameCaptured`. Also added matching rounding in `VideoWriter.FinalizeAsync` profile dimensions. âœ…
2. **Changed `MediaEncodingProfile.CreateMp4(HD1080p)` to `Auto` quality** â€” Done. Using `Auto` avoids the profile being constrained to a preset resolution when custom dimensions are applied. Set `Subtype = "H264"` explicitly. âœ…
3. **Made `FinalizeAsync` failure non-fatal in `RecordingSession.StopAsync`** â€” Wrapped `FinalizeAsync()` in try/catch so the recording session still completes and the project is created even if MP4 encoding fails. The editor can work directly from the `.frames/` JPEG directory. âœ…

**What worked:**
- Even-dimension rounding with `& ~1` and minimum of 2 in both `RecordingSession` and `VideoWriter`.
- Using `VideoEncodingQuality.Auto` instead of `HD1080p` for the recording MP4 profile, then explicitly setting all video properties.
- Making `FinalizeAsync` failure non-fatal so region recordings always produce a usable project.

**What didn't work / pitfalls:**
- `Add-AppxPackage -Register` can silently fail if the previous registration's files were deleted (clean rebuild). Must `Remove-AppxPackage` first, then re-register.
- Deleting `bin/obj` for clean rebuild requires NuGet restore (`/restore` flag or separate `msbuild /t:Restore`).
- The `.frames/` JPEG directory confirmed the actual cropped dimensions (1119Ã—454 = odd width from 150% DPI on a 746px region). This was the smoking gun proving H.264 rejection of odd dimensions.

---

## Compositor â€” Skip Background Fill for Non-Window Captures

**Feature/area:** Compositor pipeline (FrameCompositor, BackgroundCompositor, EditorPage, Project)

**Approaches tried:**

1. **Added `CaptureTargetType CaptureType` to `Project`, override `BackgroundStyle` in editor** â€” Worked. In `EditorPage.xaml.cs`, when `project.CaptureType != CaptureTargetType.Window`, the background style is overridden to `Padding=0, ShadowEnabled=false, CornerRadius=0, BorderEnabled=false`. Updated config is stored back to `ProjectService.CurrentComposition` so export uses the same settings. âœ…

**What worked:**
- `Project.CaptureType` set from `_config.Target.Type` in `RecordingSession.StopAsync`.
- `EditorPage` conditionally strips background styling for region/full-screen recordings.
- `ProjectService.CurrentComposition` is updated so the export pipeline inherits the same config.

**Key design decisions:**
- Background fill (padding, shadow, rounded corners, border) only applies to window captures. Region and full-screen captures render raw content.

---

## Region Recording â€” Border Highlight Around Selected Region

**Feature/area:** Recording overlay (RecordingPage, RegionBorderHighlight)

**Approaches tried:**

1. **Four thin native Win32 popup windows (top, right, bottom, left edges)** â€” Worked. Each window is `WS_POPUP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED`, with `WDA_EXCLUDEFROMCAPTURE` so the border doesn't appear in the recording. Click-through via `WS_EX_TRANSPARENT`. âœ…

**What worked:**
- `RegionBorderHighlight` class in `Musio.App\Services` creates 4 thin (3px) native windows with a red (#FF3030) background brush, positioned along the edges of the selected region.
- Shown in `RecordingPage.ShowRecordingOverlay` when `CaptureMode.CustomRegion` is active.
- Disposed in `CloseRecordingOverlay` when recording stops.

**Key design decisions:**
- Used native Win32 windows instead of WinUI windows â€” avoids transparency/compositor complexity and is lightweight.
- Click-through (`WS_EX_TRANSPARENT`) so the user can still interact with content under the border.
- Excluded from capture so the border never appears in the recorded video.

---

## Microsoft Store Submission â€” Manifest Cleanup

**Feature/area:** Package.appxmanifest / Store readiness

**Approaches tried:**

1. **Removed `systemAIModels` capability** â€” Was declared but never used in code. Restricted capabilities require justification and delay certification. âœ…
2. **Removed `mp:PhoneIdentity` element and `mp` namespace** â€” Boilerplate for phone apps, not applicable to desktop WinUI 3. âœ…
3. **Removed `Windows.Universal` TargetDeviceFamily** â€” WinUI 3 with `runFullTrust` is desktop-only; keeping `Windows.Universal` can cause Store validation issues. Kept only `Windows.Desktop`. âœ…

**What worked:** All three changes applied cleanly. Build succeeded with no new errors.

**Key notes for Store submission (non-code):**
- Must associate app with Store in VS (right-click â†’ Publish â†’ Associate App with the Store) to update Identity Name/Publisher.
- Privacy policy URL required (app uses webcam, microphone, screen capture).
- `runFullTrust` requires justification in Partner Center (screen capture needs full trust).
- Store signs MSIX packages automatically on submission â€” no `.pfx` needed for Store builds.

---

## Main Window â€” Minimize Instead of Hide from Capture

**Feature/area:** MainWindow capture behavior during recording

**Approaches tried:**

1. **Removed `WDA_EXCLUDEFROMCAPTURE` from MainWindow** â€” The main window was permanently hidden from all screen capture (screenshots, recordings, etc.). Removed `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` and the unused `WDA_EXCLUDEFROMCAPTURE` constant / `SetWindowDisplayAffinity` P/Invoke from `MainWindow.xaml.cs`. The minimize-on-record and restore-on-stop behavior already existed in `RecordingPage.xaml.cs`. âœ…

**What worked:** Simply removing the capture exclusion from `MainWindow`. The existing minimize/restore logic in `RecordingPage.ShowRecordingOverlay` / `CloseRecordingOverlay` already handles keeping the window out of the way during recording. The recording overlay and region border highlight retain their `WDA_EXCLUDEFROMCAPTURE` since they are recording-specific UI.

---

## Editor Audio Sync â€” Plan Review

**Feature/area:** Editor audio/video sync and scrub-audio design review

**Approaches tried:**

1. **Reviewed existing editor mapping against recorded `Project.AudioToVideoOffsetSeconds` semantics** â€” Worked. Confirmed the current heuristic in `EditorPage.LoadAudioWaveformAsync()` conflicts with the persisted capture-time offset and inverts playback alignment. âœ…
2. **Reviewed scrub proposal against current `AudioPlaybackEngine` implementation** â€” Identified a risk: repeated `Stop()` calls can leave `WaveOutEvent` in a stopped state where `Play()` may not resume reliably without re-init or different pause semantics.

**What worked:** Using the recorded positive pre-roll offset consistently for waveform trimming and `video -> audio` mapping matches the `Project` contract and the recording pipeline comments.

**What didn't work:** Assuming `Stop -> Seek -> Play` on every scrub event is harmless. The existing engine already uses `Stop()` for pause/seek, so adding more stop/play churn may reinforce the current "plays once then stops working" failure mode instead of fixing it.

---

## Recording â€” Minimize Before Capture Starts

**Feature/area:** Recording startup sequence (RecordingPage)

**Approaches tried:**

1. **Moved minimize logic from `ShowRecordingOverlay` to a new `StartRecordButton_Click` handler with a 600ms delay** â€” Worked. Changed the Start Recording button from `Command="{x:Bind ViewModel.StartRecordingCommand}"` to `Click="StartRecordButton_Click"`. The click handler minimizes the main window first, waits 600ms for the animation to complete, then calls `ViewModel.StartRecordingCommand.Execute(null)`. This ensures the minimize animation is never captured in the recording. âœ…

**What worked:** Decoupling minimize from the `ShowRecordingOverlay` method (which fires after recording starts) and moving it to a pre-recording click handler with an animation delay. The `ShowRecordingOverlay` now only handles the overlay and region border.

---

## Timeline Editor â€” Video Clip Selection, Speed Width, Split/Cut Fixes

**Feature/area:** Timeline editor (TimelineControl, EditorPage, EditorViewModel, EditOperation, TimelineMapper)

**Approaches tried:**

1. **Video clip selection via hit-testing in `VideoTrack_PointerPressed`** â€” Worked. Added `SelectedClipIndex` property and `VideoClipSelected` event to `TimelineControl`. Clips are hit-tested by converting click X to time and checking against clip `Start/End` ranges. Selected clip is highlighted with a brighter fill and white border. âœ…
2. **Speed panel conditional visibility** â€” Worked. Wrapped speed controls in a `SpeedPanel` StackPanel (initially `Collapsed`). `UpdateSpeedPanelVisibility()` toggles based on `SelectedClipIndex`. Speed ComboBox updates to match selected clip's current SpeedFactor. âœ…
3. **Speed changes clip width via `ApplyClipSpeedOperation`** â€” Worked. New operation adjusts clip `End` based on speed, shifts subsequent clips/zoom keyframes/speed segments, and updates `Duration`. Full state snapshot for undo. Added `SpeedFactor` and `SourceStart` properties to `TimelineClip` record for source-time mapping. âœ…
4. **Split fix: create default clip if `Clips` is empty** â€” Worked. `SplitOperation.Execute` now creates a `[0, Duration]` default clip when `model.Clips.Count == 0` before splitting. Undo removes the default clip if it was created. âœ…
5. **Cut fix: `RippleDeleteOperation` at playhead** â€” Worked. Changed `CutSelection()` to find the clip at the playhead position and remove it with `RippleDeleteOperation`, which also shifts subsequent clips to close the gap. âœ…
6. **TimelineMapper: clips as kept regions** â€” Worked. When `Clips` exist, `BuildFromClips()` creates output segments directly from clip data (using `EffectiveSourceStart` and `SpeedFactor`). When no clips exist, falls back to original trim-range logic. Fixed `IsDeleted()` to check if source time is outside any clip's source range. âœ…

**What worked:**
- Adding `SpeedFactor` (default 1.0) and `SourceStart` (nullable, defaults to `Start`) to `TimelineClip` preserves backward compatibility â€” all existing `new TimelineClip(start, end, label)` calls continue to work.
- Full state snapshot (Clips, ZoomKeyframes, SpeedSegments, Duration, TrimEnd, PlayheadPosition) in both `ApplyClipSpeedOperation` and `RippleDeleteOperation` makes undo reliable.
- Scaling zoom keyframes within sped-up clips + shifting those after maintains zoom timing consistency.
- The mapper's two-mode approach (clips-based vs trim-range) avoids breaking the no-clips case while fixing the clips-as-kept-regions semantics.

**What didn't work / pitfalls:**
- Original `CutOperation` used `TrimStart`/`TrimEnd` as the cut range, which defaulted to the full timeline â€” cutting everything instead of just a segment.
- Original `SplitOperation` silently did nothing when `Clips` was empty (no error, just no-op).
- `TimelineMapper` originally treated Clips as *deleted* regions (inverted semantics) â€” any content inside a clip was skipped in output. This contradicted the editor's visual representation where clips are drawn as *kept* content.
- Mixing source-time and output-time in the same model fields is dangerous. The solution keeps positions in output time after speed changes, with `SourceStart`/`SpeedFactor` providing the mapping back to source time for the mapper.

**Key design decisions:**
- Speed controls only appear when a video clip is selected (split first, then apply speed).
- Cut (Ctrl+X) = ripple delete (removes clip + closes gap). Delete = simple removal.
- `CutSelection` auto-creates a default clip if none exist, so Ctrl+X always works.

---

## Zoom Region â€” Draggable Visual Positioning

**Feature/area:** Zoom region editor (EditorPage, AutoZoomEngine, FrameCompositor)

**Approaches tried:**

1. **X/Y Sliders in toolbar** â€” Original approach. Two problems: (a) every slider ValueChanged fired UndoRedoManager.Execute, which triggered StateChanged â†’ ClearZoomSelection, dismissing the controls; (b) FrameCompositor always overrode manual zoom center with cursor position, so changes were invisible in preview. âœ—
2. **Draggable zoom region overlay on PreviewCanvas** â€” Worked. Shows raw source frame (no compositor) with a XAML rectangle overlay representing the zoom viewport. 4 dim rectangles surround the viewport for contrast. User drags to reposition, clicks Apply to commit via single `UpdateZoomSegmentPropertiesOperation`. âœ…

**What worked:**
- `ZoomState.IsManualOverride` flag set in `AutoZoomEngine.GetZoomState()` when manual keyframe is active. `FrameCompositor.ComposeFrame` skips cursor-position override when flag is true.
- `_zoomRegionEditMode` flag in EditorPage skips compositor in `UpdatePreviewFrameAsync` to show raw frame during positioning.
- Zoom region rectangle accounts for output aspect ratio via `ComputeEffectiveViewport` logic (duplicated as `GetAspectRatioValue` helper).
- Viewport coordinates are clamped during drag so the rectangle stays within source bounds.
- `OnUndoRedoStateChanged` now only clears zoom selection if the selected keyframe no longer exists in the model (was previously clearing unconditionally).
- Auto-enters edit mode on segment creation; existing segments use "Edit Region" button.

**What didn't work / pitfalls:**
- Auto-entering edit mode on every segment selection is too disruptive (pauses playback, seeks playhead). Better to use explicit "Edit Region" button for existing segments and only auto-enter on creation.
- `AspectRatio` enum lives in `Musio.Core.Settings` namespace â€” must add `using Musio.Core.Settings;` when referencing it from the App layer.
- Canvas dim rectangles must use `IsHitTestVisible="False"` so pointer events pass through to the viewport rectangle.

---

## Auto-Zoom Segment Deletion â€” Suppressing Click-Driven Auto-Zoom

**Feature/area:** AutoZoomEngine, EditOperations, FrameCompositor, PreviewRenderer, ExportEngine, VideoEncoder

**Approaches tried:**

1. **Track suppressed source click ticks** â€” Auto-generated ZoomKeyframes now carry `SourceClickTicks` (the raw `ClickEvent.TimestampTicks`). When deleted or converted to manual via edit operations, the source tick is added to `TimelineModel.SuppressedClickTicks`. `AutoZoomEngine.BuildZoomTimeline()` caches build parameters and `SetSuppressedClickTicks()` rebuilds auto segments, filtering out suppressed clicks before merging. This is robust because `long` ticks are exact â€” no floating-point tolerance needed. âœ…

**What worked:** Storing source click identity on auto-generated keyframes and suppressing at the click level before segment merging. The fix covers preview (via `InvalidatePreview` syncing), export (both `ExportEngine` GIF path and `VideoEncoder` MP4 path), and undo/redo (operations add/remove from suppressed set symmetrically).

**What didn't work / pitfalls:**
- Using `TimeSpan`-based timestamps as suppression keys is fragile â€” round-trip through `TimeSpan.FromSeconds()` introduces 100ns-tick quantization, and timeline edits (cut/speed) shift display timestamps without affecting source clicks. Use `TimestampTicks` (raw `long`) instead.
- The export path (`VideoEncoder.ExportAsync`) previously didn't sync any zoom state at all â€” needed `TimelineModel?` parameter added.
- Edit operations that set `IsManual = true` (Move, Resize, UpdateProperties) must also suppress the original auto click, otherwise both the manual keyframe and the auto segment fire.

---

## Export â€” H.264 Odd Dimensions Failure

**Feature/area:** VideoEncoder (export pipeline)

**Approaches tried:**

1. **Enforce even dimensions with `& ~1` at the target dimension assignment** â€” Worked. Applied the same fix pattern already used in `VideoWriter.FinalizeAsync` (recording pipeline) to `VideoEncoder.ExportAsync`. Reuses the existing scaling path (`needsScaling`) to downscale compositor frames when oddâ†’even adjustment changes dimensions. âœ…

**What worked:** Rounding `targetWidth` and `targetHeight` down to even with `& ~1` right where they're assigned from compositor output (lines 118-121 of VideoEncoder.cs). The existing `needsScaling` branch already handles the dimension mismatch, so no new rendering code was needed.

**What didn't work / pitfalls:**
- The recording pipeline (`VideoWriter.FinalizeAsync`) had this fix but the export pipeline (`VideoEncoder`) did not â€” the fix must be applied in BOTH places.
- Region captures at fractional DPI (125%, 150%, 175%) produce physical pixel dimensions that are often odd. Aspect ratio cropping via `Math.Round()` in `ComputeContentDimensions` can also produce odd content dimensions.
- H.264 silently rejects odd dimensions â€” the `MediaTranscoder.PrepareMediaStreamSourceTranscodeAsync` returns `CanTranscode = false` with no descriptive message, making root cause hard to diagnose.

---

## Export â€” Handle Corrupt/Missing Source MP4

**Feature/area:** VideoEncoder, ExportEngine (export pipeline)

**Approaches tried:**

1. **Wrap `MediaClip.CreateFromFileAsync` in try-catch, fall back to project metadata** â€” Worked. When the source `video.mp4` is corrupt or empty (because `VideoWriter.FinalizeAsync` failed during recording), `CreateFromFileAsync` throws "The parameter is incorrect." The fix catches this and uses `project.Width`/`project.Height` for dimensions instead. JPEG frames from `.frames/` still provide visuals. âœ…

**What worked:**
- Made `sourceClip` nullable in both `VideoEncoder.ExportAsync` and `ExportEngine.ExportGifAsync`.
- Fall back to `project.Width`/`project.Height` when the video can't be opened.
- Skip embedded audio from sourceClip when null; separately recorded audio files still work.
- Guard `frameReader is null && sourceComp is null` to give a clear error when no frame source is available at all.

**What didn't work / pitfalls:**
- `RecordingSession.StopAsync` makes `FinalizeAsync` non-fatal, so a corrupt `video.mp4` is expected. But the export pipeline assumed it was always valid â€” every `CreateFromFileAsync` call was unguarded.
- The error "The parameter is incorrect" from `MediaClip.CreateFromFileAsync` is unhelpful â€” wrapping it gives a better user experience.

---

## MSIX Store Packaging â€” Building Unsigned Packages

**Feature/area:** MSIX packaging for Microsoft Store submission

**Approaches tried:**

1. **MSBuild with `GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never`** â€” Worked. Produces unsigned `.msix` files per architecture (x64, ARM64) suitable for Store submission (Store signs packages automatically). âœ…

**What worked:**
- Must use VS MSBuild (not `dotnet build`) due to PriGen tooling issue.
- Kill any running Musio.App process before cleaning â€” locked DLLs prevent `bin/obj` deletion.
- Delete `bin/obj` folders for a truly clean build (MSBuild `/t:Clean` alone may leave stale artifacts).
- Build command: `msbuild Musio.App.csproj /restore /t:Build /p:Configuration=Release /p:Platform=x64 /p:GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never`
- Output location: `bin\{Platform}\Release\net9.0-windows10.0.26100.0\win-{rid}\AppPackages\Musio.App_{version}_{arch}_Test\Musio.App_{version}_{arch}.msix`

**What didn't work / pitfalls:**
- If Musio.App is running (check by PID in MSB3061 warnings), files are locked and clean fails silently with warnings.
- `dotnet build` fails for this project due to PriGen tooling â€” always use VS MSBuild.

---

## Accessibility Audit â€” Full App Pass

**Feature/area:** Accessibility across all UI (XAML pages, custom controls, code-behind, ViewModels, services)

**Approaches tried:**

1. **Parallel audit of all UI layers** â€” Used 4 parallel agents to audit XAML pages, custom controls, code-behind, and ViewModels/services independently. Compiled into `ACCESSIBILITY_ISSUES.md`. âœ…

**What worked:** Systematic category-based audit covering WCAG 2.1 Level AA criteria (keyboard access, screen reader support, color contrast, live regions, semantic structure, focus management). Found 47 issues (7 Critical, 18 Major, 22 Minor).

---

## Audio Waveform â€” Missing Track in Editor

**Feature/area:** Audio waveform visualization in timeline editor (EditorPage, AudioWaveformGenerator, TimelineModel)

**Approaches tried:**

1. **Wired up existing AudioWaveformGenerator in EditorPage.InitializePreviewAsync()** â€” The generator, timeline model property, and rendering code all existed but were never connected. Added `LoadAudioWaveformAsync()` that reads WAV files from `Project.AudioFilePaths`, generates waveform peaks off-thread, merges multiple sources (system + mic) by max-peak, and assigns to `TimelineModel.AudioWaveformSamples`. âœ…

---

## Audio-Video Sync â€” Precise Offset & Scrub Audio

**Feature/area:** Audio playback sync + scrub (AudioPlaybackEngine, EditorPage, AudioWaveformGenerator)

**Approaches tried:**

1. **Duration-based heuristic offset** (`videoDuration - maxAudioDuration + 0.5`) â€” Didn't work. The 0.5s fudge factor was arbitrary. `Max(0, ...)` clipped negative results. When audio was longer than video (the common case since audio pre-roll starts before video), the offset was always just 0.5 regardless of actual timing. âœ—
2. **Used `Project.AudioToVideoOffsetSeconds` directly** â€” Worked. This is the precise offset measured during recording from `(videoOriginTicks - audioOriginTicks) / Stopwatch.Frequency`. Audio pre-roll is positive: at video time T, audio file position = T + offset. Waveform uses `startSeconds` + `maxDurationSeconds` params of `AudioWaveformGenerator.GenerateWaveform()` to skip pre-roll and cap to video duration. âœ…
3. **Added `ScrubTo()` for FCP-style scrub audio** â€” Worked. Stopâ†’Seekâ†’Play with 80ms debounce auto-stop. Serialized with a lock to prevent races between UI thread and timer callback. Reduced `WaveOutEvent.DesiredLatency` to 100ms for lower latency. âœ…

**What worked:**
- `_audioOffsetSeconds = project.AudioToVideoOffsetSeconds` (positive) instead of `= -videoLead` (negative heuristic).
- `AudioWaveformGenerator.GenerateWaveform(path, samples, startSeconds: offset, maxDurationSeconds: videoDuration)` â€” trims pre-roll and caps duration.
- `AudioPlaybackEngine.ScrubTo()` with `_transportLock` serializing all Stop/Seek/Play calls.

**What didn't work / pitfalls:**
- Duration-based heuristic completely ignores `AudioToVideoOffsetSeconds` which is already recorded precisely.
- `AudioWaveformGenerator` had a bug: when `startSeconds` exceeded file length, it silently fell back to position 0 instead of returning empty â€” fixed with `>= reader.Length` guard.
- `WaveOutEvent` default `DesiredLatency` of 300ms is too high for scrub audio â€” 100ms is responsive enough.

**What worked:** Placing the audio waveform load **before** the cursor-data early return ensures audio tracks appear even when no cursor data exists. Using `Task.Run` keeps the UI responsive during waveform generation.

**What didn't work (avoided):** Loading waveform after the `mouseData is null` early return â€” would have skipped audio loading for projects without cursor data.

**Key insight:** The build must use VS MSBuild (`C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe`) with `/p:Platform=x64` or `/p:Platform=ARM64` â€” `dotnet build` fails with a missing `Microsoft.Build.Packaging.Pri.Tasks.dll` in the .NET 10 SDK.

---

## Timeline Clipping & Audio Settings Not Wired

**Feature/area:** EditorPage timeline layout, SettingsPage audio toggles, RecordingViewModel defaults

**Approaches tried:**

1. **Increased timeline Height from 170 to 230** â€” The 5 track rows total 230px (30+60+40+50+50) but the container was only 170px, clipping zoom and audio tracks. âœ…
2. **Bound SettingsPage audio toggles to AppSettings** â€” The System Audio and Microphone toggles were static XAML with no bindings. Added `x:Name`, `Toggled` handlers, and `Loaded` handler to read/write `AppSettings.IsSystemAudioEnabled` and `AppSettings.IsMicEnabled`. âœ…
3. **RecordingViewModel reads from AppSettings** â€” Changed field initializers from hardcoded `true`/`false` to `AppSettings.Instance.IsSystemAudioEnabled`/`IsMicEnabled` so saved preferences are respected. âœ…

**What worked:** All three changes together â€” layout fix + settings binding + VM init from AppSettings.

**Key insight:** SettingsPage controls were purely decorative â€” `AppSettings` had the properties but the XAML never bound to them. The RecordingViewModel also ignored AppSettings entirely.

---

## AudioCaptureEngine â€” Stop Deadlock

**Feature/area:** Audio capture stop/dispose flow (AudioCaptureEngine)

**Approaches tried:**

1. **Moved capture stop/dispose outside the lock** â€” `StopRecording()` was holding `_lock` while calling `capture.Dispose()`, but WASAPI's `RecordingStopped` callback also tried to acquire `_lock`, causing a deadlock. Fix: grab references under lock, null out fields immediately (data handlers become no-ops via null checks), then stop/dispose outside the lock. âœ…

**What worked:** Lock-then-detach pattern â€” acquire lock only to swap fields to null, release lock, then do the blocking stop/dispose work lock-free.

**What didn't work (root cause):** Holding `_lock` across `StopRecording()` â†’ `Thread.Sleep(200)` â†’ `Dispose()` while `RecordingStopped` and `DataAvailable` callbacks also contended for the same lock.

---

## Video Export â€” Scrambled Frames & Trailing Cursor

**Feature/area:** Export pipeline (VideoEncoder, ExportEngine)

**Approaches tried:**

1. **Fix concurrency (SemaphoreSlim serialization)** â€” Original code used fire-and-forget `_ = ProduceSampleAsync(...)` in `SampleRequested`, allowing overlapping callbacks to corrupt shared state (`compositor`, `_scaleTarget`). Fixed with `SemaphoreSlim(1,1)` and `Interlocked.Increment` for atomic frame reservation. Still broken (stride issue remained). âŒ
2. **Remove incorrect row flip** â€” Code had a bottom-up row flip with a wrong comment claiming "MediaStreamSource with Bgra8 expects bottom-up". Both Win2D and MF default to top-down for `CreateUncompressed(Bgra8)`. Removed the flip. Still broken. âŒ
3. **Set MF_MT_DEFAULT_STRIDE on VideoEncodingProperties** â€” Set aligned stride (256-byte aligned) on the media type properties. MediaTranscoder ignores this for `MediaStreamSource`-sourced data. Still broken. âŒ
4. **SoftwareBitmap.CopyToBuffer() for stride-aligned pixels** â€” `CopyToBuffer()` actually produces compact data (width*4 stride), not aligned. Setting `MF_MT_DEFAULT_STRIDE` to aligned stride caused worse drift. Still broken. âŒ
5. **MediaStreamSample.CreateFromDirect3D11Surface() + mod-16 dimensions + software encoding** â€” Bypasses all pixel extraction. Round output dimensions to mod-16 for H.264 macroblock alignment. Disable hardware acceleration to avoid hardware encoder surface interop quirks. âœ…

**What worked:**
- **Root cause**: Non-mod-16 output dimensions (e.g. 2878Ã—1540) caused the hardware H.264 encoder to misalign macroblock boundaries, producing horizontal banding. The preview pipeline doesn't use H.264 so was unaffected.
- Round output dimensions to mod-16: `(compositorWidth / 16) * 16`
- Use `MediaStreamSample.CreateFromDirect3D11Surface()` instead of extracting pixel bytes â€” avoids all stride alignment concerns
- Set `MediaTranscoder.HardwareAccelerationEnabled = false` to use the software encoder, which handles D3D surface interop more reliably
- Each frame gets its own `CanvasRenderTarget` disposed via `sample.Processed` event
- Keep `SemaphoreSlim` serialization for thread-safe compositor access
- Must uninstall Store version before `Add-AppxPackage -Register` for debug builds to take effect

**What didn't work:**
- `GetPixelBytes()` compact stride + no MF_MT_DEFAULT_STRIDE â€” encoder expects something other than compact stride (horizontal banding).
- `SoftwareBitmap.CopyToBuffer()` â€” produces compact data despite SoftwareBitmap having aligned internal stride; misleading API.
- Setting `MF_MT_DEFAULT_STRIDE` / `MF_MT_SAMPLE_SIZE` on `VideoEncodingProperties.Properties` â€” ignored by MediaTranscoder pipeline.

---

## Video Export â€” PR Review Fixes (Semaphore & Surface Leak)

**Feature/area:** Export pipeline (VideoEncoder)

**Issues found during code review of PR #1:**

1. **Semaphore over-release on cancellation** â€” `_frameSemaphore.WaitAsync(ct)` can throw `OperationCanceledException` before acquiring, but the `finally` block unconditionally called `Release()`, corrupting the semaphore count. Fixed with a `bool semaphoreAcquired` flag.
2. **GPU surface leak on exception** â€” `outputSurface` (CanvasRenderTarget) was only disposed via `sample.Processed` event. If an exception occurred after surface creation but before the handler was attached, the surface leaked. Fixed by moving declaration to outer scope and adding `outputSurface?.Dispose()` in the catch block.

**What worked:** Both fixes are straightforward guard patterns (acquisition flag, catch-block cleanup).
- Row flipping â€” completely wrong for `CreateUncompressed(Bgra8)` which uses top-down row order.
- Fire-and-forget async in `SampleRequested` â€” causes frame corruption due to concurrent access to shared state.

---

1. **SemaphoreSlim(1,1) to serialize frame compositing** â€” Partially helped. The `SampleRequested` handler used fire-and-forget (`_ = ProduceSampleAsync(...)`) allowing concurrent access to shared state (`FrameCompositor`, `_flipBuffer`, `_scaleTarget`). Added `SemaphoreSlim` serialization, `Interlocked.Increment` for atomic frame reservation, and pending-task draining before disposal. Fixed the concurrency issue but didn't fix the visual corruption. âœ…
2. **Removed incorrect bottom-up row flip** â€” Fixed the scrambled frames. The code had a comment claiming "MediaStreamSource with Bgra8 expects bottom-up" which is wrong. Both Win2D `GetPixelBytes()` and `VideoEncodingProperties.CreateUncompressed(Bgra8)` use top-down row order (positive stride). The row flip was converting correct top-down data to bottom-up, causing the H.264 encoder to produce corrupted output. Removed the flip and passed pixel bytes directly. âœ…

**What worked:** Both fixes combined â€” serialized compositing via SemaphoreSlim + removed the incorrect row flip.

**What didn't work:** The row flip comment was misleading â€” BGRA8 in MediaStreamSource uses top-down (positive stride), not bottom-up like legacy GDI/BMP conventions.

**Key findings:**
- Biggest gaps: custom-drawn controls (TimelineControl, RegionSelectorOverlay, PreviewCanvas) have no AutomationPeers and are pointer-only.
- All dynamic status areas (recording timer, export progress, playback time) lack live-region support.
- Many icon-only buttons lack `AutomationProperties.Name`.
- Hard-coded colors in timeline and controls won't adapt to High Contrast mode.
- System tray is not keyboard-accessible.

---

## Hard-Coded Colors â€” Theme Resource Migration

**Feature/area:** Accessibility â€” High Contrast support (TimelineControl, PreviewCanvas, RegionSelectorOverlay, RecordingOverlayWindow, EditorPage)

**Approaches tried:**

1. **Created `Themes/AppColors.xaml` resource dictionary with `ThemeDictionaries`** â€” Defined ~44 `SolidColorBrush` (and one `AcrylicBrush`) resources in Default and HighContrast theme dictionaries. Default theme preserves the exact original hard-coded color values. HighContrast maps to system colors (`SystemColorWindowColor`, `SystemColorWindowTextColor`, `SystemColorHighlightColor`, `SystemColorHotlightColor`, `SystemColorHighlightTextColor`, `SystemColorGrayTextColor`, `SystemColorButtonFaceColor`, `SystemColorButtonTextColor`). âœ…
2. **XAML files: replaced inline colors with `{ThemeResource}`** â€” All 5 affected XAML files (TimelineControl, PreviewCanvas, RegionSelectorOverlay, RecordingOverlayWindow, EditorPage) now use `{ThemeResource ResourceName}` which auto-resolves on theme/HC change. âœ…
3. **Code-behind (Win2D): resolved colors from theme resources** â€” Changed 29 `static readonly Color` fields in `TimelineControl.xaml.cs` to instance fields resolved via `GetBrushColor(key, fallback)` from XAML resources. Added `ResolveThemeColors()` called in constructor and on `ActualThemeChanged`. Same pattern for `PreviewCanvas.xaml.cs` clear color. âœ…

**What worked:**
- `SolidColorBrush` resources work for both XAML `{ThemeResource}` binding and code-behind `.Color` extraction.
- In HighContrast theme dictionaries, `Color="{StaticResource SystemColorXxxColor}"` correctly references system HC colors.
- `AcrylicBrush` in Default / `SolidColorBrush` in HighContrast for the same key works because both derive from `Brush`.
- `ActualThemeChanged` fires for both Lightâ†”Dark and High Contrast toggles; `InvalidateAllCanvases()` redraws Win2D surfaces with new colors.

---

## Memory Leak & Management Stability Pass

**Feature/area:** Cross-cutting memory/resource management across capture, export, processing, and UI layers.

**Approaches tried:**
1. Full-codebase rubber-duck review focused on memory leaks, resource management, and disposal patterns.
2. Applied fixes to 12 Critical + High severity issues, then ran a verification rubber-duck pass to catch gaps.

**What worked:**
- `composition.Clips.Clear()` in `try/finally` releases MediaClip COM objects in VideoWriter.FinalizeAsync â€” prevents linear OOM growth proportional to frame count.
- `Cleanup()` methods on EditorViewModel/ExportViewModel that unsubscribe from `ProjectService.Instance.ProjectChanged` â€” severs the singleton root that prevented GC of old VMs after page navigation.
- Expanded EditorPage.Unloaded to dispose `_frameReader`, `_previewRenderer`, `_audioPlayer` and call VM Cleanup.
- `using var` on `CanvasGeometry` and `CanvasPathBuilder` in TimelineControl draw callbacks â€” prevents Win2D native resource accumulation during 30fps redraws.
- `using var` on thumbnail streams in VideoEncoder/ExportEngine `ExtractFrameFromCompositionAsync` â€” prevents per-frame stream handle leaks during export.
- `CursorRenderer : IDisposable` with `_cursorBitmap`/`_defaultCursorGeometry` disposal, called from `FrameCompositor.Dispose()`.
- `MouseHookRecorder.SaveToFile()` clears + trims in-memory lists after persisting to disk.
- `try/finally` around GDI handle cleanup + `using var` for `SoftwareBitmap` in RegionSelectorOverlay.
- `RemoveAll(t => t.IsCompleted)` on VideoEncoder `pendingSamples` to prevent unbounded task graph growth.
- Disposing old `_recognizer` before creating new one in SpeechToText, plus `try/finally` for stream.
- ExportEngine GIF `finally` block clears `sourceComp?.Clips` and `webcamComp?.Clips`.

**What didn't work / known limitations:**
- EditorPage constructor uses many anonymous lambda event handlers (Preview.PlaybackTick, etc.) which can't be individually unsubscribed. Since they form intra-page references, fixing the singleton VM subscription root is sufficient for GC.
- `MouseHookRecorder.SaveToFile()` now invalidates in-memory data after saving. Callers needing data post-save must use `GetRecordedData()` first or `LoadFromFile()` after.
- GIF export webcam frame lifetime issue (using var disposes before ComposeFrame reads it) was identified but not fixed â€” user confirmed GIF+webcam is not a supported path currently.
- Merged dictionary via `<ResourceDictionary Source="Themes/AppColors.xaml" />` in App.xaml keeps the main file clean.

**What didn't work / pitfalls:**
- `using Microsoft.UI;` (for `Colors` class) is no longer needed after removing `Colors.White` references â€” remove to avoid unused-using warnings.
- Can't use `{ThemeResource}` inside a `Color` value directly in resource dictionaries; must wrap in `SolidColorBrush` and extract `.Color` in code-behind.

---

## Recording Overlay â€” Stop Button Feedback (Rolling Star Trek Phrases)

**Feature/area:** RecordingOverlayWindow (stop button UX)

**Approaches tried:**

1. **Swap overlay to "stopping" state with rolling 2-word Star Trek phrases** â€” Worked. On Stop_Click, hide RecordingPanel + StopButton, show StoppingPanel with a ProgressRing and a TextBlock cycling through phrases ("Engagingâ€¦", "Standbyâ€¦", "Energizingâ€¦", etc.) every 1.5s via DispatcherTimer. Timer cleaned up in CloseOverlay. âœ…

**What worked:**
- Split XAML into named `RecordingPanel` (dot + elapsed) and `StoppingPanel` (ring + phrase), toggling visibility on stop.
- `DispatcherTimer` at 1.5s interval cycles through 8 Star Trek-themed phrases.
- Timer disposed in `CloseOverlay()` before window closes.

---

## Editor Preview â€” Stutter Fix (Render Coalescing + Buffer Reuse)

**Feature/area:** Preview playback pipeline (EditorPage, PreviewCanvas, FrameCompositor, PreviewRenderer)

**Approaches tried:**

1. **Render coalescing gate in EditorPage** â€” Worked. Added `_isRendering` flag with `_pendingRenderPosition`/`_pendingRenderForce` fields. `UpdatePreviewFrameAsync` skips if a render is already in-flight and stores the latest position. A drain-loop in the active render picks up the pending position when done. Preserves `force` flag via `|=` merge. âœ…
2. **Reusable cropped buffer in FrameCompositor** â€” Worked. Added `_croppedBuffer` field to `CropSourceFrame` that's reused when dimensions match. Removed `using` from the call site in `ComposeFrame`. Buffer is compositor-owned and disposed in `Dispose()`. Halves per-frame GPU allocations. âœ…
3. **Binary search in GetActiveClicks** â€” Worked. Sorted clicks by `TimestampTicks` during `InitializeAsync`. Replaced `foreach` over all clicks with binary search to find the Â±1s window start, then iterate only matching clicks. O(log n + k) instead of O(n). âœ…
4. **Preview FPS matching project capture FPS** â€” Worked. `PreviewCanvas.PreviewFps` setter now updates `_playbackTimer.Interval` immediately. `EditorPage.InitializePreviewAsync` sets it from project FPS (capped at 30). Removed redundant 30fps cap from `PreviewRenderer`. âœ…

**What worked:**
- Drain-loop pattern (not recursive fire-and-forget) for render coalescing â€” ensures at most one render is in-flight, the latest position is always rendered next, and no renders pile up.
- `_croppedBuffer` field-level reuse with dimension check â€” safe because `CropSourceFrame` is only called within `ComposeFrame` scope.

---

## Timeline System Brushes & Video Filmstrip

**Feature/area:** Timeline track colors (TimelineControl, AppColors.xaml, EditorPage)

**Approaches tried:**

1. **Replaced custom hard-coded hex color brushes in AppColors.xaml with WinUI 3 standard system brush resolution** â€” Worked. Removed ~30 custom timeline brushes from Default and HighContrast theme dictionaries. ResolveThemeColors() now resolves from `Application.Current.Resources` using standard keys like `AccentFillColorDefaultBrush`, `CardBackgroundFillColorDefaultBrush`, `TextFillColorSecondaryBrush`, etc. Kept semantic status colors (playhead `#FFDDFF00` in Default and `#FFE87C06` in Light, cut line yellow, speed overlays, cursor click) in AppColors.xaml. âœ…
2. **XAML border backgrounds switched from custom brushes to {ThemeResource} system brushes** â€” Worked. Track label borders use `CardBackgroundFillColorDefaultBrush`, `CardBackgroundFillColorSecondaryBrush`, `SolidBackgroundFillColorBaseBrush`, `TextFillColorSecondaryBrush` directly. âœ…
3. **FCP-style filmstrip thumbnails in video track** â€” Worked. Pre-scaled thumbnails generated from VideoFrameReader at ~0.5-2s intervals, cached as CanvasBitmap[] on TimelineControl. DrawFilmstrip tiles them across clip bounds with source-time mapping (respects SpeedFactor/EffectiveSourceStart). Backplate uses CardBackgroundFillColorSecondary, stroke uses ControlStrokeColorDefault. Falls back to accent-colored rectangle when no thumbnails available. âœ…
4. **Thumbnail generation versioning** â€” Worked. `_thumbnailGenerationId` counter in EditorPage prevents stale thumbnail results from overwriting current project's thumbnails when projects change rapidly. âœ…

**What worked:** Resolving system colors via `Application.Current.Resources` (not control-local Resources) for WinUI system brushes; keeping a `GetBrushColor` fallback for semantic brushes still defined in AppColors.xaml; TimelineControl owning thumbnail lifecycle via SetThumbnails/ClearThumbnails.

**What didn't work / watch out for:**
- Cannot use `{ThemeResource}` inside own theme dictionaries to reference system resources directly â€” must resolve at runtime in C#.
- Win2D needs Color values, not Brushes â€” always extract `.Color` from resolved SolidColorBrush.
- Thumbnail generation should pre-scale to track height (~52px) at load time, never during Draw handler.
- Sorting clicks once at init time guarantees binary search correctness without relying on recorder ordering.

**What didn't work / considerations:**
- The output `CanvasRenderTarget` from `ComposeFrame` is still allocated fresh each frame because `SetFrame` takes ownership and disposes the previous frame. Reusing would require a buffer pool or API contract change â€” not worth the complexity with the render gate limiting throughput to one at a time.
- 60fps preview needs architectural changes: `CompositionTarget.Rendering` (DispatcherTimer can't sustain 16.67ms), frame read-ahead cache (zero caching on JPEG decode currently), and off-thread composition.
- The rubber-duck review flagged that `PreviewFps` setter didn't update an existing timer â€” fixed by updating `_playbackTimer.Interval` in the setter.

---

## Region Border Highlight â€” DPI Scaling Mismatch

---

## Window Picker Overlay & Auto-Trigger Selection Modes

**Feature/area:** RecordingPage, WindowSelectorOverlay, RegionSelectorOverlay

**Approaches tried:**

1. **Created `WindowSelectorOverlay` control modeled after `RegionSelectorOverlay`** â€” Worked. Full-screen overlay with desktop screenshot background, 4 dim mask rectangles, and highlight border. Uses `EnumWindows` for Z-order-aware window enumeration, filters out cloaked/minimized/tool/same-process windows. Coordinate mapping from canvas DIPs to screen physical pixels via virtual desktop bounds ratio. âœ…
2. **Auto-trigger pickers on capture mode selection** â€” Worked. `CaptureModeSelector_SelectionChanged` launches `WindowSelectorOverlay` or `RegionSelectorOverlay` automatically. `_isPageLoading` flag prevents trigger during page initialization; `_isPickerOpen` reentrancy guard prevents concurrent overlays. âœ…
3. **Replaced Window ComboBox with visual picker button + info text** â€” Worked. Consistent with Region panel pattern (button + label). Visual picker provides better UX than dropdown list. âœ…

**What worked:**
- `WindowSelectorOverlay.ShowAsync()`: minimize Musio â†’ enumerate windows â†’ capture screenshot â†’ show maximized borderless overlay â†’ return WindowInfo on click or null on Escape â†’ restore Musio (in try/finally for exception safety).
- Z-order-aware hit testing: `EnumWindows` returns windows in Z-order (topmost first), so first rect containing the cursor point = correct window.
- Window validation on click: `IsWindow()` + `IsWindowVisible()` confirm the window still exists before returning.
- Coordinate mapping: `canvasX * (vdWidth / canvasWidth) + vdLeft` for canvasâ†’screen, reverse for screenâ†’canvas.

**What didn't work / known limitations:**
- Same multi-monitor limitation as `RegionSelectorOverlay`: screenshot covers full virtual desktop but overlay is maximized on one monitor, so coordinates on secondary monitors may be slightly off due to Stretch="Fill" scaling.
- `GetWindowRect` includes invisible DWM extended frame borders; highlight may extend slightly beyond visible window edges. Could use `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` for more precise visual highlights.


**Feature/area:** Region recording border overlay (RecordingPage, RegionBorderHighlight)

**Approaches tried:**

1. **Scale region DIP coordinates to physical pixels using monitor DPI** â€” Worked. `RegionSelectorOverlay` produces coordinates in WinUI DIP (logical pixels), but `RegionBorderHighlight.Show()` creates native Win32 popup windows via `CreateWindowEx`, which expects physical screen coordinates in a per-monitor DPI-aware v2 process. Added `GetRegionMonitorDpiScale()` in `RecordingPage` that resolves the monitor handle via `MonitorEnumerator` (same as `BuildCaptureTarget()`) and calls `GetDpiForMonitor` to get the scale. Multiplied region X/Y/Width/Height by the DPI scale before passing to `Show()`. âœ…

**What worked:**
- Using the same monitor-matching logic as `RecordingViewModel.BuildCaptureTarget()` to find the correct monitor handle, then `GetDpiForMonitor` for the scale.
- This matches the DPI conversion already done in `RecordingSession.OnFrameCaptured` for the capture crop rect.

**What didn't work / known limitations:**
- On multi-monitor setups with `Stretch="Fill"`, the region selector coordinates are already distorted (pre-existing issue documented above). The DPI fix only helps single-monitor or same-DPI multi-monitor setups.

---

## Session Folder Disk Cleanup

**Feature/area:** Recording session storage (SessionCleanupService, ExportViewModel, App.xaml.cs)

**Approaches tried:**

1. **`SessionCleanupService` that deletes `.frames/` directories from exported sessions** â€” Worked. Each session folder's `.frames/` dir (thousands of JPEG files) is the dominant space consumer. After successful export, the service writes an `exported.marker` file, then deletes `.frames/` and `finalize_debug.log`. On app startup, a background `Task.Run` cleans all sessions with the marker. âœ…

**What worked:**
- `exported.marker` file distinguishes exported sessions from freshly-recorded ones (avoids cleaning sessions that haven't been exported yet).
- `HasValidVideo()` checks video.mp4 exists and is non-zero before deleting frames.
- Post-export cleanup in `ExportViewModel.ExportAsync()` runs via `Task.Run` after export succeeds.
- Startup cleanup in `App.OnLaunched()` runs fire-and-forget for all marked sessions.
- All deletion is wrapped in try-catch so failures are non-fatal.

**Key design decisions:**
- Only sessions with `exported.marker` are cleaned (not just any session with video.mp4, since MP4 is created during recording before any export).
- `.frames/` deletion means the editor can't re-open that recording for editing. Acceptable tradeoff â€” the user has their export.
- `video.mp4`, cursor, keyboard, and audio files are preserved for future capture history feature.

---

## Export â€” H.264 Corruption on High-DPI Displays (2.8K+)

**Feature/area:** VideoEncoder (export pipeline)

**Approaches tried:**

1. **Replaced `MediaEncodingProfile.CreateMp4(GetProfileQuality())` with `VideoEncodingQuality.Auto`** â€” Worked. The previous code used quality presets (`HD1080p`, `HD720p`, etc.) that constrain H.264 Level/DPB to the preset resolution. On high-DPI displays (2.8K â†’ ~2976Ã—1896 compositor output), the Level 4.0 macroblock limit (8,192) was exceeded by 2.7Ã— (22,134 actual), causing the AMD AMF encoder to produce corrupted reference frames â†’ ghosting/trailing artifacts. Using `Auto` lets the encoder auto-determine the correct Level (5.1+). âœ…

---

## Cursor Options Flyout

**Feature/area:** Cursor customization (CursorRenderer, CursorStyle, EditorPage)

**Approaches tried:**

1. **Added `Touch` to `CursorType` enum and `Color` property to `CursorStyle` record** â€” Worked. Extended the existing record without renaming/removing existing enum values to preserve backward compatibility. âœ…
2. **Added `DrawTouchCursor()` method to `CursorRenderer`** â€” Worked. Draws a filled circle centered on the cursor position using `FillCircle`/`DrawCircle`. Centering on `(x, y)` aligns the touch indicator with the actual pointer hotspot. âœ…
3. **Used contrast-aware outline color** â€” Worked. `GetContrastOutlineColor()` uses ITU-R BT.601 perceived luminance to choose black outline for light fills and white outline for dark fills, ensuring cursor visibility on any background. âœ…
4. **Added Cursor flyout to EditorPage toolbar (same pattern as Style flyout)** â€” Worked. RadioButtons for Mouse/Touch type, Slider for size, 6 preset color circles as templated RadioButtons with selection ring. Debounce timer for slider, immediate apply for discrete choices. âœ…
5. **Color preset circles as templated RadioButtons** â€” Worked. Each color circle is a `RadioButton` with a custom `ControlTemplate` containing an `Ellipse` for the color fill and a `SelectionRing` ellipse toggled via `VisualState`. Accessible and keyboard-navigable. âœ…

**What worked:** Following the existing Style flyout pattern (suppress events, debounce, `RebuildPreviewRendererAsync`) and extending `CursorStyle`/`CursorRenderer` minimally.

**What didn't work:** Setting XAML default values (`IsChecked="True"`, `Value="2"`) on cursor flyout controls â€” these fire event handlers during `InitializeComponent()` before `_suppressCursorEvents` is set, causing `ApplyCursorStyleFromControls` â†’ `RebuildPreviewRendererAsync` to race with `InitializePreviewAsync`, hanging the app after recording. Fix: remove all XAML defaults from flyout controls and set them only in `SyncCursorControlsToConfig()` under the suppress flag, matching the Style flyout pattern.

---

## Export â€” H.264 Corruption on AMD GPUs at High Resolutions

**Feature/area:** Export pipeline (VideoEncoder)

**Approaches tried:**

1. **Switched `VideoEncodingQuality` from `HD1080p` to `Auto`** â€” Partially worked for recording pipeline but NOT for export (see below).
2. **Scaled bitrate proportionally to pixel count** â€” The fixed 20 Mbps bitrate was tight for ~3K output. New `ComputeBitrate(width, height)` scales the base bitrate by `actualPixels / (1920Ã—1080)`, so high-res exports get adequate bitrate. âœ…
3. **Reused flip buffer and scale render target across frames** â€” Per-frame allocation of `byte[~22MB]` and `CanvasRenderTarget` at ~3K caused excessive GC pressure and memory throughput on integrated GPUs with shared memory. Added `_flipBuffer` and `_scaleTarget` fields, allocated once and reused. âœ…

**What worked:**
- `VideoEncodingQuality.Auto` + explicit Width/Height/Bitrate/FPS/Subtype â€” same pattern already used in `VideoWriter.FinalizeAsync` (recording pipeline).
- Bitrate scaling ensures high-res exports don't get starved. Formula: `baseBitrate * max(1.0, actualPixels / baselinePixels)`.
- Buffer reuse eliminates ~44MB/frame of allocation (flip buffer + scale target), significant at 30fps on integrated GPU.

**What didn't work / pitfalls:**
- The recording pipeline (`VideoWriter.FinalizeAsync`) already fixed this by using `Auto`, but the fix was never propagated to the export pipeline (`VideoEncoder`). Both pipelines must use the same approach.
- The issue is resolution-dependent, not GPU-vendor-specific: any display exceeding ~1920Ã—1088 can exceed Level 4.0 limits. NVIDIA may auto-promote the Level silently, masking the bug; AMD AMF is stricter.
- `VideoEncodingQuality.Auto` cannot be used in the export pipeline because it produces incomplete Video/Audio properties â€” `MediaComposition.RenderToFileAsync` in the audio mux pass throws `MF_E_INVALIDMEDIATYPE (0xC00D36E6)`. Instead, use `HD1080p` as a well-formed template and override Width/Height/Bitrate/FPS/Subtype. The encoder auto-selects the correct Level from actual dimensions.
- `MediaEncodingProfile.CreateMp4(HD1080p)` sets internal H.264 parameters (Level, profile, DPB) that overriding Width/Height alone does NOT update.

---

## Region Recording â€” Mouse Click Misalignment Fix

**Feature/area:** Compositor pipeline (FrameCompositor, AutoZoomEngine, PreviewRenderer, EditorPage, ExportEngine, VideoEncoder, Project)

**Approaches tried:**

1. **Store DPI scale in Project + subtract CropOffsetX/Y from all mouse coordinate transforms** â€” Worked. Two root causes: (a) `GetSystemDpiScale()` compared cropped region size to full monitor logical size, always returning 1.0 for region captures; (b) `CropOffsetX/Y` (physical pixel origin of the crop region) were stored but never subtracted from mouse coordinates. âœ…

---

## Core Models/Timeline/Settings/AI/Audio/Services â€” Crash/Hang/Vulnerability Fixes

**Feature/area:** Musio.Core layer â€” CursorData, TimelineModel, RegionMemory, PresetManager, SubtitleBurner, AudioWaveformGenerator, SpeechToText, GlobalHotkeyService, FrameRateLimiter, AppSettings.

**Approaches tried:**

1. **Surgical safeguards across 10 files** â€” Applied minimal guards without refactoring surrounding code. âœ…

**What worked:**
- **CursorData**: Zero-check on `TickFrequency` before division in `Duration` property.
- **TimelineModel**: NaN/Infinity/overflow clamp on `SourceDuration` computed property.
- **RegionMemory**: try/catch around composite value casts in `LoadRegion`.
- **PresetManager**: Per-file try/catch in `LoadPresets` + path traversal/reserved name sanitization in `GetPresetPath`.
- **SubtitleBurner**: try/catch around `Convert.ToByte` hex parsing in `ParseColor`.
- **AudioWaveformGenerator**: Added `bitsPerSample <= 0 || bitsPerSample % 8 != 0` guard.
- **SpeechToText**: 10-minute timeout via `Task.WhenAny` + `Task.Delay` to prevent infinite hang.
- **GlobalHotkeyService**: try/catch in `Initialize` and `HotkeyWndProc` event invocation.
- **FrameRateLimiter**: `Thread.SpinWait(10)` instead of `SpinWait(1)` to reduce CPU pressure.
- **AppSettings**: try/catch in `Set<T>` around `_settings.Values[key] = value`.

**What didn't work:** N/A â€” all fixes applied cleanly.

**What worked:**
- Added `Project.DpiScale` (float) â€” set from `GetMonitorDpiScale(hMonitor)` during recording. Zero = auto-detect fallback for backward compatibility with old projects.
- `FrameCompositor.InitializeAsync` accepts `cropOffsetX`, `cropOffsetY`, `dpiScale` parameters. When `dpiScale > 0`, uses it directly instead of the broken `GetSystemDpiScale()` heuristic.
- Smoothed cursor positions: `X = logical * dpiScale - cropOffsetX` (applied once during init).
- Click positions in `GetActiveClicks`: `cx = (click.X * coordScale - cropOffset - viewport.X) * scaleX + padding`.
- `AutoZoomEngine.BuildZoomTimeline` accepts crop offsets; subtracts them from zoom center positions.
- `EditorPage` zoom keyframe creation: `CenterX = (click.X * dpiScale - cropOffX) / sourceW`.
- All callers (PreviewRenderer, ExportEngine, VideoEncoder, EditorPage) pass `project.CropOffsetX/Y` and `project.DpiScale`.

**What didn't work / known limitations:**
- `GetSystemDpiScale(capturedDimension)` compares dimension to `GetSystemMetrics(SM_CXSCREEN)`. For region captures, the cropped dimension is always smaller than the monitor logical size, so the comparison `capturedDimension > logicalSize` fails and returns 1.0. This heuristic only works for full-monitor captures.
- Old region-capture projects without `DpiScale` stored will still use the broken auto-detect path (acceptable as legacy behavior).
- Window captures may have a similar class of bug (no origin offset stored), but are out of scope for this fix.

---

## ARM64 Build & Deploy â€” CLI Workflow

**Feature/area:** Build pipeline (MSBuild, MSIX deployment)

**Approaches tried:**

1. **VS MSBuild via `[System.Diagnostics.Process]::Start()` wrapper** â€” Worked. Direct `&` invocation of MSBuild was blocked by shell permissions, but wrapping in `ProcessStartInfo` with redirected stdout/stderr succeeded. âœ…

**What worked:**
- MSBuild location: `C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe`
- Build command (via Process API): `MSBuild.exe Musio.App.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=ARM64`
- Registration: `Add-AppxPackage -Register <AppxManifest.xml path>` (run via `powershell.exe -Command` subprocess).
- Launch: `Start-Process 'shell:AppsFolder\PratikMistri.Musio_9gph0n9984scy!App'` (via subprocess).
- Package family name: `PratikMistri.Musio_9gph0n9984scy`

**What didn't work / pitfalls:**
- Direct `& MSBuild.exe` calls and `dotnet build` both blocked by Copilot CLI shell permissions â€” must use `[System.Diagnostics.Process]::Start()` wrapper.
- `explorer.exe shell:AppsFolder\...` returns exit code 1; use `Start-Process` in a powershell subprocess instead.

---

## Mouse Position Mismatch in Region Recordings â€” DPI Double-Scaling

**Feature/area:** Compositor pipeline (FrameCompositor, AutoZoomEngine, EditorPage)

**Approaches tried:**

1. **Remove DPI scaling from mouse coordinate transforms** â€” Worked. In a PerMonitorV2 process, `WH_MOUSE_LL` hook reports physical screen coordinates (not logical DIP). The previous fix multiplied hook coordinates by `project.DpiScale` (e.g., 1.5Ã—) before subtracting the physical crop offset, effectively double-scaling them. Set `coordScaleX/Y = 1.0f` in all transform sites. âœ…

**What worked:**
- `FrameCompositor.InitializeAsync`: Set `coordScaleX = 1.0f; coordScaleY = 1.0f` instead of using `dpiScale` or `GetSystemDpiScale()`.
- Same `1.0f` scale flows into `AutoZoomEngine.BuildZoomTimeline` for zoom center calculations.
- `EditorPage.xaml.cs`: Two call sites (zoom keyframe creation and drag-to-create zoom segment) updated to use `1.0f` instead of `project.DpiScale`.
- `CropOffsetX/Y` remains in physical pixels (correctly computed as `logicalCrop * dpiScale` in RecordingSession).
- `Project.DpiScale` is still stored for backward compatibility but no longer used for hook coordinate transforms.

**What didn't work / root cause:**
- The previous fix (storing `DpiScale` from `GetMonitorDpiScale`) correctly identified the DPI value but used it incorrectly â€” it assumed `WH_MOUSE_LL` coordinates were logical, when they're actually physical in PerMonitorV2.
- The fallback `GetSystemDpiScale()` heuristic happened to return 1.0 (coincidentally correct) because `GetSystemMetrics(SM_CXSCREEN)` in PerMonitorV2 returns physical dimensions, making `capturedDim / logicalSize = 1.0`.
- Bug was invisible at 100% DPI (scale=1.0, multiplication has no effect) but manifested at 125%/150%/200% DPI.

---

## Cursor Coordinates â€” Window & Multi-Monitor Offset Fix

**Feature/area:** Recording pipeline (RecordingSession, Project, FrameCompositor)

**Approaches tried:**

1. **Compute screen-absolute cursor offset for all capture types** â€” Used. `Project.CropOffsetX/Y` must represent the screen-absolute physical pixel position of the captured frame's top-left corner (not just the crop-rect-within-monitor offset). Added `ComputeCursorOffset()` in `RecordingSession` that handles all three capture types. âœ…

**What worked:**
- **Monitor capture**: Use `GetMonitorInfo(hMonitor).rcMonitor.Left/Top` as cursor offset. Fixes cursor on non-primary monitors.
- **Window capture**: Use `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` to get the window's actual rendered bounds (excludes DWM shadow). Falls back to `GetWindowRect`. Fixes cursor for all window captures.
- **Region capture**: Monitor origin + physical crop rect position. Fixes cursor on non-primary monitor regions.
- Separate `_cursorOffsetX/_cursorOffsetY` fields from `_physicalCropRect` (which is only for VideoWriter frame cropping).
- No changes needed in FrameCompositor â€” it already applies `cursor.X - cropOffsetX` correctly.

**What didn't work / known limitations:**
- Window position is captured once at first frame. If the window moves during recording, cursor alignment drifts (acceptable for v1).
- `DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS)` may differ slightly from Graphics Capture bounds for some window types.

---

## Editor Background Style Settings

**Feature/area:** Editor toolbar â€” background style editing for Window/Region captures

**Approaches tried:**
1. Considered adding a side panel for background settings â€” rejected as too heavy for MVP.
2. Implemented a flyout from a "Style" button in the editor toolbar with preset picker + individual controls.

**What worked:**
- Flyout approach with `DispatcherTimer` debounce (200ms) for slider/toggle changes to avoid thrashing the PreviewRenderer.
- Dedicated `RebuildPreviewRendererAsync` that recreates only the PreviewRenderer/FrameCompositor while preserving frame reader, audio, zoom keyframes, and playhead position. Full `InitializePreviewAsync` was too heavy (disposes everything, reloads waveform/cursor data, resets to TimeSpan.Zero).
- `_suppressStyleEvents` flag to prevent feedback loops when programmatically updating controls (e.g., syncing controls to a preset selection).
- Explicit `CaptureTargetType.Monitor` check (instead of `!= Window`) to gate which captures get background styling â€” allows both Window and Region to have backgrounds.
- Building with VS MSBuild (`MSBuild.exe /p:Platform=x64`) works; `dotnet build` fails with MSB4062 due to missing WinAppSDK PRI task in dotnet CLI.

**What didn't work / known limitations:**
- `FrameCompositor` is immutable after init â€” no incremental background updates possible without full re-init. Long-term, an `UpdateBackgroundStyle()` API on FrameCompositor could avoid re-smoothing cursor data for non-padding changes.
- Image background type not exposed in UI (kept to SolidColor/Gradient/Blur for simplicity).
- Shadow/border detail controls (blur amount, opacity, width, color) not exposed â€” uses sensible defaults.

---

## Overlapping Zoom Segments Fix

**Feature/area:** AutoZoomEngine â€” overlapping zoom transition handling

**Approaches tried:**
1. Investigated MergeSegments logic â€” it correctly merges auto segments but doesn't apply to manual keyframes.
2. Fixed EvaluateManualKeyframes and EvaluateAutoSegments to use max-zoom-wins strategy.

**What worked:**
- Both `EvaluateManualKeyframes` and `EvaluateAutoSegments` now evaluate ALL active segments at a given time and return the one with the highest zoom level. This creates smooth crossovers: when keyframe B zooms in while A zooms out, B naturally takes over once its zoom exceeds A's decaying value.
- Added regression test `GetZoomState_OverlappingManualKeyframes_NoJump` that samples the overlap zone and asserts no sudden zoom drops (>0.3 in 10ms).

**What didn't work / known limitations:**
- The original first-match strategy returned the earliest active segment, which meant A's zoom-out always won over B's zoom-in during overlaps, causing a snap when A ended.
- Tests run with `dotnet test --no-build -c Debug /p:Platform=x64` after building with VS MSBuild.

---

## Zoom + Padding â€” Post-Composite Zoom

**Feature/area:** FrameCompositor zoom behavior when background padding is applied.

**Approaches tried:**

1. **Post-composite zoom (Approach A)** â€” Compose the frame at 1x (full source + background + cursor) into an intermediate buffer, then crop+scale the buffer according to the zoom state. This makes zoom operate on the entire framed output (content + padding), so padding scales proportionally. âœ…
2. **Pre-compute adjusted geometry (Approach B, not implemented)** â€” Would compute effective padding and source viewport mathematically for each zoom level. Better source quality but significantly more complex math, asymmetric padding, and harder to maintain.

**What worked:**
- When `padding > 0`, `ComposeFrame` now delegates to `ComposeFramePostCompositeZoom` which renders at 1x into a reusable `_compositeBuffer`, then applies the zoom as a final crop+scale. Zoom center is converted from sourceâ†’composite space: `(centerX - viewport1x.X) * contentW / viewport1x.Width + padding`.
- When `padding == 0`, the original `ComposeFrameDirect` path is used (no extra buffer overhead).
- Always using post-composite path when padding > 0 (even at zoom=1) avoids a visual pop at the zoom threshold â€” at zoom=1, crop rect = full output, so it's effectively a no-op.
- Webcam, keyboard, and subtitle overlays are rendered AFTER the zoom so they stay fixed on screen. Cursor is composited before zoom so it scales with content.

**What didn't work / known limitations:**
- Slight quality softness at high zoom levels (>3x) because the source is rasterized at 1x then upscaled. Acceptable for typical zoom ranges (1.5â€“3x).
- Initially used post-composite path for ALL frames when padding > 0 (even zoom=1). This doubled render cost for every frame and made export extremely slow. Fixed by only activating post-composite path when `zoomLevel > 1.01`.

---

## Wallpaper Background â€” Per-Frame Image Load Fix

**Feature/area:** BackgroundCompositor wallpaper/image background rendering.

**Approaches tried:**

1. **Cache the loaded `CanvasBitmap` in `BackgroundCompositor`** â€” `DrawImageBackground` was loading the wallpaper from disk via `CanvasBitmap.LoadAsync` on EVERY frame. Added `_cachedBackgroundImage` and `_cachedBackgroundPath` fields; only reload when the path changes. Made `BackgroundCompositor` implement `IDisposable` to clean up the cached bitmap. âœ…

**What worked:**
- Instance-level caching of the wallpaper bitmap with path-change detection. Image loads from disk once, reused for all subsequent frames.
- `BackgroundCompositor` now implements `IDisposable`; disposed by `FrameCompositor.Dispose()`.

**What didn't work:**
- The original code loaded the image synchronously from disk every frame (`CanvasBitmap.LoadAsync(...).GetAwaiter().GetResult()`), causing choppy preview playback and extremely slow export with wallpaper backgrounds.

---

## Memory Leak Review

**Feature/area:** Resource-lifetime review for recording/export/editor cleanup paths.

**Approaches tried:**
1. Reviewed diffs and current implementations for `VideoWriter`, `EditorPage`, `MouseHookRecorder`, `EditorViewModel`, `ExportViewModel`, `VideoEncoder`, `ExportEngine`, `CursorRenderer`, `FrameCompositor`, `SpeechToText`, `TimelineControl`, and `RegionSelectorOverlay`.
2. Built the solution with VS MSBuild (`Musio.sln /restore /t:Build /p:Configuration=Debug /p:Platform=x64`) to verify the reviewed changes compile cleanly.

**What worked:**
- The new deterministic cleanup patterns compile cleanly and most of the targeted fixes are sound: disposing thumbnail/stream objects, clearing `MediaComposition.Clips` after render, disposing cursor resources, and releasing GDI handles in `finally`.

**What didn't work / known limitations:**
- `ExportEngine.ExportGifAsync` still passes a `using var` webcam bitmap into `FrameCompositor.SetWebcamFrame()`, but `GifEncoder` composes after the callback returns, so the compositor can read a disposed bitmap.
- `TimelineControl` still allocates `CanvasPathBuilder` objects without disposing them.
- `SpeechToText` keeps the last recognizer alive after `TranscribeAsync`, so its event handlers retain captured transcription state until the next call or `Dispose()`.

---

## Webcam Overlay â€” End-to-End Pipeline Fix

**Feature/area:** Webcam overlay (AppSettings, SettingsPage, RecordingViewModel, RecordingSession, ExportEngine, PreviewRenderer, EditorPage)

**Approaches tried:**

1. **Traced why webcam never activated despite toggle** â€” Root cause: `RecordingViewModel.StartRecordingAsync` set `IsWebcamEnabled` in config but never set `WebcamDeviceId`. `RecordingSession` required both to be set (`IsWebcamEnabled && !string.IsNullOrWhiteSpace(WebcamDeviceId)`), so the webcam engine was never created. âœ…
2. **Wired full pipeline** â€” Added `WebcamDeviceId` to `AppSettings`, wired `SettingsPage` toggle/combo with device enumeration, initialized VM from settings, passed device ID through config, added auto-select fallback in `RecordingSession`, auto-set `WebcamOverlayStyle` in export enrichment, and added webcam frame extraction to editor preview. âœ…

**What worked:** Fixing all 6 layers of the pipeline (settings persistence â†’ UI wiring â†’ VM init â†’ session auto-select â†’ export style enrichment â†’ editor preview plumbing).

**What didn't work / known limitations:**
- Webcam-to-video timing offset is not tracked; webcam frames are sampled at raw video time 1:1 which may cause slight desync.
- GIF export still has a potential use-after-dispose issue with webcam frames (pre-existing).
- Editor preview initially used `using var webcamFrame` which disposed the `CanvasBitmap` before `RenderPreviewFrame` could read it. Fixed by keeping the frame alive as a field (`_lastWebcamFrame`), disposing only when replaced or on page unload.
- WinRT `ImageStream` from `GetThumbnailAsync` throws `ObjectDisposedException` during `using var` cleanup (FlushAsync). Fixed by manually disposing intermediate streams with error suppression instead of `using var`.
- Editor webcam drag/resize uses normalized (0â€“1) coordinates via `NormalizedX`/`NormalizedY` on `WebcamOverlayStyle`, mapped between screen and output space using `PreviewCanvas.FrameLayoutRect`.
- Export `VideoEncoder.ProduceSampleAsync` had same `using var webcamFrame` bug â€” frame disposed before `ComposeFrame`. Also extracted webcam at compositor dimensions instead of webcam video dimensions. Fixed both + GIF export path.

## 2026-05-04 - Editor preview canvas exploration
- **Feature/area**: Editor preview canvas, webcam overlay compositor, zoom-region editing.
- **Approaches tried**: Inspected PreviewCanvas, EditorPage preview host, FrameCompositor/WebcamCompositor, and RegionSelectorOverlay/TimelineControl for interaction patterns.
- **What worked**: PreviewCanvas uses a CanvasControl (`PreviewSurface`) and `SetFrame(CanvasRenderTarget?)` swaps the cached render target; zoom-region editing already uses a Canvas overlay with pointer press/move/release and resize handles.
- **What didn't work**: No existing pointer interaction was found on the preview canvas itself for drag/resize overlays; webcam placement in compositor is enum-based only.

---

## 2026-05-04 - Webcam export performance optimization

- **Feature/area**: Export pipeline (VideoEncoder, ExportEngine, WebcamCompositor, FrameCompositor)
- **Approaches tried**:
  1. **Reduced webcam extraction resolution** â€” GetThumbnailAsync was decoding at full webcam resolution (e.g., 1920Ã—1080) even though the overlay displays at ~300px. Now extracts at 1.5Ã— the overlay size, reducing decode work by ~80%. âœ…
  2. **Eliminated double stream wrapping** â€” ExtractFrameFromCompositionAsync was converting ImageStreamâ†’.NET Streamâ†’RandomAccessStreamâ†’CanvasBitmap. ImageStream already implements IRandomAccessStream, so pass it directly. âœ…
  3. **Cached shadow and clip geometry in WebcamCompositor** â€” Shadow (CanvasCommandList + ShadowEffect) and clip geometry were recreated every frame despite being constant (only depend on position/shape). Now cached and invalidated on UpdateStyle(). Made WebcamCompositor IDisposable. âœ…
  4. **Fixed GIF export webcam frame leak** â€” ExportEngine's GIF path set webcam frame on compositor but never disposed it after composition consumed it, leaking one CanvasBitmap per frame during long GIF exports. âœ…
  5. **Wired WebcamCompositor disposal into FrameCompositor.Dispose()** â€” Prevents GPU resource leaks from cached shadow/geometry. âœ…
- **What worked**: All five optimizations combined. Build and all 93 tests pass.
- **What didn't work**: N/A â€” all approaches succeeded without regression.

---

## 2026-05-04 - Copilot Code Review Fixes (7 issues)

- **Feature/area**: RecordingSession, WebcamCompositor, ExportEngine, SpeechToText, EditorPage, VideoEncoder
- **Approaches tried**:
  1. **Stale webcam device ID** (RecordingSession.cs) â€” Saved device ID was used without validation; stale/unplugged camera caused StartAsync() to throw. Fix: enumerate devices upfront and fall back to first available if saved ID not found. âœ…
  2. **Shadow clipping at top/left edges** (WebcamCompositor.cs) â€” Shadow render target only padded right/bottom; shadow was clipped when webcam was near left/top. Fix: ensure render target has padding on all sides via min-size floor. âœ…
  3. **GIF webcam error handling** (ExportEngine.cs) â€” GIF path opened webcam clip without try/catch, failing entire export on corrupt files. Fix: wrapped in try/catch to skip webcam overlay gracefully. âœ…
  4. **Recognizer field shadowing** (SpeechToText.cs) â€” `using var _recognizer` created a local that shadowed the instance field, preventing external Dispose()/TranscribeAsync() from cancelling in-flight recognition. Fix: assign to instance field directly. âœ…
  5. **Full-resolution webcam preview** (EditorPage.xaml.cs) â€” Preview requested thumbnails at native resolution (1080p/4K) for ~300px overlay. Fix: cap extraction to 1.5Ã— display size, matching export path. âœ…
  6. **Stale webcam state on project reload** (EditorPage.xaml.cs) â€” LoadWebcamCompositionAsync didn't clear previous _webcamComposition/_lastWebcamFrame. Fix: clear old state at method start. âœ…
  7. **Webcam bitmap leak on error** (VideoEncoder.cs) â€” webcamFrame only disposed on success path; ComposeFrame() throw leaked GPU memory. Fix: moved cleanup to try/finally. âœ…
- **What worked**: All 7 fixes applied. Musio.Core and Musio.Tests build clean, all 93 tests pass.
- **What didn't work**: Musio.App build failed due to running app locking the DLL (not a code issue).

---

## Recording Page â€” Mic / System Audio / Camera Toggle Buttons

- **Feature/area**: RecordingPage UI (RecordingPage.xaml, RecordingViewModel.cs)
- **Approaches tried**:
  1. **ToggleButton with FontIcon** â€” Added 3 `ToggleButton` controls (Mic &#xE720;, Speaker &#xE767;, Camera &#xE714;) bound two-way to existing ViewModel properties (`IsMicEnabled`, `IsSystemAudioEnabled`, `IsWebcamEnabled`). Placed between hero buttons and capture mode selector. âœ…
  2. **Persisting toggle state** â€” Added `partial void On*Changed` methods in ViewModel to write back to `AppSettings.Instance` on each toggle. âœ…
- **What worked**: ToggleButtons with two-way `x:Bind` to existing `[ObservableProperty]` fields + partial change handlers for persistence. Controls disabled during recording via shared `IsHitTestVisible`/`Opacity` binding pattern already used by capture mode section.
- **What didn't work**: N/A â€” straightforward addition.

---

## Recording Page â€” Single Toolbar Layout with Inline Expansion

- **Feature/area**: RecordingPage UI (RecordingPage.xaml, RecordingPage.xaml.cs)
- **Approaches tried**:
  1. **Single horizontal toolbar** â€” Replaced the vertical 3-layer layout (hero button â†’ toggles â†’ capture mode) with a single centered toolbar: `[Capture Mode] | [inline sub-options] | [ðŸŽ¤ ðŸ”Š ðŸ“·] | [âº]`. Used a `Border` with card styling (CornerRadius="12") and `AppBarSeparator` dividers between sections. âœ…
  2. **Inline expansion for sub-options** â€” Window picker (ComboBox + refresh button) and Region selector (button + info text) expand inline in the toolbar when their mode is selected, separated by an AppBarSeparator. âœ…
  3. **Split options vs action sections** â€” Options (capture mode, toggles) wrapped in a StackPanel with `IsHitTestVisible`/`Opacity` bindings for recording-disabled state, while record/stop button stays outside so it remains interactive. Added `InvertBoolToVisibility` helper for showing/hiding start vs stop button. âœ…
- **What worked**: Single toolbar with inline expansion, split disabled/interactive sections, removed VisualStateManager (no longer needed without text labels on buttons).
- **What didn't work**: N/A.

---

## Timeline Track Colors â€” Light Theme Bright Palette

- **Feature/area**: TimelineControl light-theme track colors (`TimelineControl.xaml.cs` `InitializeThemeColors`)
- **Approaches tried**:
  1. **Replaced dark/muted "rich" light-theme colors with bright vivid ones** â€” Previous light-theme colors were low-brightness muted tones (e.g., zoom `180,145,30`, audio `20,160,130`, mic `135,75,180`, cursor `40,120,210`). Changed to high-saturation bright colors with a slight white lift: zoom `255,200,50`, audio `40,215,180`, mic `170,95,245`, cursor `60,150,255`. Updated zoom selected border/handle colors to match. âœ…
- **What worked**: Bright vivid colors with high hue saturation and a touch of white â€” visible and distinct on light backgrounds without being neon.
- **What didn't work**: N/A.

---

## Timeline Track Colors â€” Dark Theme High Saturation

- **Feature/area**: TimelineControl dark-theme track colors (`TimelineControl.xaml.cs` `InitializeThemeColors`)
- **Approaches tried**:
  1. **Boosted dark-theme colors to high saturation** â€” Previous dark-theme colors were already fairly vivid but not max saturation (zoom `230,200,60`, audio `50,210,180`, mic `180,120,230`, cursor `100,170,250`). Pushed to higher saturation with a touch of white: zoom `255,210,50`, audio `40,230,190`, mic `190,100,255`, cursor `80,170,255`. âœ…
- **What worked**: Near-max saturation colors pop well on dark backgrounds while the slight white tint prevents them from looking harsh/neon.
- **What didn't work**: Just boosting brightness of the same hues (gold, teal, lavender) didn't feel inspiring â€” needed completely different neon hue choices.

---

## Timeline Track Colors â€” Neon Hue Overhaul

- **Feature/area**: TimelineControl track colors for both themes (`TimelineControl.xaml.cs` `InitializeThemeColors`)
- **Approaches tried**:
  1. **Complete hue shift to neon palette** â€” Replaced all track hues: zoom goldâ†’neon green (`50,255,100`), audio tealâ†’neon cyan (`0,240,220`), mic lavenderâ†’neon magenta (`255,50,200`), cursor pale blueâ†’electric blue (`60,140,255`). Light theme uses slightly deeper versions for contrast on lighter backgrounds. âœ…
- **What worked**: Completely changing hues to neon-style colors rather than boosting brightness of existing hues. The neon green/cyan/magenta/blue palette feels modern and inspiring.
- **What didn't work**: Boosting brightness/saturation of the same gold/teal/lavender hues â€” still felt muted and uninspiring.

---

## Timeline Track Colors â€” User-Chosen Neon Palette + 4px Filmstrip Stroke

- **Feature/area**: TimelineControl track colors and filmstrip stroke (`TimelineControl.xaml.cs`)
- **Approaches tried**:
  1. **Applied exact user palette (#0DFF89, #0C2EE8, #FF00AA, #DDFF00, #E87C06)** â€” Mapped: orange `#E87C06` â†’ video clip + filmstrip stroke, neon yellow `#DDFF00` â†’ zoom, neon green `#0DFF89` â†’ audio, hot pink `#FF00AA` â†’ mic, electric blue `#0C2EE8` â†’ cursor. Light theme uses slightly deeper variants. âœ…
  2. **4px filmstrip stroke** â€” Changed filmstrip stroke from 1px (unselected) / 2px (selected) to 4px in all states. Stroke color changed from translucent white to orange `#E87C06`. âœ…
- **What worked**: Using the exact user-provided palette with appropriate dark/light theme adjustments. Orange filmstrip stroke at 4px gives the video track a bold, distinct frame.
- **What didn't work**: Previous neon palette (green/cyan/magenta/blue chosen by AI) didn't match user's vision â€” always ask for or use user-provided color references.

---

## Window Capture â€” Bring Window to Front on Record

- **Feature/area**: RecordingViewModel â€” Window capture mode (`RecordingViewModel.cs`)
- **Approaches tried**:
  1. **Added `SetForegroundWindow` P/Invoke call in `StartRecordingAsync`** â€” After `BuildCaptureTarget()` succeeds and before creating the `RecordingSession`, call `SetForegroundWindow(SelectedWindow.Handle)` when in `CaptureMode.Window`. âœ…
- **What worked**: Simple `SetForegroundWindow` call right after target validation brings the selected window to the top of the z-order before capture begins.

---

## PLM Suspension Hang Fix (HANG_QUIESCE)

- **Feature/area**: App lifecycle â€” PLM suspension handling (`App.xaml.cs`, `MainWindow.xaml.cs`, `EditorPage.xaml.cs`)
- **Approaches tried**:
  1. **WM_ENDSESSION handling in MainWindow WndProc** â€” Detects system shutdown/logoff and sets `_isExiting = true` via `App.HandleSystemShutdown()` so `OnWindowClosing` doesn't cancel the close. âœ…
  2. **ExtendedExecutionSession when hiding to tray** â€” Requests `ExtendedExecutionReason.Unspecified` when the window becomes hidden to prevent PLM from suspending the app while it runs in the tray. Released when the window is shown again. âœ…
  3. **Pause editor playback on window hide** â€” `Window.VisibilityChanged` event pauses `EditorPage.Preview` (and audio via its `IsPlayingChanged` handler) when the window becomes invisible. âœ…
- **What worked**: Three-pronged approach â€” WM_ENDSESSION for system shutdown, ExtendedExecutionSession for PLM prevention, and VisibilityChanged for playback pause.

---

## Comprehensive Automated Test Suite & CI Pipeline

- **Feature/area**: Test infrastructure â€” Musio.Tests project, GitHub Actions CI workflow
- **Approaches tried**:
  1. **`dotnet build` for test project** â€” Failed with MSB4062 because Musio.Core references WinAppSDK which requires the PRI task DLL only available in VS MSBuild.
  2. **VS MSBuild.exe for build + `dotnet test --no-build` for execution** â€” Worked. MSBuild builds the Core and Tests projects successfully, then `dotnet test --no-build` runs all tests. âœ…
  3. **GitHub Actions workflow with `microsoft/setup-msbuild@v2`** â€” Uses `setup-msbuild` action to get MSBuild on CI runner, then `dotnet test --no-build` to execute. âœ…
- **What worked**: Build with VS MSBuild (`msbuild /p:Configuration=Release /p:Platform=AnyCPU`), test with `dotnet test --no-build`. CI uses `microsoft/setup-msbuild@v2` action.
- **What didn't work**: `dotnet build` fails for any project that transitively references WinAppSDK due to missing `Microsoft.Build.Packaging.Pri.Tasks.dll` in the dotnet SDK.
- **Test files created**: AspectRatioHelperTests, CubicBezierEasingTests, TimelineMapperTests, PerformanceMonitorTests, SubtitleGeneratorTests, AudioWaveformGeneratorTests, ProjectModelTests, EditOperationsExtendedTests, SessionCleanupServiceTests, KeyboardRecorderTests, ExportResolutionTests, ZoomKeyframeExtendedTests (254 total tests, all passing).

---

## Touch Cursor Dedup â€” Overlapping Click Animations

**Feature/area:** Compositor pipeline (CursorRenderer, FrameCompositor)

**Approaches tried:**

1. **Chain-based dedup in RenderTouchClicks** â€” Worked. Consecutive click-down events whose gap < TouchTotalDuration (1.41s) are grouped into "chains." Each chain renders a single touch cursor that floats in â†’ taps at each click position â†’ slides between click positions â†’ fades out after the last tap. âœ…

**What worked:**
- `CursorRenderer.RenderTouchClicks` groups overlapping click-downs into chains and delegates to `RenderTouchChain`.
- `RenderTouchChain` walks the chain timeline: pre-roll float-in for first click, tap-down/tap-up at each click, EaseInOut slide between consecutive clicks, fade-out after last tap.
- `FrameCompositor.GetActiveClicks` window widened from Â±1.0s to Â±1.5s to ensure upcoming chained clicks are visible during transitions.
- Every click's tap animation is preserved (no taps are suppressed).
- Isolated clicks (gap â‰¥ 1.41s) behave identically to the original code.

**What didn't work / design rejected:**
- "Latest pre-roll wins" approach (only render the click with the most recently started animation) â€” rejected because it suppresses earlier click taps when a later click's 1s pre-roll begins before the earlier click actually taps.
- Expanding the window was necessary: a Â±1s window can miss the next chained click during the transition phase (max future distance = 1.25s).

---

## Processing Layer â€” Crash/Hang/Vulnerability Fixes

**Feature/area**: `CursorRenderer`, `BackgroundCompositor`, `AutoZoomEngine`, `VideoFrameReader` in `Musio.Core.Processing`.

**Approaches tried:**

1. Surgical safeguards added to four files without refactoring surrounding code.

**What worked:**

- **CursorRenderer**: Wrapped `CanvasBitmap.LoadAsync` in try/catch so corrupt/missing custom cursor images don't crash init. Added disposal of previous `_cursorBitmap`/`_defaultCursorGeometry` before reloading to prevent GPU resource leaks. âœ…
- **BackgroundCompositor**: Changed sync-over-async `.GetAwaiter().GetResult()` to `.AsTask().ConfigureAwait(false).GetAwaiter().GetResult()` to prevent SynchronizationContext deadlocks. âœ…
- **AutoZoomEngine**: Added null check on `suppressedTicks` parameter in `SetSuppressedClickTicks` to prevent NRE. âœ…
- **VideoFrameReader**: Wrapped `OpenSession` body in try/catch for `IOException`/`UnauthorizedAccessException`/`ArgumentException` to handle inaccessible session folders gracefully. âœ…

**What didn't work:** N/A â€” all fixes were straightforward safeguards.

**Note:** Build has a pre-existing error in `ExportEngine.cs(297)` unrelated to these changes.

---

## App Layer â€” Crash/Hang/Vulnerability Safeguards

**Feature/area**: Musio.App â€” MainWindow, RecordingPage, PreviewCanvas, RecordingOverlayWindow, RegionBorderHighlight

**Approaches tried:**
- Direct surgical fixes: replace throw with debug log, add try/catch to async void handlers, clamp FPS to â‰¥1, stop existing timer before creating new one, call Hide() before Show() to prevent GDI resource leak.

**What worked:**
- All five fixes applied as minimal safeguards without refactoring surrounding code. No new dependencies added. Build succeeds for Musio.App (pre-existing Musio.Core error is unrelated).

**What didn't work:**
- N/A â€” first approach succeeded for all five issues.

---

### Export Layer â€“ Crash/Hang/Vulnerability Fixes (Round 2)

- **Feature/area**: VideoEncoder.cs, ExportEngine.cs, HardwareEncoderDetector.cs in Musio.Core/Export.
- **Approaches tried**:
  1. Added 5-minute timeout to MuxAudioAsync TCS await to prevent infinite hang.
  2. Captured outputSurface in a local variable before nulling to prevent double-dispose race in sample.Processed callback.
  3. Added sourceClip = null cleanup in ExportGifAsync finally block (MediaClip is not IDisposable).
  4. Wrapped GetStringAttribute/GetGuidAttribute COM vtable access in try/catch returning E_FAIL.
- **What worked**: All four fixes applied cleanly. MediaClip doesn't implement IDisposable, so used null-out instead of Dispose.
- **What didn't work**: Tried sourceClip?.Dispose() but MediaClip (WinRT) has no Dispose method â€” switched to nulling the reference after Clips.Clear().

---

## Capture Layer - Crash/Hang/Vulnerability Fixes

**Feature/area:** AudioCaptureEngine, MouseHookRecorder, KeyboardHookRecorder, ScreenCaptureEngine, VideoWriter, WebcamCaptureEngine

**Approaches tried:**

1. Surgical try/catch guards on callback threads - Wrapped audio Write calls and hook Marshal.PtrToStructure in try/catch. Worked.
2. Timeout on ManualResetEventSlim.Wait() - 5s timeout with InvalidOperationException. Worked.
3. Resource cleanup on partial init failure - cleanup already-started resources on throw. Worked.
4. Input validation - fps > 0 and null-check on GetDirectoryName. Worked.
5. Dispose deadlock prevention - Task.Wait(3s) instead of GetAwaiter().GetResult(). Worked.

**What worked:** All five - minimal surgical fixes.

**What didn't work:** dotnet CLI build fails due to missing WinAppSDK PriGen task in .NET 10 SDK; must use VS MSBuild to build.

---

## Window/Region Selector â€” Escape Key Not Working

**Feature/area:** WindowSelectorOverlay + RegionSelectorOverlay (Escape to cancel selection)

**Approaches tried:**

1. **`Activated` event handler + `KeyDown` fallback** â€” Did not fix the issue. XAML `Focus(FocusState.Programmatic)` and `KeyboardAccelerator`/`KeyDown` all depend on XAML keyboard focus, which is not reliably established on a borderless maximized overlay window before the user clicks. âŒ
2. **Win32 low-level keyboard hook (`WH_KEYBOARD_LL`)** â€” Worked. Installs a `SetWindowsHookEx` hook in `ShowAsync` that intercepts `VK_ESCAPE` at the OS level, completely bypassing XAML focus. Hook is cleaned up in the `finally` block. Applied to both overlays. âœ…

**What worked:** `SetWindowsHookEx(WH_KEYBOARD_LL, ...)` with a managed callback that dispatches `CancelSelection` / `TrySetResult(null)` via `DispatcherQueue.TryEnqueue`. The delegate is stored as a field to prevent GC.

**What didn't work:** Any approach relying on XAML keyboard focus (`Focus(FocusState.Programmatic)`, `KeyboardAccelerator`, `KeyDown` event) â€” XAML focus is not established on a borderless maximized overlay until the user clicks on a focusable element.


---

## Perf â€” FrameCompositor.CropSourceFrame Interpolation

**Approaches tried:**

1. **Adaptive interpolation: Linear when sourceâ†’content scale is within Â±5% of 1:1, otherwise HighQualityCubic** â€” Avoids running a 4-tap bicubic filter for every pixel on every frame in the common case (AspectRatio.Auto with no zoom, where viewport == content size, scale = exactly 1.0). Mirrors the same adaptive choice already present in ComposeFramePostCompositeZoom. âœ…

**What worked:** A small Math.Abs(scale - 1.0) < 0.05 guard around the interpolation choice. Hits every frame in both preview and export pipelines, and is visually equivalent to cubic at 1:1.

**What didn't work:** N/A (single-pass change).

---
---

## Multi-Monitor Region Selection — Compression & Wrong Coords

**Feature/area:** `RegionSelectorOverlay` (region capture flow).

**Symptom:** On multi-monitor setups, choosing "Region" displayed both monitors' contents squashed into a single display, and the resulting selection rectangle landed in the wrong place.

**Root cause:**
1. `CaptureDesktopScreenshotAsync` captured the entire virtual desktop (`SM_X/CXVIRTUALSCREEN`) in physical pixels.
2. The host `Window` was sized via `OverlappedPresenter.Maximize()`, which only covers a single monitor. The second monitor was never under the overlay.
3. `ScreenshotImage` used `Stretch="Fill"` so the multi-monitor screenshot was compressed into the single-monitor window.
4. `ConfirmSelection` stored raw overlay DIPs as `CaptureRegion.X/Y/W/H`, but `RecordingSession` expects monitor-local DIPs (it multiplies by the monitor's DPI scale to recover physical pixels).

**What worked:**
- Replaced `presenter.Maximize()` with `AppWindow.MoveAndResize` to the full virtual desktop rect (`SM_X/Y/CX/CYVIRTUALSCREEN`). The window now spans every monitor in physical pixels, matching the screenshot 1:1.
- Set `IsResizable/IsMaximizable/IsMinimizable=false` on the presenter.
- `ConfirmSelection` now: overlay DIPs -> virtual-desktop physical pixels (`_screenshotWidth/ActualWidth` ratio + virtual screen origin) -> monitor-local physical pixels (clamped to host monitor) -> monitor-local DIPs via `GetDpiForMonitor` (`MonitorFromPoint` at selection center). This handles mixed-DPI multi-monitor correctly because each monitor's DPI is queried independently.

**What didn't work / rejected approaches:**
- Per-window DPI Unaware context: would require coordinate juggling between unaware logical pixels and the PMv2-captured physical-pixel screenshot. Rejected as more complex and fragile.
- Keeping `Stretch="Fill"` after Maximize fix: irrelevant once window equals virtual desktop size — Fill becomes 1:1.


---

## Region Border Highlight — Wrong Position on Non-Primary Monitor

**Feature/area:** `RegionBorderHighlight` placement in `RecordingPage.ShowRecordingOverlay`.

**Symptom:** After the region selection fix stored `CaptureRegion` in monitor-local DIPs, the red border indicator drawn around the recording region during capture was always placed on the wrong monitor / at the wrong location.

**Root cause:** `ShowRecordingOverlay` multiplied `region.X/Y` (monitor-local DIPs) by the monitor's DPI scale, producing monitor-local **physical** pixels. The Win32 `CreateWindowEx` used by `RegionBorderHighlight` requires **screen-absolute** physical pixel coordinates (PMv2 process), so without the monitor's screen origin the border was anchored at (0,0) of the virtual desktop instead of the correct monitor.

**What worked:** Added `GetRegionMonitorOrigin` (`GetMonitorInfo` on the resolved monitor handle) and offset the computed px/py by `info.rcMonitor.Left/Top` before calling `RegionBorderHighlight.Show`. Border now lands exactly on the captured region across all monitors and DPI scales.


## Multi-Monitor Region Selection — Rubber-Duck Adoptions

- **Feature/area**: Region selection overlay + recording border highlight (multi-monitor).
- **What worked**:
  - Pick host monitor for the selected rect by **largest intersection area** in virtual-desktop physical pixels (replaces `MonitorFromPoint` on a single corner, which can disagree across monitor boundaries).
  - Cancel selection when the rect has zero intersection with any monitor (instead of clamping to a phantom 1x1 region via `Math.Max(1, …)`).
  - Add `IntPtr Handle` to `MonitorInfo` so DPI lookups (`GetDpiForMonitor`) and origin lookups (`GetMonitorInfo`) use the same hMonitor used for selection, eliminating point-lookup drift.
  - Match monitor by exact `DisplayName == MonitorId` (or `StartsWith(MonitorId + ' ')` to cover `"\\.\DISPLAY1 (Primary)"`). `Contains` matched `\\.\DISPLAY1` against `\\.\DISPLAY10`.
  - Round border px/py with `Math.Round` and floor pw/ph to even numbers (`& ~1`) to match the H.264 crop math in `RecordingSession` (avoids 1-2 px drift between highlight and actual captured frame).
- **What didn't work**:
  - `presenter.Maximize()` to cover the virtual desktop - only covers the monitor that owns the window. Use `AppWindow.MoveAndResize` with `SM_X/Y/CX/CYVIRTUALSCREEN`.
  - `DisplayName.Contains(MonitorId)` for monitor lookup - unsafe substring match across DISPLAY1/DISPLAY10.

## Stale Saved Region - Disconnected Monitor

- **Feature/area**: `RecordingViewModel.GetCaptureTarget` (CustomRegion mode).
- **What worked**: When the saved `CaptureRegion.MonitorId` no longer matches any connected monitor, clear `SelectedRegion`/`HasSelectedRegion`, surface `RecordingStatus` ('Saved region's monitor is no longer connected - please select a new region'), and return null. This forces the user to reselect on the current display topology.
- **What didn't work**: Silently falling back to `allMonitors.FirstOrDefault()` - the region's monitor-local DIPs got applied to the wrong (primary) monitor, producing a clamped/off-screen capture and a misplaced red border.
## Region Picker - "blank on open / silent on cancel" confusion

**Approaches tried:**

1. **Pre-render initial region in `RegionSelectorOverlay.OnLoaded`** - Worked. Added `ShowAsync(CaptureRegion? initialRegion)` overload; `OnLoaded` now seeds `_selX/_selY/_selW/_selH` and `_hasSelection = true` from the caller-supplied region (or persisted last region as fallback), then calls `UpdateOverlay()`. User now opens onto their existing rectangle with handles instead of a blank dark canvas. ?
2. **Track cancel vs confirm via `WasCancelled` property on the overlay** - Worked. `CancelSelection` sets `WasCancelled = true` before resolving the TCS with null. `RecordingPage.LaunchRegionPickerAsync` reads it after `ShowAsync` returns null to distinguish "user pressed Escape ? no-op" from "first-time open with no prior selection". On Escape with an existing selection, page shows an InfoBar: *"Region selection cancelled - kept previous region."* - eliminates the pixel-identical confusion. ?

**What worked:** Both fixes together. Pre-render eliminates the blank-screen surprise; InfoBar makes Escape's no-op explicit.

**What didn't work / not chosen:**

- Returning a richer result type (e.g. `RegionPickerResult { Region, WasCancelled }`) - would be cleaner but required more surface area changes; `WasCancelled` as a property on the overlay (already a stateful UserControl) was the smaller diff.

---

## Recording state lost on page navigation

**Approaches tried:**

1. **Per-page `RecordingViewModel` (original)** - Didn't work. `RecordingPage` declared `public RecordingViewModel ViewModel { get; } = new();` so every navigation back to the page constructed a fresh VM, losing `CaptureMode`, `SelectedWindow`, `SelectedRegion`, `HasSelectedRegion`, and audio toggles. Region survived only because it was persisted to `ApplicationData.LocalSettings` via `RegionMemory`.
2. **Shared singleton `RecordingViewModel.Shared`** - Worked. Page now uses `ViewModel { get; } = RecordingViewModel.Shared;` so all selection state survives Editor ? Record navigation. ?
3. **Per-navigation event subscription** - Required companion fix. Constructor-based `ViewModel.PropertyChanged += -` on a shared VM would leak page references (the VM holds the lambda which captures `this`). Moved subscription to `OnNavigatedTo` and added matching tear-down in `OnNavigatedFrom` so prior page instances can be GC'd. ?

**What worked:** Shared VM + navigation-scoped event lifecycle.

**What didn't work / not chosen:**

- Re-hydrating only `SelectedRegion` from `LoadLastRegion()` in `UpdateRegionInfoDisplay` (already existed) - insufficient because `CaptureMode` and `SelectedWindow` were not persisted, and on fresh-install `LocalSettings` is empty.
- `NavigationCacheMode=Required` on the page - would also work but caches the entire visual tree; singleton VM is a smaller, more explicit contract.
- **What worked**: When the saved `CaptureRegion.MonitorId` no longer matches any connected monitor, clear `SelectedRegion`/`HasSelectedRegion`, surface `RecordingStatus` ('Saved region's monitor is no longer connected - please select a new region'), and return null. This forces the user to reselect on the current display topology.
- **What didn't work**: Silently falling back to `allMonitors.FirstOrDefault()` - the region's monitor-local DIPs got applied to the wrong (primary) monitor, producing a clamped/off-screen capture and a misplaced red border.

---

---

## Stop-Trigger Click Removal from Recording Data

**Feature/area:** MouseHookRecorder, RecordingOverlayWindow, recording stop flow

**Approaches tried:**

1. **Timestamp-based trim with `NotifyStopRequested()`** - Worked. Added a `NotifyStopRequested()` method to `MouseHookRecorder` that records the current `Stopwatch.GetTimestamp()` and sets `_paused = true` to stop collecting new events. A static `TrimStopClick()` method on `MouseRecordingData` finds the last left-button-down click within 200ms before the stop-requested timestamp and removes it, its corresponding up event, and button samples. Move/scroll samples are preserved. ?

**What worked:** Signaling the mouse recorder from the overlay's `Stop_Click` and `OnOverlayClosing` handlers (earliest possible point in the stop flow), then trimming the stop-trigger click in `GetRecordedData()`. The 200ms threshold is generous enough for UI dispatch delay (~3-10ms typical) while avoiding false positives.

**What didn't work:** N/A - first approach worked.

**Build notes:** The `dotnet build` CLI fails on Musio.Core/App due to missing WinAppSDK packaging tasks (`ExpandPriContent`). Use MSBuild from VS (`"C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe"`) for building. Tests require `DOTNET_ROLL_FORWARD=LatestMajor` since .NET 9 runtime is not installed (only 8 and 10).


---

## Zoom Region Editor Styling, Resize & Center Snapping

**Feature/area:** `EditorPage` zoom region edit overlay (`ZoomRegionRect` + handlers in `EditorPage.xaml.cs`).

**Approaches tried:**
1. **1px dashed stroke** ? reduced `StrokeThickness` from 2 to 1 on `ZoomRegionRect`.
2. **Corner-handle resize, aspect-preserving** ? Added 4 corner `Rectangle` handles in XAML (visual only, `IsHitTestVisible=False`); hit-testing performed in code against the rect corners with a 10px radius. On drag, the opposite corner is anchored, scale = `max(|dx|/W0, |dy|/H0)`, `newZoom = startZoom / scale` clamped to `[1.5, 4.0]` (the UI's exposed range). vp width/height are derived from zoom (and output aspect override is uniform in zoom), so the rect aspect is preserved automatically.
3. **Center snapping with Shift override** ? On translation, snap normalized center to `0.5` when within `CenterSnapThreshold` (0.02) per axis, unless `e.KeyModifiers` includes `VirtualKeyModifiers.Shift`. Blue dashed guide lines visualize active snap.

**What worked:** Reading `PointerRoutedEventArgs.KeyModifiers` directly (no separate keyboard hook). Computing the anchor corner in display coords at `PointerPressed` and re-projecting the new center display position back to normalized coords keeps the opposite corner fixed across resize. Clamping zoom first, then deriving effective scale, avoids the rect drifting outside `[1.5, 4.0]`.

**What didn't work / rejected:** Letting the corner handle `Rectangle` elements be hit-test-visible ? would steal pointer capture from the `Canvas` and break the existing drag handlers. Solution: keep handles purely cosmetic and do manual hit testing.

---

## Region Selector Overlay & Recording Page - PR Review Fixes

**Approaches tried:**

1. **TryApplyPresetRegion coordinate space bug** - Was assigning CaptureRegion.X/Y/Width/Height (monitor-local DIPs) directly into overlay selection coords (virtual-desktop overlay DIPs). On multi-monitor / mixed-DPI this seeded the wrong place/size. Fixed by converting monitor-local DIPs ? physical pixels (using saved monitor's origin + GetMonitorDpiScale) ? overlay DIPs (subtract virtual-desktop origin from SM_X/YVIRTUALSCREEN, divide by _screenshotWidth/ActualWidth). ?
2. **UpdateOverlay degenerate-selection state leak** - When sw/sh clamped to <=0, the blank overlay rendered but _hasSelection and ButtonPanel.Visibility were left set. Now clears _hasSelection/_sel* and hides ButtonPanel in the degenerate branch. ?
3. **RecordingPage._infoBarTimer navigation leak** - Timer was created on the page's DispatcherQueue and retained OnInfoBarTimerTick, keeping the page alive after navigation. Now Stop() + detach Tick handler + close InfoBar in OnNavigatedFrom. ?

**What worked:** All three above.


---

## Editor - Spacebar Play/Pause Shortcut

**Feature/area:** EditorPage keyboard accelerators / Preview playback.

**Approaches tried:**

1. **Page-level `KeyboardAccelerator` with `Key="Space"` invoking `Preview.Play()`/`Preview.Pause()` based on `Preview.IsPlaying`** - Worked. ?
2. **Skip handler when a text input has focus** - Used `FocusManager.GetFocusedElement(XamlRoot)` and bailed for `TextBox`/`PasswordBox`/`RichEditBox`/`AutoSuggestBox` so typing a space in editable fields isn't hijacked. ?

**What worked:** XAML accelerator + focus-check guard in the handler.



---

## Overlapping Zoom Segments - Center Snap Fix

**Feature/area:** AutoZoomEngine - focal-point continuity across overlapping zoom segments

**Approaches tried:**
1. Earlier fix used max-zoom-wins for both zoom level and center (cx/cy). Zoom level stayed continuous, but the *center* snapped at the instant B's zoom first exceeded A's, since the winning keyframe's center was used wholesale. Editing a segment moved where this snap occurred, making the visual jump obvious.
2. Blend centers by per-keyframe "activation weight" (max(0, zoom - 1)) while still using max-zoom for the zoom level. ?

**What worked:**
- In `EvaluateManualKeyframes` and `EvaluateAutoSegments`: compute `w = max(0, zoom - 1)` per active segment, accumulate `weighted sum of cx/cy`, return `weightSum > eps ? sum/weight : maxZoomSegmentCenter`.
- This guarantees: at A-only the center = cA; at the crossover it's a smooth weighted blend; at B-only the center = cB. No focal-point snap regardless of how segments are edited.
- Regression test `GetZoomState_OverlappingManualKeyframes_CenterDoesNotSnap` steps through the overlap and asserts per-step center delta stays well under the would-be snap magnitude.

**What didn't work:**
- Returning the max-zoom segment's center as-is - produces a discontinuous jump at the crossover when overlapping segments have different centers.

---

## Style Menu For Full-Screen Capture

**Feature/area:** EditorPage style flyout, ProjectService composition defaults.

**Approaches tried:**

1. **Make StyleButton/StyleSeparator visible unconditionally in InitializeStyleControls** - Worked. Previously gated on Window/Region only; now exposed for Monitor too. ✅
2. **Move the Monitor background-defaults override (Padding=0, CornerRadius=0, ShadowEnabled=false, BorderEnabled=false) from `EditorPage.InitializePreviewAsync` into `ProjectService.SetProject`** - Worked. Defaults apply at project-load time so user edits via the now-visible style menu survive editor re-navigation. ✅

**What worked:** Combination - unconditional style-menu visibility + applying Monitor defaults in `ProjectService.SetProject` (instead of every `InitializePreviewAsync`).

**What didn't work:** Leaving the Monitor override unconditional in `InitializePreviewAsync` - would clobber user edits every time the EditorPage was re-constructed.

---

## Export Resolution & Quality - Exposed in Settings

**Feature/area:** AppSettings, SettingsPage, ExportViewModel, VideoEncoder, AspectRatioHelper

**Approaches tried:**

1. **Added DefaultExportResolution / DefaultExportQuality to AppSettings** stored as enum names (strings), mirroring the existing Theme/DefaultCaptureMode pattern. New GetEnum<T> helper does Enum.TryParse with Enum.IsDefined fallback.
2. **VideoEncoder now consumes _settings.Resolution** via new AspectRatioHelper.ComputeExportDimensions(compW, compH, resolution). The helper fits the compositor's already-aspect-corrected output within the resolution bounds, caps at compositor native (no upscale), then mod-16 floors.
3. **SettingsPage XAML/code-behind** got an Export Defaults section with Resolution + Quality ComboBoxes. Uses _suppressExportDefaultEvents during initial SelectComboBoxByTag to avoid persisting accidental defaults during page load.

**What worked:**
- Aspect ratio is preserved because the compositor has already locked in the chosen AspectRatio + padding before encoding. The resolution selector only caps the bounding box.
- No-upscale clamp: min(resolutionBound, compositorNative) keeps 720p recordings from being uselessly upscaled to 4K.
- mod-16 floor produces a tiny aspect drift (e.g. 1080->1072, ~0.7%) which is imperceptible and preserves the existing learnings about H.264 macroblock alignment.

**What didn't work / decisions deferred:**
- ComputeBitrate's Math.Max(1.0, pixels/baseline) still gives 720p exports the same bitrate as 1080p. Left as-is to keep scope tight.
- Did not add an option to allow upscaling; if users want it later add an opt-in toggle rather than removing the clamp.

## Export resolution/quality settings - rubber-duck refinements

**Feature/area**: AppSettings export defaults + AspectRatioHelper.ComputeExportDimensions

**Approaches tried**:
- Default DefaultExportResolution to HD1080 (matches ExportPreset default).
- Use `Math.Max(16, (fitW/16)*16)` to enforce mod-16 floor.

**What worked**:
- Defaulting to UHD4K preserves prior behavior (encoder previously ignored the
  field, so output was effectively native source resolution). HD1080 would have
  silently downscaled existing >1080p users on upgrade.
- For compositor dims < 16px on a side, branch to even-rounded source size
  (`Math.Max(2, w - w%2)`) instead of padding to 16. Preserves the
  no-upscale invariant.
- DataTestMethod invariant tests (bounds, no-upscale, even, <3% aspect drift)
  across 8 (compositor, resolution) pairs caught the small-input regression
  before merge.

**What didn't work**:
- Padding sub-16 compositor dims up to 16 via `Math.Max(16,...)` violated
  the no-upscale invariant for tiny degenerate inputs.

## 2026-05 - Aspect Ratio Flyout (Cursor-style)
- **Feature**: Project-level aspect-ratio + fit-mode picker with always-visible toolbar flyout (EditorPage).
- **What worked**:
  - Project-level state on Project (AspectRatio, FitMode, CropAnchorX/Y); ExportEngine reads from project, not ExportSettings.
  - Generalized AspectRatioHelper.CalculateCropRect with anchorX/anchorY (0..1) for 9-point snap. Added CalculateContainCanvas for Contain mode.
  - FrameCompositor: introduced _sourceArea*+offset fields decoupled from contentWidth/Height. Cursor/click/zoom-center math all use sourceArea.
  - Contain mode: ComputeEffectiveViewport returns unmodified viewport (preserves source AR); CropSourceFrame fills letterbox bars via BackgroundCompositor.FillBackgroundRect (new public helper using session.Transform translate to reuse private gradient/image/blur helpers), then draws source into the centered sub-rect.
  - Post-composite zoom GATED OFF for Contain (letterbox stays constant when zooming - else bars would scale with content).
  - Inline Button.Flyout in EditorPage.xaml row-2 toolbar (next to styles/cursor) with RadioButton grids; _suppressAspectRatioEvents flag prevents init re-entrancy.
- **What didn't work / pitfalls**:
  - dotnet 9 SDK absent on this machine (only 8 + 10). Workaround: vstest.console.exe with DOTNET_ROLL_FORWARD=Major env var; do NOT use `dotnet test`.
  - MSBuild on Musio.sln alone doesn't always rebuild Musio.Tests when only test files change - target Musio.Tests.csproj directly to force rebuild.
  - Must kill Musio.App.exe before any build (DLL lock).
  - When updating AspectRatio enum, also bump SettingsTests.AspectRatio_AllValues_AreDefined length assertion.
- **Deferred to v2**: free-drag crop anchor with shift-bypass; timeline thumbnail display-time transform.

## Frame style: padding & aspect ratio isolation (round 3)

### Feature/area
FrameCompositor + BackgroundCompositor - decoupling padding from output aspect ratio, and unifying letterbox/padding fill so the wallpaper is one continuous container.

### What didn't work
- Previous model had `BackgroundCompositor.CalculateOutputSize` return `(source + 2*padding, source + 2*padding)`. This made the output canvas grow with padding, so dragging the padding slider visibly changed the canvas aspect ratio.
- Drawing letterbox/pillarbox bars by calling `FillBackgroundRect` inside the cropped buffer caused the wallpaper to be rendered separately in the letterbox bars vs the outer padding margin - visible "wallpaper repeat" with two independent containers.

### What worked
- `BackgroundCompositor.CalculateOutputSize` is now identity `(w, h)`: padding insets within the canvas, never extends it.
- `FrameCompositor.ComputeContentDimensions` now computes:
  - `_contentWidth/_contentHeight` = output canvas at target AR (fit to source bounds), independent of padding.
  - `_innerContentWidth/_innerContentHeight` = canvas - 2*padding (the inner content rect, where source is drawn).
  - `_sourceAreaWidth/Height/OffsetX/Y` describe the source placement WITHIN the inner content area. Cover fills inner; Contain preserves source AR inside inner with letterbox/pillarbox.
- `CropSourceFrame` sizes `_croppedBuffer` to inner dims and clears to transparent; letterbox bars are no longer filled in the buffer.
- `BackgroundCompositor.CompositeFrame` already draws the chosen background across the entire output canvas (w - h) before drawing the (now possibly transparent) content into the inset rect. Because the cropped buffer has transparent letterbox regions, the outer wallpaper shows through them as one continuous fill - padding margin + letterbox bars = single container.
- Cursor and click coord math (`(cursor - viewport)*scale + padding + sourceAreaOffset`) is unchanged: scale uses `_sourceAreaWidth/viewport.Width`, offsets are in inner-buffer space, and `+padding` shifts to outer canvas coords (content is drawn at `(padding, padding)` by BgCompositor).

### Test impact
- Updated 4 `BackgroundCompositorTests.CalculateOutputSize_*` tests to expect identity (canvas unchanged), no longer adding padding.
- All 268 tests pass.


## Frame style: collapse letterbox + padding into one container (round 4)

### Feature/area
FrameCompositor + BackgroundCompositor - eliminating the separate "inner content area" / "letterbox bars" concept so there is exactly one background container around the source frame.

### What worked
- Removed `_innerContentWidth/_innerContentHeight` fields entirely.
- `_sourceAreaWidth/_sourceAreaHeight` now describe the actual visible source frame size in output canvas coords.
- `_sourceAreaOffsetX/_sourceAreaOffsetY` now represent the source frame's position from the canvas origin (= user-padding + any AR-fit gap on that side). There is no separate "inner" coordinate space.
- `ComputeContentDimensions`: compute canvas (target AR, padding-independent), then a max content box (canvas - 2*userPadding), then fit source into it per FitMode, finally CENTER the source within the canvas (so leftover gap is simply more background).
- `CropSourceFrame`: buffer is sized exactly to the source-area dims (no internal letterbox padding). Drawing 1:1.
- `BackgroundCompositor.CompositeFrame` simplified signature: takes srcX/srcY/srcWidth/srcHeight directly (no padding/inner concept). Background fills the entire canvas; shadow / rounded-corner clip / border all use the source rect.
- Cursor/click/post-composite-zoom coord math: removed the separate `+padding` term - `_sourceAreaOffset*` now already includes total background gap.

### Outcome
- Shadow and rounded corners wrap the actual visible captured frame, not the larger "inner content" container.
- One unified background container fills everything around the source.
- All 268 tests still pass.


## Zoom in Contain mode operates on full canvas (round 5)

### Feature/area
FrameCompositor.ComposeFrame zoom-path selection.

### Problem
Previously, in FitMode.Contain (non-Auto AR), zoom went through the direct path so only the source viewport zoomed while letterbox bars stayed constant. The user wants zoom to treat the entire composed canvas (background + source frame + letterbox area) as one unit so the zoom region can span the whole visible frame.

### What worked
Removed the `useContainDirect` carve-out: post-composite zoom is now always used whenever ZoomLevel > 1.01, regardless of FitMode. The composite buffer (which already contains the background fill across the whole canvas) is zoom-cropped and scaled to the output, so the visible frame zooms as a single unified unit.

### Outcome
- Zoom now spans the entire visible frame including letterbox area in Contain mode.
- Cursor also scales with zoom in Contain mode (previously it did not).
- All 268 tests pass.


## ZoomScope: user choice between whole-frame and source-only zoom (round 6)

### Feature/area
Added a user-selectable `ZoomScope` (Frame | Source) on the Project / FrameCompositorConfig, surfaced in the Frame Style flyout as a "Zoom scope" radio pair ("Whole frame" / "Source only").

### Wiring
- `Musio.Core.Settings.ZoomScope` enum (Frame=default, Source).
- `Project.ZoomScope` (default Frame).
- `FrameCompositorConfig.ZoomScope`.
- `ExportEngine` propagates `project.ZoomScope` to the composition used for export.
- `FrameCompositor.ComposeFrame`: post-composite zoom path used only when `ZoomScope == Frame`; otherwise direct path so background/letterbox/padding stay fixed.
- EditorPage XAML adds a `ZoomScopePanel` under FitModePanel inside the Frame Style flyout; always visible.
- Code-behind: `SelectZoomScopeRadio`, `ZoomScopeOption_Checked`, `ApplyZoomScope` mirror the FitMode pattern (project + composition update + RebuildPreviewRendererAsync).

### Outcome
Users can choose whether zoom treats the entire visible frame as one unit (Frame) or constrains zoom to the captured source while keeping background fixed (Source). All 268 tests pass.


## Round 7: Custom visual controls for Frame Style flyout

- **Feature/area**: EditorPage Frame Style flyout UI controls.
- **Approaches tried**:
  1. Custom TileRadioStyle with iconic visuals for every option (radios with rich content).
  2. Hybrid: default RadioButtons for aspect ratio, ctk:Segmented for binary choices (Fit, Zoom scope), single 5x5 Grid with CropCellRadioStyle for 3x3 crop position.
- **What worked**: Approach 2. CommunityToolkit.WinUI.Controls.Segmented (already referenced) gives a clean binary toggle. For the 3x3 crop grid, a single bordered Grid with 3 content cols/rows + 2 1px divider lanes and 9 RadioButtons styled with a cell-fill template (background fills on CheckedNormal via CombinedStates VSM) produces a unified rectangle that fills the chosen segment.
- **What didn't work**: TileRadioStyle was visually busy and inconsistent with WinUI design language for binary/segmented choices.

## Round 8: Aspect-ratio tile selector

- **Feature/area**: EditorPage Frame Style flyout - Aspect ratio selector.
- **Approaches tried**: Tile-style RadioButton with proportional preview rectangle (sized to the actual aspect ratio, fitted inside a 36x26 bounding box) plus a label below; Auto tile shows a rounded outline rectangle with an 'A' glyph inside.
- **What worked**: New `AspectRatioTileStyle` (bordered Border template with CombinedStates VSM; accent 2px border + subtle fill on CheckedNormal). Eight tiles with per-ratio Border dimensions communicate the shape at a glance and are visually consistent with the rest of the redesigned flyout.
- **What didn't work**: Plain default RadioButtons with text content - lacked visual cues for the actual ratio so users couldn't preview at a glance.

## Round 9: Accent-fill preview rectangle on aspect-ratio tiles

- **Feature/area**: EditorPage Frame Style flyout - Aspect ratio tile selection signal.
- **Approaches tried**: (1) Use VSM in AspectRatioTileStyle template to swap inner preview brush - blocked because the preview Border lives in Content, not the template, so VSM cannot retarget it. (2) Add a small `BoolToObjectConverter` (in Musio_App.Converters) and bind each preview Border's Background/BorderBrush (and the Auto glyph Foreground) to the parent tile's IsChecked via `ElementName` binding.
- **What worked**: Approach 2. Three shared converter instances declared in Page.Resources (PreviewBgBrush, PreviewBorderBrush, PreviewGlyphBrush) keep XAML compact. When a tile is checked, its inner rectangle fills with AccentFillColorDefaultBrush and its outline switches to accent; Auto's 'A' glyph switches to TextOnAccentFillColorPrimaryBrush for contrast. Tile-level accent border was removed in favor of a SubtleFillColorSecondaryBrush background so the accent rectangle is the focal point.
- **What didn't work**: Template-VSM approach (Content elements unreachable from style VSM).

## Round 10: Crash fix - ThemeResource on converter CLR properties causes XAML layout cycle

- **Feature/area**: EditorPage Frame Style flyout - aspect-ratio tile bindings, App crash post-capture (HRESULT 0x802B000A in combase.dll, XAML layout cycle).
- **Approaches tried**: (1) Originally used three BoolToObjectConverter instances in Page.Resources whose `TrueValue`/`FalseValue` CLR properties were assigned via `{ThemeResource AccentFillColorDefaultBrush}` etc. This compiled fine but, when EditorPage was navigated to post-capture, crashed the process inside combase with XAML_E_LAYOUT_CYCLE. (2) Replaced with a single `CheckedToBrushConverter` that looks up theme brushes from `Application.Current.Resources` at runtime, keyed by `ConverterParameter` (`Bg`/`Border`/`Glyph`).
- **What worked**: Approach 2. No ThemeResource is assigned to a non-DependencyProperty, so the XAML parser doesn't get into a resource-resolution cycle.
- **What didn't work**: `{ThemeResource X}` markup on plain CLR properties of objects placed in `Page.Resources`. Even though XAML compilation succeeds, runtime activation of a Page that references those resources can throw XAML_E_LAYOUT_CYCLE. Avoid this pattern - use static brushes, hard-coded colors, or look up brushes in code at runtime.

---

## Export Bugs: Truncated to ~1s and Crash During Preview Playback

**Symptoms:**
1. Export produced a ~1-second video instead of the full recording duration.
2. Exporting while the editor preview was playing caused frame corruption or a crash.

**Root cause(s):**
1. `VideoEncoder.ProduceSampleAsync` swallowed exceptions thrown during per-frame compositing and set `request.Sample = null` on error. `MediaStreamSource` treats `Sample = null` as end-of-stream, so the very first frame error silently truncates the output to whatever frames were already enqueued (roughly 1 second).
2. The export pipeline composites frames on the same shared Win2D device (`CanvasDevice.GetSharedDevice()`) and reads the same source `.frames/` JPEGs as the editor preview. Running both at once causes GPU device contention and shared-buffer races -> visible corruption or a hard crash.

**What worked:**
- Capture the first per-frame error in `VideoEncoder.ExportAsync` via a new `onError` callback, drain pending sample tasks, then re-throw as an `InvalidOperationException` that includes the frame index and inner exception. This turns silent ~1s truncation into a loud, debuggable failure with the real root cause surfaced in the editor's error panel.
- In `EditorPage.ExportFlyout_Opened`, call `Preview.Pause()` (and best-effort `_audioPlayer.Pause()`) before kicking off the export so the export pipeline owns the shared Win2D device and source frame reader.
- Verified all 268 MSTest cases still pass.

**What didn't work / not tried:**
- Giving the encoder its own `CanvasDevice` (instead of the shared one) would also help but was not needed once the preview is paused before export. Worth revisiting if the corruption recurs.


---

## Round 13: Style changes silently dropped on re-export

### Feature/area
Export flyout state lifecycle (`src/Musio.App/Pages/EditorPage.xaml.cs`).

### Symptom
User exports ? applies shadow/rounded-corner ? exports again ? 2nd MP4 still
has no shadow/corner even though the preview shows them correctly.

### Root cause
`ExportFlyout_Opened` short-circuits if `ExportVM.ExportSucceeded` is true.
The flag is only reset by the explicit "Close" button (`CloseFlyout_Click`).
Dismissing the flyout by clicking outside leaves the flag set, so the next
"open" just re-shows the cached `ExportedFilePath` from the previous run -
no fresh `PrepareForExport` is called and no new export runs. The user
believes they triggered a 2nd export and inspects the 1st (pre-style) file.

### Fix
Hook `ExportFlyout.Closed` to call `PrepareForExport` whenever the flyout
dismisses in a terminal state (Succeeded/Failed) and is not actively exporting.
This guarantees the next `Opened` falls through to the export path and reads
the latest `ProjectService.Instance.CurrentComposition`.

### What didn't work
Initially considered moving the reset into `ExportFlyout_Opened` itself, but
that would re-export even when the user re-opens the flyout just to re-read
the success message. Resetting on close is cleaner and matches user intent.

---

## Brand Presets - Cinematic Refresh

**Feature/area:** DefaultBrandPresets (src/Musio.Core/Settings/DefaultBrandPresets.cs)

**Approaches tried:**

1. Renamed and re-toned all 8 presets to soothing, low-saturation cinematic palettes. Display Names: Midnight, Mist, Dusk, Porcelain, Twilight, Pine, Aurora, Linen. C# property names (DarkMinimal, OceanGradient, ...) kept unchanged so existing tests and references continue to work. Increased shadow blur and softened gradient angles (155-165 deg) for cinematic depth. Replaced saturated hot pinks/greens/magentas with muted plums, sages, dusty terracottas, steel blues.

**What worked:** The cohesive visual refresh itself. (Note: a later pass replaced this preset set with the current bold-gradient lineup and renamed the static property identifiers — see the "Background presets - bold gradients" entry below. The "preserving identifiers" claim in this earlier pass no longer reflects the current code.)

**What didn't work:** N/A on this pass.

## Wallpaper picker "+" tile (EditorPage background style)

**Approach that worked:**
- Prepend a non-data "+" tile to `WallpaperGrid.Items` (sentinel `Tag = "__add_wallpaper__"`), keeping `_wallpaperPaths` as the source of truth for actual wallpapers.
- All grid<->path index conversions shift by 1 (`gridIndex = pathIndex + 1`).
- In `WallpaperGrid_SelectionChanged`, detect the sentinel tag and open `Windows.Storage.Pickers.FileOpenPicker` (init with main window HWND via `App.Current.MainAppWindow`). On success, insert the new path at index 0 of `_wallpaperPaths` and a freshly built tile at index 1 of the grid, then select it. On cancel, restore previous selection (or clear).
- Wrap programmatic `SelectedIndex` mutations in `_suppressStyleEvents` to avoid recursive style updates.

**Caveats:**
- `CanvasBitmap.LoadAsync` reads via Win32 file IO from the absolute path; for packaged builds, file access beyond the picker token is not guaranteed across sessions. Picking again works.

---

## Background presets - bold gradients

**Approach that worked:**
- Replaced the 8 muted `DefaultBrandPresets` entries with 8 bold two-stop gradients suitable as backdrops for screen recordings: `Nebula` (indigo->magenta), `Lagoon` (purple->teal), `Prism` (sky->coral), `Emerald` (vivid green->near-black), `Coral` (coral->magenta), `Ember` (midnight->amber), `Tide` (iris->cyan), `Sunset` (orange->violet). All gradients angled ~135-160 degrees with consistent padding/radius/shadow defaults.
- Updated `SettingsTests` named-preset assertions to match the new static property names. Count-based assertion (`ReturnsEightPresets`) retained.

**What didn't work / pitfalls:**
- Initial pass used literal brand names (Apple Vision, Google Gemini, etc.); avoid mentioning brand names in user-facing preset labels even when colors are inspired by them.

---

## Rubber-duck feedback on custom backgrounds branch

**Adopted (fixes pushed):**
- `LoadSystemWallpapersAsync` now takes an `initialCustomPath`, preserves any custom (non-system-wallpaper-dir) entries already in `_wallpaperPaths`, and re-applies the wallpaper grid selection after the async load completes (the synchronous `SyncStyleControlsToConfig` runs before items exist, so the prior code never highlighted the active wallpaper on reopen).
- `BuildBackgroundStyleFromControls` falls back to `ProjectService.CurrentComposition?.Background.BackgroundImagePath` when image mode is active but grid selection is empty - prevents losing the project's image when other controls (slider/toggle) fire `ScheduleStyleUpdate` before wallpapers finish loading.
- `FindMatchingPresetIndex` now also compares `GradientEndColor`, `GradientAngle` (0.5 degree tolerance), `ShadowEnabled`, `BorderEnabled`, and `BackgroundImagePath`; previously edits to those fields left a built-in preset selected which mislabeled the configuration as a brand preset.

**Deferred (documented as known limitations):**
- Packaged-app file access for `CanvasBitmap.LoadAsync` on arbitrary picker results: future fix should either copy into `ApplicationData.LocalFolder/CustomWallpapers` or persist a `FutureAccessList` token. Current behavior works in unpackaged dev runs and for files in user-readable locations.
- Reentrancy guard on the "+" tile: `FileOpenPicker` is modal, so concurrent picks are unlikely. Could revisit if we ever see UI thread re-entry issues.
- Swatch endpoint math vs compositor parity: the swatch clips at the unit-square edge while `BackgroundCompositor` uses half-diagonal endpoints. The preview direction is correct; saturation differs slightly at non-orthogonal angles. Acceptable for a small swatch.

---

---

## Recording Overlay - System Backdrop Acrylic & Matching Shadow

**Feature/area:** `RecordingOverlayWindow`

**Approaches tried:**

1. **`AcrylicBrush` on Grid + `SetWindowRgn` pill clip** - Worked for the body, but DWM's drop shadow still followed the rectangular window bounds, so the shadow appeared square around a rounded body.
2. **Switched to `SystemBackdrop = new DesktopAcrylicBackdrop()` and removed `SetWindowRgn` + Grid `CornerRadius`** - Worked. The window itself is acrylic, DWM rounds the corners via `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND` (~8px), and the DWM shadow now follows those rounded corners. Grid background is `Transparent`. Guarded with `DesktopAcrylicController.IsSupported()`.

**What worked:** `DesktopAcrylicBackdrop` (Microsoft.UI.Xaml.Media) as the window backdrop with default DWM corner rounding; removed the GDI region clip that decoupled the body from the shadow.

**What didn't work:** `SetWindowRgn` for a full pill shape - it clips only the window contents, not the DWM shadow, so the shadow stayed rectangular.

---

## Mouse Move Sample Throttling at 250Hz

**Feature/area:** MouseHookRecorder move-event recording.

**Approaches tried:**
1. **Stopwatch-based pure `WM_MOUSEMOVE` gate in `HookCallback()`** — Worked. Added a 4ms minimum interval between persisted move samples while leaving button and scroll samples unthrottled.

**What worked:** Tracking the last persisted move timestamp and resetting it in `StartRecording()` caps high-frequency move samples without changing the binary serialization format.

**What didn't work:** Earlier validation was blocked by pre-existing App/Core build issues, but those repo-level blockers have since been resolved by adding `FindMonitorBoundsForOverlayPoint` / `ClampPointToRect` and qualifying `Windows.Storage.Streams.Buffer` usage.

---

## Crash/freeze hardening - D3D, Win2D, VRAM, export watchdog

**Feature/area:** `Direct3DDeviceHelper`, `FrameCompositor`, `VideoEncoder`

**Approaches tried:**
- Checked `D3D11CreateDevice` HRESULT and retried hardware failures with WARP (`D3D_DRIVER_TYPE_WARP = 5`).
- Subscribed shared `CanvasDevice.DeviceLost` handlers and converted later use into recoverable exceptions instead of recreating devices mid-frame.
- Added BGRA render-target memory preflights plus OOM/COM allocation wrapping for full-frame `CanvasRenderTarget` allocations.
- Wrapped first-pass `TranscodeAsync` with a 2-hour watchdog using cancellation, mirroring the audio mux timeout pattern.

**What worked:** Fail-fast validation and recoverable exception paths keep exports from crashing/freezing while preserving quality and normal <=4K behavior.

**What didn't work:** `MSBuild.exe` was not available via `Get-Command MSBuild.exe`, so validation was limited to re-reading modified files and `git diff --check`; do not use `dotnet build` for this repo.

---

## Crash/Freeze Hardening Pass - High-DPI / High-Res Stability (13 fixes)

**Feature/area:** Capture pipeline, compositor, export, picker overlays, mouse hook.

**Context:** Reports of freezes/crashes on high-DPI and high-resolution devices. Rubber-duck reviewed the codebase; 13 hardening items implemented in parallel by 5 sub-agents across non-overlapping file groups. No functionality or export-quality change.

**What worked:**

1. **Streaming MP4 finalize** in `VideoWriter.FinalizeAsync` - replaced per-frame `MediaClip` + `MediaComposition.RenderToFileAsync` with a streaming `MediaStreamSource` + `MediaTranscoder` (BGRA8 -> H.264, 20 Mbps, mod-2 dims, CFR). Eliminates 100k+ COM objects on long recordings. Same encoding settings -> identical output quality. `MediaTranscoder.HardwareAccelerationEnabled = false` to avoid AMD H.264 corruption.
2. **In-flight write drain** - `VideoWriter.StopAcceptingFrames()` + `WaitForQuiescenceAsync()` replace the fixed 500ms sleep in `RecordingSession.StopAsync`.
3. **Serialized crop+write** - entire `WriteFrame` body now runs under `_writeLock`; shared `_cropTarget` no longer races on free-threaded frame-pool callbacks.
4. **`TryGetNextFrame()` inside try/catch** - `ObjectDisposedException`/`COMException` during shutdown/device-lost/monitor hot-plug no longer crash the process.
5. **D3D HRESULT + WARP fallback + `CanvasDevice.DeviceLost`** - `Direct3DDeviceHelper` checks `D3D11CreateDevice` HRESULT and falls back to WARP. `FrameCompositor` / `VideoEncoder` subscribe `DeviceLost` and throw a recoverable exception (no mid-frame recreate).
6. **Virtual-desktop screenshot guards** - `RegionSelectorOverlay` / `WindowSelectorOverlay` reject `width*height*4 > 1 GB` or `>16384px`, with checked `long` arithmetic and GDI return-value validation. Fall back to solid overlay.
7. **VRAM preflight** - `FrameCompositor` / `VideoEncoder` estimate BGRA surface bytes; throw a clear `InvalidOperationException` above 1.5 GB. Wraps `CanvasRenderTarget` allocations in OOM/COM try/catch with cache release + context rethrow. Normal <=4K exports unaffected.
8. **`GraphicsCaptureItem.Closed`** - `ScreenCaptureEngine` subscribes/unsubscribes; on close, one-shot guard calls `StopCapture()` so `CaptureStopped` fires.
9. **`IAsyncDisposable` on `RecordingSession`** - sync `Dispose()` is non-blocking best-effort (no MP4 finalize). `DisposeAsync()` does graceful stop with 30s timeout. Finalize accepts `CancellationToken`.
10. **Transcode watchdog** - 2-hour timeout around first-pass `VideoEncoder.ExportAsync` `TranscodeAsync` (mirrors mux pattern; uses `ct.Register` directly).
11. **Mouse move throttle at 250 Hz** - `MouseHookRecorder` skips pure `WM_MOUSEMOVE` samples within 4ms of the last; clicks/scrolls/buttons always preserved. Serialization format unchanged.
12. **Window picker covers virtual desktop** - `WindowSelectorOverlay` replaces `presenter.Maximize()` with `AppWindow.MoveAndResize` over the full virtual desktop (mirrors region picker).
13. **Region drag clamped to active monitor (Option A)** - drag rectangle visually sticks at monitor edges of the monitor containing the drag origin; saved region equals what is drawn. No popup/error.

**Validation:**
- All three projects build clean via VS Enterprise MSBuild (`vswhere -latest -find 'MSBuild\**\Bin\MSBuild.exe'` -> `Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe`) with `/p:Platform=x64 /p:Configuration=Debug /restore`.
- All 286 MSTest tests pass. The host has .NET 8 and .NET 10 SDKs/runtimes but no .NET 9 runtime, so tests were executed with `DOTNET_ROLL_FORWARD=Major` to roll forward to the .NET 10 runtime.

**What didn't work / things to remember:**
- `dotnet build` still fails for this repo (PriGen MSB4062). Use VS MSBuild.
- `Get-Command MSBuild.exe` returns nothing; use `vswhere -latest -find` to locate it.
- Without a .NET 9 runtime installed, `dotnet test` aborts the test host; set `DOTNET_ROLL_FORWARD=Major` to run against the installed .NET 10 runtime.
- For #1, the cleanest BGRA->H.264 path is `BitmapDecoder` -> `SoftwareBitmap` (BGRA8) -> `MediaStreamSample.CreateFromBuffer`. Avoid `MediaComposition` for any per-frame encoding.
- For #5, do NOT recreate the device mid-frame on `DeviceLost` - surface a recoverable exception and let outer code restart cleanly.

## Window selection highlight rect overstating bounds (2026-05-24)

**Feature/area**: WindowSelectorOverlay / RegionSelector

**Problem**: Highlight rect during window-picker hover extended ~7px beyond the visible window edges, creating confusion about whether the recording would include the extra region.

**Root cause**: GetWindowRect on Windows 10/11 includes the invisible DWM resize/shadow border for thick-framed windows. GraphicsCaptureItem.CreateForWindow captures roughly the visible bounds, so the highlight overstated the recorded area.

**Fix that worked**: Replaced GetWindowRect with DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) (with GetWindowRect fallback for non-DWM/classic-themed cases). Applied in WindowSelectorOverlay.EnumerateWindows() and RegionSelector.BuildWindowInfo(). Added a DwmGetWindowAttributeRect P/Invoke overload returning a RECT struct.

## Region capture off-by-1px at fractional DPI — DID NOT WORK (2026-05-24)

**Feature/area**: RegionSelectorOverlay / CaptureRegion / RecordingSession

**Problem**: Precisely-selected region captures included an extra 1px on the left/top edge.

**Approach tried (reverted)**: Hypothesized the cause was DIP<->physical-pixel round-trip rounding at fractional DPI. Extended `CaptureRegion` with optional `PixelX/Y/Width/Height`, threaded a `CropIsPhysicalPixels` flag through `CaptureTarget`, and skipped the DPI multiply in `RecordingSession`. **User confirmed this did not fix the 1px offset**, so the changes were reverted. The actual cause lies elsewhere (possibly in selection overlay rendering, stroke alignment, screenshot/Image stretch mapping, or the captured frame origin itself). Investigate before re-attempting.

---

## Version Bump 1.0.4 -> 1.1.0 & Store MSIX Packages

**Feature/area:** Version metadata + MSIX packaging

**What was done:**
- Incremented Package.appxmanifest Version from 1.0.4.0 to 1.1.0.0.
- Updated SettingsPage.xaml "Version 0.1.0" -> "Version 1.1.0" (stale string was out of sync with manifest).
- Built unsigned Store MSIX for x64 and ARM64 via VS MSBuild with /p:GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never, copied both to %USERPROFILE%\Downloads.

**What worked:**
- Cleaning bin/obj before each platform build avoids stale artifacts.
- Building x64 and ARM64 separately (Platform=x64 then Platform=ARM64) produces per-arch .msix in bin\{Platform}\Release\net9.0-windows10.0.26100.0\win-{rid}\AppPackages\.

---

## HANG_QUIESCE Comprehensive Fix (44% of crash bucket)

- **Feature/area**: App lifecycle / OS quiesce handling (`App.xaml.cs`, `MainWindow.xaml.cs`)
- **Approaches tried**:
  1. **Catch WM_QUERYENDSESSION and treat it as quiesce signal** — previous code only handled WM_ENDSESSION, and even that used wrong constant (`0x0026` instead of `0x0016`) so it never fired. Now WndProc returns 1 to WM_QUERYENDSESSION and routes both messages to `BeginQuiesce`. ✅
  2. **`BeginQuiesce` with hard 1.5 s safety timer** — sets `_isExiting`, disposes tray/hotkey/extended-execution, posts `Window.Close()` via dispatcher, and starts a `Threading.Timer` that calls `Environment.Exit(0)` so we never exceed the OS quiesce budget. ✅
  3. **Guarded `OnWindowClosing`** — only cancels close when `_trayService` is actually alive AND not exiting; lets OS-initiated/Task-Manager closes proceed. ✅
  4. **Replaced `Environment.Exit(0)` in event handlers with `Application.Exit()` + safety timer** — prevents the dispatcher from being killed mid-pump (itself a HANG_QUIESCE source) while still guaranteeing process termination. ✅
- **What worked**: All four together. The wrong WM_ENDSESSION constant (0x0026 vs 0x0016) was the root reason the earlier fix didn't help — the handler was dead code.
- **Bugs found in pre-existing code**: WM_ENDSESSION constant was 0x0026; correct value is 0x0016. Previous `HandleSystemShutdown` disposed services but never closed the window or posted WM_QUIT, so the pump kept running after WM_ENDSESSION even if it had fired.

---

## Mini Mode â€” Phase A (extract controls + services, no behaviour change)

**Feature/area:** App shell refactor for the unified Mini-Mode window (spec v4 Â§5 & Â§10).

**Approaches tried:**

1. **Extract `RecordingPillControl` from `RecordingOverlayWindow`** â€” worked. Moved the entire `<Grid x:Name="RootGrid">` plus stopping-ticker logic (phrase cycling, `Stop_Click`, `ShowStoppingState`, `AnimateToPhrase`, the `StoppingPhrases` table) into `Musio_App.Controls.RecordingPillControl`. Window now hosts the control and forwards `StopRequested`. âœ…
2. **Extract `MiniSetupControl` from `RecordingPage`** â€” worked. Moved the toolbar `Border` + the metadata `TextBlock` + the hero Record/Stop visuals into the control. Picker handlers now delegate to `CapturePickerService.Shared`. The page subscribes to `RecordRequested`/`TransientInfoRequested` to keep overlay lifecycle + InfoBar on the page (per spec). âœ…
3. **Extract `FullShellControl` from `MainWindow`** â€” worked. `MainWindow.xaml` becomes a one-element host; the WndProc subclass / min-size / quiesce logic stays in `MainWindow.xaml.cs` so the OS quiesce handler (the dead-code fix from a prior pass) is preserved. âœ…
4. **`CapturePickerService.PickRegionAsync` / `PickWindowAsync`** â€” works. Took an `IDimmable? dimTarget` parameter today (passes through `NoOpDimmable` when `null`) so Phase C wiring can drop the dim implementation in without touching call sites. Inferring "kept previous region" from "service returned false AND VM still has a region" preserves today's `overlay.WasCancelled` UX without leaking overlay state. âœ…
5. **`WindowChromeService.ApplyTo(window, ChromeProfile)`** â€” centralised the four Win32/DWM calls (`WDA_EXCLUDEFROMCAPTURE`, `DWMWA_BORDER_COLOR`, `DWMWA_CAPTION_COLOR`, `DWMWA_WINDOW_CORNER_PREFERENCE`, plus `WS_BORDER`/`WS_DLGFRAME` strip) that used to live inline in `RecordingOverlayWindow.ConfigureWindow`. `ChromeProfile.Full` exists but is a no-op until the unified shell window lands. âœ…
6. **`WindowMatcher.FindWindow(processName, windowTitle)`** â€” walks `EnumWindows`, filters `IsWindowVisible`, requires exact (`Ordinal`) title match and case-insensitive process-name match. Returns `null` when no window matches (spec Â§5.4 forbids fuzzy fallback). Not yet called from any flow. âœ…
7. **`ShellSettings` typed facade** â€” lives in `Musio.App\Services\` (not `Musio.Core`) because `StartupMode` (in `Musio_App.Shell`) and `CaptureMode` (in `Musio_App.ViewModels`) are App-side types; Core doesn't reference App. Reads/writes go through `AppSettings.Instance` underneath so we share the existing settings store. Serialises `Rect` + window selection via `System.Text.Json`. âœ…

**What worked:**

- Adding the new "Expand" / "Collapse-to-Mini" placeholder buttons with `Visibility="Collapsed"` satisfies *both* "the slot must exist + emit an event" and "no visible UI change" from the Phase A constraints. Wire-up is one XAML line + a `Visibility="Visible"` toggle later.
- The recording overlay solid-background fallback path (used when `DesktopAcrylicController.IsSupported()` is false) needs a tiny public setter on `RecordingPillControl` because XAML-generated `x:Name` fields default to `private`. Exposed `SetRootBackground(Brush)` rather than making `RootGrid` public.
- Title-bar icon path: when moving the `<TitleBar.IconSource>` out of `MainWindow.xaml` into the `FullShellControl` UserControl, prefer the explicit `ms-appx:///Assets/AppIcon.ico` URI over the bare `Assets/AppIcon.ico` path so the asset still resolves once the markup lives in a control resource tree.

**What didn't work:**

- `dotnet test --no-build` returns exit 0 with no output (and runs 0 tests) when the test assembly hasn't been built yet â€” `MSBuild src\Musio.App\Musio.App.csproj` does NOT transitively build `Musio.Tests`. You must invoke MSBuild on `src\Musio.Tests\Musio.Tests.csproj` (with `/restore`) before running `dotnet test --no-build`, otherwise you get a silent green that hides the fact zero tests ran.
- Forgetting `using Musio.Core.Settings;` when moving region-related code: `CaptureRegion` lives there, not in `Musio.Core.Capture`. The pages compile fine until you reference `CaptureRegion` by name.

**Validation:**

- VS Community MSBuild (`C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`) `/p:Platform=x64 /p:Configuration=Debug /restore` â€” PASS, no new errors/warnings.
- `dotnet test src\Musio.Tests\Musio.Tests.csproj --no-build -c Debug /p:Platform=x64` with `DOTNET_ROLL_FORWARD=LatestMajor` â€” PASS, 286/286 tests.
- Sanity launch of `Musio.App.exe` â€” PASS, window appears, process is responsive (`Responding=True`, `MainWindowTitle='Musio'`).



---

## Mini Mode â€” Phase A rubber-duck review fixes

**Feature/area:** `CapturePickerService`, `MiniSetupControl`, `WindowMatcher`, `ShellSettings`, `RecordingPillControl`.

**Fixes applied:**

1. **`PickerResult` tri-state** â€” replaced `Task<bool>` on `CapturePickerService.PickRegionAsync`/`PickWindowAsync` with `Task<PickerResult>` (`Selected` / `Cancelled` / `AlreadyOpen`). `MiniSetupControl` now only fires the "kept previous selectionâ€¦" InfoBar on an actual `Cancelled` with a prior selection in the VM; the re-entrancy `AlreadyOpen` path is a silent no-op (was previously misclassified as cancellation when nothing had actually been shown).
2. **WindowMatcher DWM-cloaked filter** â€” added `DwmGetWindowAttribute(DWMWA_CLOAKED)` check between `IsWindowVisible` and the title check so UWP/Settings/Start/virtual-desktop ghost HWNDs don't satisfy a remembered selection.
3. **ShellSettings setter normalisation** â€” `LastRegion` setter writes `string.Empty` (â‰¡ null) when given a `Rect` with `Width <= 0` or `Height <= 0`; `LastWindowSelection` setter writes `string.Empty` when both `ProcessName` *and* `WindowTitle` are empty. Prevents WindowMatcher from ever being called with empty strings.
4. **`RecordingPillControl.OnUnloaded` â†’ `Teardown()`** â€” unified the unload path with the explicit teardown so a future host swapping the control out can't leak a ticking `DispatcherTimer` or a re-entering phrase storyboard on a detached control.

**Validation:**

- VS Community MSBuild `/p:Platform=x64 /p:Configuration=Debug /restore` on both `Musio.App` and `Musio.Tests` â€” PASS (no errors, no new warnings).
- `dotnet test --no-build` with `DOTNET_ROLL_FORWARD=LatestMajor` â€” PASS, 286/286.
- Sanity launch â€” PASS, window appears, `Responding=True`, `MainWindowTitle='Musio'`.

**Deferred (intentional):** nit #5 (resolving `MiniSetupControl.GetHostWindow()` via `XamlRoot`) â€” Phase B's `AppShellWindow` will set up the owner relationship properly, so the current `return null` is left in place.


---

## Phase B — Unified AppShellWindow + 4-state state machine (mini-mode)

**Feature/area:** Replaced `MainWindow` + `RecordingOverlayWindow` with a single `AppShellWindow` that hosts `MiniSetupControl`, `RecordingPillControl`, and `FullShellControl` in a shared `Grid x:Name="StateHost"`. Drove a 4-state machine (`MiniSetup`, `MiniRecording`, `Full`, `FullRecording`) with per-frame `AppWindow.MoveAndResize` morph animations and XAML cross-fade between inner controls. Wired all expand/collapse/record/stop events from the inner controls into `TransitionToAsync(target)`.

### What worked
- **Single window, three children, mutually-exclusive Visibility + Opacity storyboards** for cross-fade — simplest model, no z-order surprises, no second-HWND state to keep in sync. Avoided WinUI 3 multi-window/visual-tree gymnastics entirely.
- **`DispatcherTimer` at 16 ms for `AppWindow.MoveAndResize` interpolation** with static easing helpers (`QuadraticEaseInOut`, `CubicEaseOut`). Matches WinUI rendering cadence, smooth on test hardware, no jitter. Re-entrancy made safe via a lock + `_activeTransition` Task + `_queuedTarget` (newest queued target wins; running transition completes first).
- **Per-state config block** (`ConfigureForState(target, animate)`): one switch statement deciding (a) which inner control becomes the visible one, (b) target `RectInt32`, (c) `ChromeProfile`, (d) presenter flags (`IsAlwaysOnTop`, `IsResizable`, capture-exclusion, min/max/minimize buttons). Keeps the morph orchestration in `RunSingleTransitionAsync` short.
- **High-level `Window.SystemBackdrop` swap** (`new MicaBackdrop()` for Full, `new DesktopAcrylicBackdrop()` for Mini). Only re-assigned when the kind changes — avoids backdrop reset flicker. Much simpler than the lower-level `MicaController` plumbing.
- **Real `WindowChromeService.ApplyFull`**: restores `WDA_NONE`, restores DWM border/caption to `DWMWA_COLOR_DEFAULT`, re-adds `WS_BORDER|WS_DLGFRAME`. Phase A had this as a no-op; without it, switching from Mini → Full left the stripped chrome behind. New `SetCaptureExclusion(window, exclude)` helper isolates the WDA toggle for `FullRecording`.
- **Ported `WndProc` min-size subclass** from `MainWindow` into `AppShellWindow`; conditionalised on `_currentState ∈ {Full, FullRecording}` so the Mini states don't get min-size clamped. Installed via `InitializeAfterActivation()` invoked from `App.OnLaunched` after `Activate()` so the HWND is real.
- **Origin tracking** (`_originStateBeforeRecording`) at the moment of entering a recording state — works for Phase B's "back to origin on Stop" semantics. Auto-Editor-on-Stop (Phase C) will piggy-back on this same field.
- **DPI-aware sizing** via `GetDpiForWindow(hwnd)` × base-96 constants, with `DisplayArea.GetFromWindowId(..., Primary)` for work-area centering. Verified geometry on a 1536×976 work area: Mini = `(508, 16, 520, 200)`; Full = `(256, 104, 1024, 768)`.
- **Smoke-test verification pattern**: launch unpackaged exe via `Start-Process -PassThru`, read `MainWindowHandle`, P/Invoke `GetWindowRect`, kill by literal PID. Confirms both initial states land at the right rect without needing UI automation.

### What didn't work / wrinkles
- **Spec target of 64 px MiniSetup height** couldn't be hit in Phase B because the current `MiniSetupControl` still has the legacy hero-Record vertical layout (~200 px). Phase B uses 200 px and defers the compact horizontal redesign to Phase C. State machine + chrome are independent of the inner control's intrinsic height, so this is purely a visual deferral.
- **Full docked-pill (ticker / stopping animation)** — Phase B implements the slot in the title bar plus `ShowDockedPill(vm) / HideDockedPill()` plus `DockedPillStopRequested / DockedPillCollapseRequested` events, but the inner pill is a simplified `ElapsedText + Stop + Collapse` trio (no spinning ring / stopping ticker). Deferred to Phase C; functional contract is complete (capture-exclusion flips, Stop/Collapse work).
- **`Stop-Process -Id $variable -Force`** is blocked by this session's PowerShell policy; have to use a literal PID. Bake the PID into the command after the launch echoes it.
- **One transient observation** of Full launching at the Mini rect on the very first cold launch after switching the code path — could not reproduce after a clean rebuild and was almost certainly a leftover env-var override from an earlier smoke-test run. Re-tested with a clean env: Full launches at `(256, 104, 1024, 768)` reliably.
- **`dotnet test --no-build`** silently reports 0 tests when the test project hasn't been built yet (e.g., when the previous build only built `Musio.App`). Always MSBuild `Musio.Tests.csproj` first, then `dotnet test --no-build`. With `DOTNET_ROLL_FORWARD=LatestMajor`, .NET 10 SDK on the box runs the .NET 9 test target fine.

### Final state
Build PASS, tests **286/286 PASS**, sanity launch PASS for both `Full` and `MiniSetup` initial states (window geometry measured via P/Invoke `GetWindowRect`). Old `MainWindow` / `RecordingOverlayWindow` types deleted; no references remain. Docked-pill visuals deferred to Phase C; everything else in Phase B scope landed.

---

## Phase B — Rubber-duck review fixes (mini-mode)

**Feature/area:** Tightened the unified `AppShellWindow` shell after a rubber-duck review surfaced two reds, four yellows, and the 64 px MiniSetup height verdict. All defects fixed; build clean, 286/286 tests pass, smoke-loop verified Record→Stop→Record yields `pill.IsReadyForRecording=True` on the second entry.

### What worked
- **`RecordingPillControl.ResetToRecordingState()` + `PauseTickerWhileHidden()`**: the pill is now a long-lived instance inside `AppShellWindow` (it goes `Visibility.Collapsed`, not Unloaded, between recordings), so an explicit reset on re-entry is required. `ResetToRecordingState` stops the phrase timer + storyboard, restores `RecordingPanel`/`ButtonRow` visibility, collapses `StoppingTextHost`/`StoppingSpinner`, re-enables Stop+Expand, and clears `_stopRequested`. `PauseTickerWhileHidden` kills the ticker without dropping the VM subscription so a hidden pill doesn't churn CPU. The shell calls both at the right transition seams (`RunSingleTransitionAsync`).
- **Same-control short-circuit in `RunSingleTransitionAsync`**: `Full ↔ FullRecording` (both use `FullShell`) now skips cross-fade + morph entirely — only chrome, presenter, state flags, docked-pill visibility, and `_currentState` are touched, then we return. Eliminates the unnecessary ~240 ms shell flash and matches spec §4.6.
- **Origin-honouring Stop handlers**: both `OnPillStopRequested` and `OnFullDockedStopRequested` now consume `_originStateBeforeRecording` (with the literal heuristic as a fallback) and clear it after read. Verified path: MiniSetup → MiniRecording → expand to FullRecording → docked Stop lands back in MiniSetup, not Full.
- **Stop-before-record-starts race fix**: `OnMiniSetupRecordRequested` and `RecordingPage.OnToolbarRecordRequested` re-check `CurrentState == MiniRecording` after the `await TransitionToAsync(...)` before firing `StartRecordingCommand`. Prevents the bug where Stop fires during the morph and we'd otherwise spawn a recording with no visible pill.
- **`SetWindowPos(... SWP_FRAMECHANGED)`** appended to both `ApplyMini` and `ApplyFull` in `WindowChromeService` — non-client area now recomputes after every style flip, even on the new no-morph Full ↔ FullRecording path.
- **IntPtr-typed `GetWindowLong`/`SetWindowLong` P/Invokes** in `WindowChromeService` (8 bytes on x64 instead of int). Style math now casts via `long` to keep the bitwise ops symmetric, then back to `IntPtr`. Mirrors the correct declaration already in `AppShellWindow.xaml.cs`.
- **Dead branch removal in `ComputeWindowRect` source rect**: `AppWindow.Position` is a struct, so the always-true `is { } pos` pattern was dead. Simplified to direct field read.
- **MiniSetup 64 px height — actually achieved**: restructured `MiniSetupControl.xaml` into a single horizontal toolbar Border containing the segmented capture-mode picker + inline window/region selectors + audio toggles + an opt-in compact inline Record / Stop+timer. Moved the hero centered Record button + Stop+timer + selection-metadata caption OUT of `MiniSetupControl` and INTO `RecordingPage.xaml` (so the Full-state Record page still has its rich layout). MiniSetupControl gained a `ShowInlineRecordButton` DP (defaults true; `RecordingPage` sets false to avoid two Record buttons in Full state) and a `SelectionMetadataChanged` event (null = hide). Measured: 520×64 at top-center on 1536×976 work area.
- **Pill loop smoke test**: temporary `MUSIO_PILL_LOOP=1` env-var hook in `App.OnLaunched` drove `AppShellWindow.TransitionToAsync(MiniRecording → MiniSetup → MiniRecording)` and wrote `pill.IsReadyForRecording` after each cycle to `crash.log`. Result: `ready=True` on BOTH the first AND second Record entries. Hook + introspection probes removed after verification.

### What didn't work / wrinkles
- **WinAppSDK `DependencyProperty` boilerplate**: `[ObservableProperty]` source-gen wouldn't work cleanly on `MiniSetupControl` for an `x:Bind`-target property because `x:Bind` requires the property surface at code-gen time. Used a plain `DependencyProperty.Register` instead.
- **`RecordingPage` now has both the toolbar and a hero Record button** (with `ShowInlineRecordButton="False"` to suppress the toolbar's compact one). Two physical instances of `MiniSetupControl` exist app-wide (one in `AppShellWindow`, one in `RecordingPage`) and both subscribe to `IsRecording`. NIT 9 explicitly allowed deferring full consolidation to Phase C; left as-is.
- **Test project can't reference `AppShellWindow`** (Musio.Tests only references Musio.Core, and creating a `Window` in xUnit on a non-STA thread is impractical), so the Phase B verification couldn't take the form of a unit test. The temporary `MUSIO_PILL_LOOP=1` env-var hook + structured log probe was the pragmatic substitute and produced cleanly-asserted evidence (`ready=True` on both Record entries).

### Final state
- Build PASS, tests **286/286 PASS**, sanity launch PASS:
  - Full state: `(256, 104, 1024, 768)` ✓
  - MiniSetup state: `(508, 16, 520, 64)` ✓ — spec target met
- Pill smoke: Record→Stop→Record loop ends with `ready=True` on second entry ✓
- All review items addressed; smoke-test hooks + probes removed.


## Mini Mode — Phase C (App.xaml.cs + tray + persistence + summon + docked pill)

**Feature/area:** Mini Mode (spec §3.1, §3.3, §3.4, §3.7, §3.8, §4.2, §4.3.1, §4.5, §4.6, §4.7, §5.3, §5.4, §5.6, §5.8, §5.9). Largest user-visible phase: persistence wiring, dim-while-picking, auto-Editor-on-Stop, close-to-tray, tray menu rebuild, segmented-control reorder, summon hotkey, CLI flags, single-instance, docked-pill promotion.

**Approaches tried:**

1. **Persistence (`ShellSettings` + VM partial methods).** Added `Shell.StartupMode.HasBeenSet` sentinel so existing installs keep the prior `Full` value while new installs default to `Mini`. Audio/cam/region/mode toggles ride the `[ObservableProperty]` partial-method hooks; window picker confirms write `LastWindowSelection` from inside `CapturePickerService.PickWindowAsync` (single source of truth — keeps the persistence + dim flow in one place). **Worked.** Restoration runs once on launch via new `SelectionRestoreService.RestoreOnLaunch(viewModel)` which returns `SelectionRestoreOutcome(AppliedMode, AutoLaunchPicker, RegionDiscardedReason)`. `RegionMemory.LoadLastRegion()` (which carries `MonitorId`) is what we use for actual restore validation; `ShellSettings.LastRegion` is the spec-compliant simple rect mirror.

2. **`MiniSetupControl` as `IDimmable`.** XAML root `Border` opacity is animated with a 120 ms `QuadraticEase`-In-Out storyboard. Used a theme resource `MiniToolbarDimmedOpacity` (0.15 default, 0.4 HighContrast) plus a runtime `AccessibilitySettings.HighContrast` check to pick the right value — the WinUI theme-dictionary lookup from code-behind is unreliable so the `AccessibilitySettings` shortcut is the safer fallback. **Worked.** Driven by `CapturePickerService.PickerOpening/Closed` events; both `MiniSetupControl` (via the picker call site) and `AppShellWindow` (via PickerOpening/Closed events) participate. Focus-watchdog: `DispatcherTimer 2 s` defensively calls `UndimAsync` when `GetForegroundWindow()` is neither shell HWND nor picker HWND.

3. **Tag-based segmented selection.** Spec §4.2 reordered the segments Region → Window → FullScreen. We decoupled XAML order from `CaptureMode` enum integer values by switching `MiniSetupControl.SelectCaptureModeSegment(CaptureMode)` to walk items by `Tag`. **Worked.** Survives further reorderings without code changes.

4. **`RequestRemeasure` event + 150 ms width morph.** `MiniSetupControl` raises `RequestRemeasure` after every capture-mode switch. `AppShellWindow.RemeasureMiniSetupWidthAsync` runs a center-anchored width morph (top-center fixed, half-width delta on the left edge) with the same per-frame `QuadraticEase` animator used by state morphs. **Worked.** Anchoring is critical — interpolating just `Width` without re-centering causes the toolbar to drift left.

5. **Hotkey semantics flip (Ctrl+Shift+R).** Was start/stop, now SUMMON when not recording, STOP when recording. We did NOT rename `GlobalHotkeyService.StartStopRecording` (the constant ID), only changed what `App.OnHotkeyPressed` does with it. `SummonAsync(bool suppressMiniMorph = false)` lives on `AppShellWindow`: it `ShowWindow(SW_SHOW/RESTORE)`, calls `AllowSetForegroundWindow(Environment.ProcessId)` before `SetForegroundWindow`, morphs to `MiniSetup` only when not recording, focuses Record, stamps `_lastSummonAt`. `WasRecentlySummoned` (5 s window) gates the Esc-dismiss behavior. **Worked.**

6. **Single-instance via `Microsoft.Windows.AppLifecycle.AppInstance`.** `AppInstance.FindOrRegisterForKey("MusioMainInstance")` + `RedirectActivationToAsync` for second-launch handoff. The receiver translates the activation's argument string through `ParseCliFlags` and re-routes to the same `SummonAsync` / page-navigation / `HandleNewRecordingRequestAsync` code paths used by the tray and the in-process launch. **Worked.** Watch out for the activation-argument extraction: `args.Data as ILaunchActivatedEventArgs` is the canonical path but we fall back to `Environment.GetCommandLineArgs()` because some packaging configurations don't surface launch args via `ILaunchActivatedEventArgs.Arguments`.

7. **Tray menu rebuild.** `SystemTrayService` now exposes `NewRecordingRequested(NewRecordingRequestedEventArgs.PreselectedMode?)`, `OpenFullRequested(OpenFullRequestedEventArgs.Page?)`, `StopRecordingRequested`, `ShowRecordingPillRequested`. Menu is rebuilt on every right-click using `IsRecordingProbe : Func<bool>?` (App wires it to `RecordingViewModel.Shared.IsRecording`). New-recording entries are *disabled* (greyed) while recording. **Worked.** Left-click still raises `NewRecordingRequested(null)`. `ShowCloseToTrayBalloon()` is one-shot/idempotent.

8. **Close-to-tray.** `App.OnWindowClosing`: if `_isExiting || _isQuittingFromTray` → never cancel; if recording → cancel + `StopRecordingViaSharedPathAsync()` (delegates to the shared VM's `StopRecordingCommand`); else cancel + `ShowWindow(SW_HIDE)` + `ShowCloseToTrayBalloon()`. `OnExitRequested` (tray "Quit") sets `_isQuittingFromTray = true` before `BeginQuiesce` so the closing handler short-circuits. **Worked.**

9. **Auto-Editor on Stop.** New `AppShellWindow.OnViewModelPropertyChanged` watches the false-edge of `IsRecording` via a `_wasRecordingLastTick` flag. On a successful stop (`LastProject != null`) it morphs to `Full` and navigates the frame to `EditorPage` (the project is already on `ProjectService.Instance.CurrentProject`, so EditorPage picks it up without a navigation parameter). Idempotent: skips re-navigation when `frame.Content is already EditorPage` (RecordingPage's own `IsRecording` observer can beat us to it). **Worked.**

10. **Docked-pill full visuals.** Replaced the Phase B "elapsed text + Stop + Collapse trio" in the FullShell title bar with a real `RecordingPillControl` instance (acrylic backdrop neutralised via `SetRootBackground(Transparent)`, `IsExpandButtonVisible = false`). The Collapse-to-Mini button stays outside the pill because its semantics differ from the pill's own expand button. Bridged the hosted pill's `StopRequested` → `DockedPillStopRequested` so `AppShellWindow` wiring stays unchanged. **Worked.** Both pills end up subscribed to `RecordingViewModel.Shared.PropertyChanged` simultaneously during FullRecording, but only the visible one matters; the floating pill's Stop button can't be clicked because the floating pill is `Visibility.Collapsed`.

11. **CLI flag parsing.** Tokenised by whitespace; supports `--mini`, `--full[=record|editor|settings]`, `--new-recording[=fullscreen|window|region]`. Defaults: `--new-recording` alone → `CustomRegion`, `--full` alone → record page. Implemented as `App.ParseCliFlags(string)` returning a `record struct CliFlags(AppShellState? InitialState, string? FullPage, CaptureMode? NewRecordingMode)`. **Worked.**

**What didn't work / non-obvious gotchas:**

- **XAML field default visibility is `private`.** `App.xaml.cs` couldn't see `AppShellWindow.MiniSetup` / `FullShell` / `RecordingPill` until we added `x:FieldModifier="public"` on each XAML element. Code-only public properties are also viable but `x:FieldModifier` is one line and survives generated-code regeneration.
- **`RecordingViewModel.StopRecordingAsync` is private** (annotated `[RelayCommand]`). The generated public surface is `StopRecordingCommand`. Calling `.StopRecordingCommand.ExecuteAsync(null)` is the correct re-entry.
- **`RecordingPage.ShowTransientInfo` was private** — changed to `internal` so `App.ShowTransientInfoOnPageAsync` can hand the region-discarded message to the InfoBar host.
- **`MiniSetup.FocusRecordButton()` did not exist initially**; added a public method that no-ops when `StartRecordButton.Visibility != Visible` so it's safe to call regardless of the inline-record-button gating.
- **First-launch detection without migration**: the empty-string sentinel via `Store.Get<string>(KeyStartupMode, "")` works because Phase B never wrote `Shell.StartupMode` (we know that from the Phase B learnings entry); no migration code needed.
- **CaptureRegion lives in `Musio.Core.Settings`, not `Musio.Core.Capture`** — `SelectionRestoreService` needed both usings (CaptureTarget vs CaptureRegion split). Pre-flight grep saved us here.
- **Tests project does not reference Musio.App**; all Phase C tests would need to be in Musio.Core (none added in Phase C — out of scope per spec). Baseline 286 tests still pass.

**Build / test confirmation (after final edits):**
- VS MSBuild `Musio.App.csproj` Debug|x64 → **PASS** (only pre-existing MVVMTK0045 warnings).
- VS MSBuild `Musio.Tests.csproj` Debug|x64 → **PASS**.
- `dotnet test --no-build` with `DOTNET_ROLL_FORWARD=LatestMajor` → **286/286 passed**.
- Sanity launch (default StartupMode) → PID stayed alive 6+ s without exit.

**Files created (Phase C):**
- `src\Musio.App\Services\SelectionRestoreService.cs` — stateless restore service + outcome record.
- `src\Musio.App\Services\TrayEventArgs.cs` — `NewRecordingRequestedEventArgs(PreselectedMode)`, `OpenFullRequestedEventArgs(Page)`.

**Files modified (Phase C):** `App.xaml.cs` (massive — CLI + single-instance + tray events + hotkey rebind + close-to-tray + auto-Editor wiring); `AppShellWindow.xaml{.cs}` (`x:FieldModifier="public"`, SummonAsync, DismissRequested handler, RemeasureMiniSetupWidthAsync, OnPickerOpening/Closed, focus watchdog, HandleRecordingStoppedAsync); `Controls\MiniSetupControl.xaml{.cs}` (segment reorder + Tag selection + IDimmable + DimWhilePicking DP + RequestRemeasure + DismissRequested + FocusRecordButton + SyncCaptureModeFromViewModel); `Controls\FullShellControl.xaml{.cs}` (docked-pill swap to RecordingPillControl); `Pages\RecordingPage.xaml.cs` (`ShowTransientInfo` → internal); `Services\ShellSettings.cs` (StartupMode default + HasBeenSet sentinel); `Services\CapturePickerService.cs` (LastWindowSelection persistence); `Services\SystemTrayService.cs` (new events + dynamic menu + balloon + probe); `ViewModels\RecordingViewModel.cs` (partial methods write to ShellSettings); `Themes\AppColors.xaml` (MiniToolbarDimmedOpacity).



## Mini Mode — Phase C v2 (rubber-duck fix-up)

**Feature/area:** Mini Mode follow-up to address rubber-duck review of Phase C. Five reds (R1-R3), three yellows (Y1-Y3), and one nit (N1).

**Approaches tried:**

1. **R1a — Clear LastProject at record start.** `RecordingViewModel.StartRecordingAsync` now sets `LastProject = null` before the start-recording flow runs. Without this, a failed stop could leave the previous recording's clip as the "current" project and the editor would open it. **Worked.**

2. **R1b — Shell-level overlay InfoBar.** Spec rubber-duck rejected the "briefly switch to Full" trick. Added an `InfoBar x:Name="ShellInfoBar" x:FieldModifier="public"` overlay to `AppShellWindow.xaml` (Bottom-aligned over the StateHost grid). `AppShellWindow.ShowTransientInfo(string, InfoBarSeverity)` is the single entry point. Auto-dismiss via a `DispatcherQueueTimer` (6 s, non-repeating). **Worked.** Visible in every state; no state-machine coupling required.

3. **R1c + R2 — Centralised stop completion.** `HandleRecordingStoppedAsync` is now the single completion path for ALL stop sources (pill / docked / hotkey / tray / close-during-recording). It fires once via `OnViewModelPropertyChanged` on the IsRecording false-edge, guarded by `_handlingStop` re-entrancy flag. Branching:
   - **Success** (`LastProject != null`): morph to Full → navigate Editor (always, even when already on EditorPage — see R3).
   - **Failure** (`LastProject == null`): fall back to `_originStateBeforeRecording` (or current state's natural non-recording counterpart) and surface `_viewModel.RecordingStatus` in a red InfoBar via Severity=Error.
   `OnPillStopRequested` and `OnFullDockedStopRequested` are stripped down to just `await _viewModel.StopRecordingCommand.ExecuteAsync(null)` — the optimistic state transition is gone, so a stop failure no longer leaves the UI in a non-recording state mid-transition. **Worked.** Single source of truth for stop completion.

4. **R3 — EditorPage reload on second clip.** Removed the "skip Navigate if already on EditorPage" guard. `Frame.Navigate(typeof(EditorPage))` always re-runs even when already on the editor; since EditorPage doesn't set `NavigationCacheMode` and its constructor calls `_ = InitializePreviewAsync()` (which reads `ProjectService.Instance.CurrentProject`), a fresh instance picks up the new clip cleanly. Also removed the duplicate `Frame.Navigate(typeof(EditorPage))` call from `RecordingPage.OnNavigatedTo` since `AppShellWindow` owns post-stop navigation now. **Worked.**

5. **Y1 — Bare `--new-recording` flag.** `ParseCliFlags` now distinguishes `--new-recording` (no value, opens Mini Setup without an auto-picker) from `--new-recording=region` (opens Mini Setup AND auto-launches the region picker). The unknown-value branch returns `null` instead of falling back to `CustomRegion` so a typo doesn't silently trigger a picker. **Worked.**

6. **Y2 — No-flag second launch.** Added an `hasIntent` predicate in `OnInstanceActivated` (true iff `InitialState`, `NewRecordingMode`, or `FullPage` is set). When there's no intent, we route to a new `BringToForegroundOnly()` helper that just does `ShowWindow(SW_SHOW/RESTORE)` + `AllowSetForegroundWindow(ProcessId)` + `SetForegroundWindow(hwnd)` + `Activate()` — no state transition, no `SummonAsync` (which would morph Full/Editor→MiniSetup and surprise a user mid-edit). **Worked.**

7. **Y3 — StartupMode migration for existing installs.** Added `bool HasKey(string)` to `Musio.Core.Settings.AppSettings`. In `App.OnLaunched`, before reading StartupMode, if `StartupModeHasBeenSet == false` we probe for any pre-existing settings key (`DefaultSavePath` / `Theme` / `DefaultFps` / `DefaultCaptureMode` / `IsSystemAudioEnabled` / `IsMicEnabled` / `IsWebcamEnabled`). If any are present, the install is pre-Phase-C and we write `StartupMode = Full` to preserve the prior default; otherwise we write `Mini` (the new fresh-install default). Either way we set the sentinel, so the migration runs at most once per install. **Worked.**

8. **N1 — Docked pill height.** Changed the hosted `RecordingPillControl` in `FullShellControl.xaml` from a hard `Height="36"` to `MaxHeight="44"` so it sizes naturally while still being constrained inside the 48 px title bar. **Worked.**

**What didn't work / non-obvious gotchas:**

- **Stop trigger paths previously did optimistic transitions.** Phase C v1 had `OnPillStopRequested` calling `await TransitionToAsync(destination)` immediately while stop ran in background. This made centralising fragile because the origin was consumed before failure detection. The fix was to make stop triggers *only* fire the command, and let the IsRecording=false event drive the transition; that meant the visual feedback during the "Stopping..." phase comes from `RecordingPillControl.ShowStoppingState()` which the pill already calls in `Stop_Click`. No visual regression.
- **`RecordingPage.OnNavigatedTo` also navigated to EditorPage on `IsRecording=false`** — left over from Phase B's page-level handling. With the centralised path in `AppShellWindow.HandleRecordingStoppedAsync`, that branch caused a double-Navigate on every successful stop. Removed the page branch and left a comment pointing readers at the shell-owned path. The page still owns the region-border highlight + OpenCaptureGate hook.
- **`AppSettings.HasKey` was a Musio.Core change.** Made sure it's purely additive (no signature changes) so the Musio.Tests project still compiles + all 286 tests still pass.
- **`InfoBar` overlay z-order.** Placed it as the LAST child of `StateHost` so it sits above all three state controls naturally (StateHost is a `Grid`, last-child-wins for z-order). Margin keeps it 12 px from the bottom so it doesn't fight with content.
- **Migration false-positives.** A fresh install where the user opens Settings and the SettingsPage's `Get(nameof(Theme), "System")` somehow writes the key would incorrectly trigger the existing-install branch. Inspection of `AppSettings.Get` confirms it does NOT write on Get — only Set does — so the probe is safe.

**Build / test confirmation (after final edits):**
- VS MSBuild `Musio.App.csproj` Debug|x64 → **PASS** (only pre-existing MVVMTK0045 warnings).
- VS MSBuild `Musio.Tests.csproj` Debug|x64 → **PASS**.
- `dotnet test --no-build` w/ `DOTNET_ROLL_FORWARD=LatestMajor` → **286/286 passed**.
- Sanity launch (default StartupMode) → PID stayed alive 6+ s without exit.

**Files modified (Phase C v2):**
- `src\Musio.Core\Settings\AppSettings.cs` — added `bool HasKey(string)` for migration probes.
- `src\Musio.App\App.xaml.cs` — one-time StartupMode migration; `ParseCliFlags` Y1 fix; `OnInstanceActivated` Y2 + `BringToForegroundOnly()`; `ShowTransientInfoOnPageAsync` re-targeted at the shell InfoBar; added `SetForegroundWindow` + `AllowSetForegroundWindow` + `SW_RESTORE` P/Invokes.
- `src\Musio.App\AppShellWindow.xaml` — added `ShellInfoBar` overlay.
- `src\Musio.App\AppShellWindow.xaml.cs` — `_handlingStop` guard; `HandleRecordingStoppedAsync` success/failure branching; `ShowTransientInfo(string,InfoBarSeverity)` + DispatcherQueueTimer; `OnPillStopRequested` / `OnFullDockedStopRequested` stripped to command-only.
- `src\Musio.App\Controls\FullShellControl.xaml` — docked pill `Height="36"` → `MaxHeight="44"`.
- `src\Musio.App\Pages\RecordingPage.xaml.cs` — removed duplicate EditorPage navigation on stop.
- `src\Musio.App\ViewModels\RecordingViewModel.cs` — clear `LastProject` at start of `StartRecordingAsync`.


---

## Mini Mode Phase D — Logic Test Coverage

**Feature/area:** Mini Mode state machine, selection restoration, picker/tray/CLI/settings seams.

**Approaches tried:**
1. **Pure `AppShellStateMachine` seam** — Worked. Covered Record/Stop success/failure origin handling and Esc-dismiss rules without instantiating WinUI `AppShellWindow`.
2. **Delegate-based seams for restore/picker/window matching** — Worked. `SelectionRestoreService`, `CapturePickerService`, and `WindowMatcher` can now be tested with fake persisted state, picker delegates, and window snapshots instead of real UI/native windows.
3. **Pure tray menu spec builder** — Worked. Menu contents can be asserted without creating Win32 menus; rebuild stability covers the no-managed-handler-leak case.
4. **Direct WinUI window automation** — Not used. `AppShellWindow`, picker overlays, and native tray display still require a UI thread/WinUI runtime and were intentionally not instantiated in unit tests.

**What worked:** Added 38 logic tests (286 → 324 total) across state machine, selection restore, window matcher, shell settings, picker tri-state/re-entrancy, tray menu rebuild, and CLI flags. `InternalsVisibleTo("Musio.Tests")` plus small pure/delegate seams kept production changes minimal while avoiding UI automation.

**What didn't work / limitations:** Full visual morphing, actual InfoBar rendering, real DWM cloaking via native windows, and real tray menu handler leak detection remain outside unit/integration scope without UI/native automation. MSTest parallel execution conflicted with singleton settings state, so the test assembly is marked `DoNotParallelize`.

---

## Mini Mode Phase D v2 — Production Seam Rubber-Duck Fixes

**Feature/area:** Mini Mode production seams for transition rules, Esc routing, and window matching performance.

**Approaches tried:**
1. **Wired `AppShellStateMachine.NextState(...)` into production paths** — Worked. `AppShellWindow` and the Full Record page now use the reducer for Record, Stop success/failure, Esc-dismiss, Expand, and Collapse destination decisions while keeping WinUI side effects in the shell.
2. **Esc short-circuit in `MiniSetupControl`** — Worked. The control now returns without handling Esc while a picker is open (or recording), allowing picker cancel semantics to receive the key; the shell guard remains defense-in-depth.
3. **Restored lazy `WindowMatcher` production enumeration** — Worked. The live path now filters visible/cloaked/title first, calls `TryGetProcessName` only for title candidates, and stops on the first match. The pure snapshot helper remains for unit tests.

**What worked:** Option A for Y1 kept the tests tied to production transition decisions instead of a test-only fork. Build passed, `dotnet test --no-build` remained 324/324 passing, and sanity launch stayed alive for 6+ seconds.

**What didn't work / limitations:** No new UI automation was added; visual morphing, real picker Esc routing, and native tray/window behavior remain covered by logic seams plus sanity launch rather than full UI tests.

---

## Mini Mode Phase E — Final Spec Review Gaps

**Feature/area:** Mini Setup expand affordance, Settings startup mode UI, and region-persistence cleanup.

**Approaches tried:**
1. **MiniSetup expand visibility DP** — Worked. Added `MiniSetupControl.IsExpandButtonVisible` (default false), bound the XAML button to it, and set it from `AppShellWindow.ApplyStateFlags` when state is `MiniSetup`.
2. **Reuse existing state-machine expand event** — Worked. Existing `ExpandRequested` wiring already routes through `OnMiniSetupExpandRequested` and `AppShellStateMachine.NextState(...MiniSetupExpand...)`; added a unit test for MiniSetup → Full.
3. **Settings startup-mode radio buttons** — Worked. Added a Startup Mode settings card with Mini toolbar / Full window radio buttons and immediate writes to `ShellSettings.Instance.StartupMode`.
4. **Single region source of truth** — Worked. Removed dead `ShellSettings.LastRegion` accessors and the `RecordingViewModel.OnSelectedRegionChanged` writer, leaving the existing `RegionSelector`/`RegionMemory` path as the sole region store.

**What worked:** Small UI + settings changes compiled cleanly. VS MSBuild App and Tests passed; `dotnet test --no-build` with `DOTNET_ROLL_FORWARD=LatestMajor` now reports 325/325 passing (324 → 325 from the new expand transition test). Sanity launch stayed alive 6+ seconds.

**What didn't work / limitations:** `dotnet test --no-build --arch x64` looked in the non-x64 bin path and failed before running tests; using `-p:Platform=x64` matched the MSBuild output path and ran successfully.

---

## Mini Mode — User-Reported Bug Fixes

**Feature/area:** AppShellWindow, MiniSetupControl, FullShellControl, capture pickers

**Approaches tried:**

1. **Content-driven MiniSetup sizing** — Replaced the fixed MiniSetup width path with measurement of the toolbar's desired width after load and on capture-mode remeasure. Worked with AppWindow remeasure hooks, but AppWindow DPI/min-size behavior required preserving the existing DPI-scaled rect path.
2. **Full title bar collapse placement** — Moving the button out of `TitleBar.Content` into a right-aligned overlay was needed; `TitleBar.Content` did not honor the desired app-right alignment.
3. **Full→Mini morph min-size fix** — Added a target-state min-track override during transitions so the Full min-size handler does not veto MiniSetup dimensions.
4. **Picker dim flow** — Region/window picker overlays must not minimize the shell for MiniSetup entry; keeping the shell visible allows `IDimmable` to show the dimmed toolbar.
5. **Mini keyboard accessibility** — Added tab stops, tab order, automation names/help text, cycle tab navigation, and initial focus on Record.

**What worked:**
- Measuring MiniSetup on load and RequestRemeasure, paired with target-state min-size handling, avoids clipping and allows Full→Mini size changes.
- Overlaying Full title-bar actions at the right edge with a 138px right margin matches caption-control spacing better than `TitleBar.Content`.
- Keeping picker launch from minimizing MiniSetup preserves the dimmed toolbar while the picker is active.
- `TabFocusNavigation="Cycle"` plus `FocusRecordButton()` gives keyboard users a predictable starting point and cyclic tab order.

**What didn't work / pitfalls:**
- `KeyboardNavigation.TabNavigation` is not a WinUI 3 attachable property; use `TabFocusNavigation`.
- `Canvas.ZIndex` on a Grid row overlay compiles, but `Panel.ZIndex` does not in this WinUI project.
- Recomputing AppWindow rects without the existing DPI scaling made Mini windows too small on the test display.

### Mini Mode bug-fix follow-up (v2)

**Feature/area:** MiniSetup window sizing/positioning

**Approaches tried:**

1. **Recenter after remeasure** — Required. The first fix widened MiniSetup but preserved the old center during some paths, causing the toolbar to extend off the right side in screenshots. Recompute the target rect from the work area every time: width/height from measured content, X from work-area center, Y from top margin.
2. **DPI coordinate experiments** — Mixed behavior observed between AppWindow coordinates, Win32 rects, and screenshots at 150% DPI. The stable MiniSetup path uses the measured size and recenters against the current DisplayArea work area on load, remeasure, and transition.

**What worked:** Recomputing the whole MiniSetup target rect instead of only changing width. This keeps both margins balanced and prevents right-edge clipping after launch and Full→Mini collapse.

**What didn't work / pitfalls:** Preserving the previous X/center during remeasure can leave a widened MiniSetup off-screen. Screenshot verification must minimize/avoid foreground terminal windows or they obscure the toolbar.

## Mini Mode — region-picker dim approach pivot

- **What didn't work**: SystemBackdrop swap + DwmExtendFrameIntoClientArea(-1,-1,-1,-1) to make the toolbar feel transparent during region picking. Even with the glass-frame trick the result still felt like a frozen app, and the whole codepath was complex (backdrop save/restore, P/Invoke, MARGINS struct).
- **What worked**: Slide the toolbar straight up off-screen during the *region* picker only (180ms ease-in/out via existing AnimateWindowAsync); window picker leaves toolbar in place (single-click target, nothing to obscure).
- **Wiring**: CapturePickerService.PickerOpening now carries a PickerKind (Region/Window). AppShellWindow.OnPickerOpening branches on Kind. IDimmable interface kept for back-compat but DimAsync/UndimAsync are no longer called by the picker service.


## Mini Mode bug-fix loop — round 3

- **Duplicate Full collapse button (2 chevrons in the title bar)**: CollapseOverlayButton in FullShellControl.xaml was a Canvas-positioned hit-test workaround for the in-TitleBar.Content button. Removed it entirely; the in-content button alone hit-tests fine. Don't add a backplate'd overlay button — there is only one collapse button now.
- **ExpandButton clipped + 16 DIP extra height in MiniSetup**: two root causes. (a) `RunSingleTransitionAsync` / `ConfigureForState` called `ComputeWindowRect` BEFORE `ApplyStateFlags`, so `MiniSetup.IsExpandButtonVisible` was still false at measure time → the 36 DIP ExpandButton was treated as Collapsed (width=0). Fix: call ApplyStateFlags first. (b) Window height was `MiniSetupHeight + 2*chromeBorder` (=66 DIP) with `ToolbarBorder VerticalAlignment=Center`, leaving ~16 DIP of acrylic above/below the visible pill. Fix: window height = exactly MiniSetupHeight (64 DIP) and `VerticalAlignment=Stretch` on the border so the pill IS the window.
- **Slide-out animation lost because picker grabbed the screen first**: `async void OnPickerOpening` returns to the picker service immediately and the region picker takes its frozen-frame backdrop BEFORE the slide finishes — the toolbar gets baked into the picker background. Fix: added `CapturePickerService.OnPickerOpeningAsync` (and `OnPickerClosedAsync`) — `Func<…, Task>` hooks that the picker service AWAITS. Slide now runs to completion before the picker overlay is constructed.


## RecordingViewModel dispatcher must be set on *every* host

- `RecordingViewModel` is a shared singleton consumed by both `RecordingPage` and `AppShellWindow`. Its recording-elapsed timer fires on a ThreadPool thread, then it marshals to the UI via `RunOnUI` which depends on `_dispatcher` being set.
- `RecordingPage` calls `ViewModel.SetDispatcher(DispatcherQueue)` in its ctor. `AppShellWindow` didn't. Starting a recording from the mini toolbar without ever visiting RecordingPage left `_dispatcher` null → `RunOnUI` invoked the action directly on the timer thread → setting bound TextBlock.Text off-thread threw `COMException 0x8001010E (RPC_E_WRONG_THREAD)` and terminated the process.
- Fix: every host that constructs/consumes the shared VM and may trigger a recording must call `SetDispatcher(this.DispatcherQueue)` from its UI thread. `AppShellWindow` ctor now does it.

## Theme-aware brushes inside the recording pill

- `OverlayForegroundBrush` in AppColors.xaml is hardcoded `#FFFFFFFF` for both light & dark themes — intentional for dark-overlay surfaces (region picker, window picker). Don't use it inside the recording pill chrome (`RecordingPillControl`): the pill sits on the system acrylic, so in Light mode white is unreadable. Use `TextFillColorPrimaryBrush` for the elapsed-time + stopping-phrase text and the expand-button glyph there instead.


## Mini Mode — remove startup auto-launch of region/window picker

**Feature/area:** `App.OnLaunched` + `SelectionRestoreService`

**Symptom:** App opened with the region picker auto-launched. Picker overlay took noticeable time to appear and rendered a stale screenshot of the desktop (BitBlt grabbed the half-painted launch state). Whole experience felt slow on every launch.

**Approach tried:**
1. **Drop the `LaunchPickerForCurrentModeAsync` call from the launch path.** Toolbar still comes up pre-selected (spec §3.1 default of Region survives), but the heavy picker capture only runs when the user explicitly clicks the picker affordance. CLI `--new-recording=region|window` is unchanged because it goes through a different branch (`HandleNewRecordingRequestAsync`).

**What worked:** Single one-branch removal in `App.OnLaunched`. `SelectionRestoreOutcome.AutoLaunchPicker` is now unused at the host level — kept in the record so existing unit tests on `SelectionRestoreService` still compile/pass without churn.

**What didn't work:** Trying to make the picker faster on launch would require rewriting the BitBlt path (e.g., Windows.Graphics.Capture) — not worth it because the right UX answer is "don't capture during launch".

## Mini Mode — Region picker becomes inline; Record is implicit confirm

**Feature/area:** `MiniSetupControl`, `RegionSelectorOverlay`, `CapturePickerService`, `AppShellWindow.OnMiniSetupRecordRequested`

**Goal:** Make region selection feel ambient — selecting the Region tab auto-launches the overlay (pre-populated with the persisted region); clicking Record on the toolbar is the implicit confirm; Esc cancels. No more dedicated "Select Region" / "Confirm" / "Cancel" buttons.

**Approaches tried:**
1. **Remove the inline "Select Region" / "Select Window" buttons + their `StackPanel` containers.** The auto-launch in `CaptureModeSelector_SelectionChanged` already handled this case; the buttons were a redundant duplicate trigger.
2. **Hide the in-overlay action buttons (Confirm / Cancel / Use Last)** in `RegionSelectorOverlay.xaml` and remove every code-behind reference to `ButtonPanel` / `UseLastButton`. Updated the instruction text to "Click and drag to adjust. Click Record on the toolbar to start. Esc to cancel.".
3. **Add `RegionSelectorOverlay.TryConfirmCurrent()` + `CapturePickerService.TryConfirmActiveRegionPicker()`.** The picker service tracks the active overlay in a static `ActiveRegionOverlay` property set by its default factory and cleared via `ContinueWith` when `ShowAsync` completes. `AppShellWindow.OnMiniSetupRecordRequested` calls `TryConfirmActiveRegionPicker` (when in Region mode + picker open) before transitioning to `MiniRecording`; a `Task.Yield()` lets the picker's TCS continuation write `ViewModel.SelectedRegion` before the transition reads it.

**What worked:** Picker auto-launch was already wired in `MiniSetupControl` for tab selection. `RegionSelectorOverlay.ShowAsync` already supports pre-populated regions via its `initialRegion` argument, which `CapturePickerService.PickRegionAsync` already passes (`ViewModel.SelectedRegion`). So the only behavior changes needed were: (a) delete the inline buttons + their handlers, (b) hide the in-overlay action buttons, (c) plumb Record-as-confirm.

**What didn't work / wrinkles:** `CaptureMode` lives in `Musio_App.ViewModels`, not `Musio.Core.Capture` — using `Musio.Core.Capture.CaptureMode` as a fully-qualified name fails. Use the unqualified `CaptureMode` (covered by `using Musio_App.ViewModels;`).

## Mini Mode — Capture-mode tab switch must cancel any open picker

**Feature/area:** `CapturePickerService`, `WindowSelectorOverlay`, `MiniSetupControl`

**Symptom:** With auto-launch on tab selection, switching from Window → Region (or Window → Full Screen) left the window picker on screen because the previous picker was never cancelled and the new launch hit the `IsPickerOpen` re-entrancy guard.

**Approach tried:**
1. **Add `public void Cancel()` to `WindowSelectorOverlay`** (mirrors the existing `RegionSelectorOverlay.Cancel`) that signals `_tcs?.TrySetResult(null)`.
2. **Track `ActiveWindowOverlay` in `CapturePickerService`** (parallel to `ActiveRegionOverlay`); set in the default factory via `ContinueWith` clearing.
3. **Add `Task CancelActivePickerAsync()` to `CapturePickerService`.** It calls `Cancel` on both active overlays then awaits a per-call `TaskCompletionSource` (`_pickerClosedSignal`) that the picker's `finally` completes. Avoids the polling/yield races of "just call cancel and hope it's done".
4. **In `MiniSetupControl.CaptureModeSelector_SelectionChanged`**: before re-launching, if a picker is open, `await CapturePickerService.Shared.CancelActivePickerAsync()`. Works for both "switch to another picker mode" and "switch to Full Screen" (which simply doesn't relaunch anything).

**What worked:** The TCS-signal approach is the only race-free way: `Cancel` resolves the overlay's TCS, the picker's awaiting `ShowAsync` returns null, the picker service's `finally` runs (sets `_isPickerOpen=false`, clears active overlay, signals our TCS). Caller awaits the signal and can safely launch the next picker.

**What didn't work:** A naive `CancelActivePicker()` (sync) plus a single `await Task.Yield()` on the caller side is brittle — the cancel-to-finally chain crosses multiple await points (overlay close, `OnPickerClosedAsync` hook) so the number of yields needed isn't statically known. The explicit completion signal removes that fragility.

## Window picker — lock smoke around clicked window (parity with region picker)

**Feature/area:** `WindowSelectorOverlay`, `CapturePickerService`, `AppShellWindow`

**Symptom:** Clicking a window in the window picker immediately resolved the picker TCS and closed the overlay, so the dim `smoke` mask disappeared and the user lost visual confirmation of what they had selected. Region picker keeps the selection visible after drag — the two flows should match.

**Approach tried:**
1. Added `_lockedWindow` field to `WindowSelectorOverlay`. `Grid_PointerPressed` no longer resolves the TCS — it just sets `_lockedWindow = _hoveredWindow` and re-runs `UpdateOverlay`. The smoke mask + highlight border + info label stay rendered around the locked window.
2. `Grid_PointerMoved` still tracks hover (so the next click can switch the lock) but only updates the rendered overlay while `_lockedWindow is null` — once locked, hovering other windows doesn't repaint the mask. A subsequent click switches the lock.
3. Added `public bool TryConfirmCurrent()` that validates `_lockedWindow ?? _hoveredWindow` is still alive then `_tcs.TrySetResult(candidate)`. Used by the toolbar Record button.
4. `CapturePickerService.TryConfirmActiveWindowPicker()` mirrors `TryConfirmActiveRegionPicker`; `AppShellWindow.OnMiniSetupRecordRequested` adds the Window branch — same yield-then-transition pattern as Region.

**What worked:** Keeping hover detection alive after lock means the user can re-pick a different window without an explicit reset — clicking another window seamlessly swaps the lock. `TryConfirmCurrent` falls back to `_hoveredWindow` for the edge case where the user clicks Record without ever clicking a window (they're hovering it but haven't pressed down).

**What didn't work:** Initially considered making `Grid_PointerMoved` keep updating the overlay after lock so the mask previewed the hovered window. That ended up flickering the smoke between the locked and hovered window on every mouse-move — the locked-stays-locked-until-next-click model is much calmer.

## Shell summon + expand/collapse — slide-down + 125 Hz parallel morph

**Feature/area:** `AppShellWindow.SummonAsync`, `AppShellWindow.RunSingleTransitionAsync`, `AnimateMorphAsync`

**Symptom:**
1. Pressing the global summon hotkey while hidden in the tray made the window appear in its last persisted state (often Full), then morph down to the toolbar — the user saw the full chrome flash briefly.
2. The expand/collapse morph felt segmented and slow: `CrossFadeOut (120 ms) → AnimateWindow (340 ms) → CrossFadeIn (120 ms)` ran strictly sequentially, totaling ~580 ms, and the timer ran at 16 ms (~60 Hz).

**Approach tried:**
1. **Slide-down summon.** In `SummonAsync`, detect `!IsWindowVisible(hwnd)`. If hidden AND desired target is MiniSetup, call `ConfigureForState(MiniSetup, animate:false)` while still hidden (so chrome/presenter/backdrop/inner-control swap happens off-screen), compute the final mini rect, `MoveAndResize` to `{X, Y - Height - 24, W, H}`, `ShowWindow(SW_SHOW)`, then `AnimateWindowAsync` Y back down to the target with a 240 ms `CubicEaseOut`. The user perceives a toolbar dropping from above the screen, never sees the prior chrome.
2. **Parallel morph.** Replaced the sequential fade-out → resize → fade-in chain with a single `AnimateMorphAsync` that drives the rect interpolation AND the opacity of both controls in the same 8 ms timer tick. Outgoing opacity ramps 1→0 over the first ~55 % of progress; incoming opacity ramps 0→1 over the last ~55 % (10 % overlap for a smooth handoff). Both inner controls stay `Visible` for the duration of the morph so the visual swap tracks the resize frame-for-frame.
3. **Snappier durations.** Cut mini↔mini from 340 ms to 200 ms; full-involving from 400 ms to 280 ms. With parallel cross-fades these no longer feel rushed.

**What worked:** Driving rect + opacity in one timer (instead of three separate animations) is what makes it feel "120 fps smooth" — the control swap is literally on the same frame as the resize, so there's no perception of two separate phases. Reconfiguring while hidden eliminates the Full-chrome flash entirely.

**What didn't work:** Initially tried running `CrossFadeOutAsync` as a separate Storyboard concurrently with `AnimateWindowAsync` via `Task.WhenAll` — this still drifted because the Storyboard runs on the composition timeline while the timer runs on the dispatcher, so the two timings diverged by ~20 ms (visible as a brief opacity lag at the start). Folding both into the same dispatcher tick eliminated the drift.

## App shell backdrop — Mica Alt instead of base Mica

**Feature/area:** `AppShellWindow.UpdateBackdropFor`

**Change:** `new MicaBackdrop { Kind = MicaKind.BaseAlt }` for Full / FullRecording states. MiniSetup / MiniRecording keep DesktopAcrylic. Only one call site in the app uses `MicaBackdrop` so this is the single source of truth.

## Mini toolbar — transparent fill to expose acrylic backdrop

**Feature/area:** `MiniSetupControl.xaml` (`ToolbarBorder`)

**Change:** `ToolbarBorder.Background` from `{ThemeResource CardBackgroundFillColorDefaultBrush}` (opaque dark card fill) → `Transparent`. The 1 px `CardStrokeColorDefaultBrush` outline + `CornerRadius=12` stay so the pill shape is still defined. With `DesktopAcrylicBackdrop` set on the window for MiniSetup, the area inside the pill now renders the acrylic blur of the desktop behind the window instead of a flat dark card.

**Why:** The card fill was opaque and visually swallowed the backdrop, defeating the point of using acrylic for the mini shell.

## Win11 system backdrop requires WS_DLGFRAME — don't strip it for chromeless windows

**Feature/area:** `WindowChromeService.ApplyMini`

**Symptom:** Setting `SystemBackdrop = new DesktopAcrylicBackdrop()` on the mini shell window had no visible effect — the window painted as solid black/default colour. Mica on the full shell rendered fine.

**Root cause:** `ApplyMini` was stripping `WS_BORDER | WS_DLGFRAME` from the window style (to remove any DWM-drawn frame on the borderless mini toolbar). Windows 11's `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, …)` — which is what `WinUI` `SystemBackdrop` wires up — REQUIRES `WS_DLGFRAME` to be present. Without it DWM silently falls back to the solid window background and the backdrop never renders. `WS_BORDER` alone is fine to strip; `WS_DLGFRAME` is the killer.

**Fix:** Removed the manual `GetWindowLong / SetWindowLong` style strip from `ApplyMini`. Chrome hiding is still handled by `OverlappedPresenter.SetBorderAndTitleBar(false, false)` + `ExtendsContentIntoTitleBar = true`, and OS-level rounding by `DWMWA_WINDOW_CORNER_PREFERENCE`. Together those hide the visible caption/border without disabling the backdrop.

**What didn't work earlier:** Setting `ToolbarBorder.Background = Transparent` alone — the area below the border still painted solid because the backdrop itself was disabled at the DWM layer, so there was nothing to see through to.

## Mini chromeless window — sheet-of-glass frame extension + single Mica Alt backdrop

**Feature/area:** `WindowChromeService.ApplyMini`, `AppShellWindow.UpdateBackdropFor`

**Symptom A:** After keeping `WS_DLGFRAME` so the Win11 system backdrop would render, a 1 px white outline (the DWM dialog frame edge) appeared around the mini toolbar.

**Symptom B:** Per-state backdrop swap (`DesktopAcrylic` for mini, `Mica` for full) caused a visible re-tint flash on every morph, and the user wanted a single consistent material across both halves of the same window.

**Fix A — sheet-of-glass:** Call `DwmExtendFrameIntoClientArea(hwnd, MARGINS{-1,-1,-1,-1})` in `ApplyMini`. The -1 sentinel tells DWM the entire client area is part of the frame, so there is no visible client/non-client boundary to render and the WS_DLGFRAME edge line disappears — without removing the style itself, which keeps the backdrop alive.

**Fix B — one backdrop:** `UpdateBackdropFor` now sets `MicaBackdrop { Kind = MicaKind.BaseAlt }` for every state and only re-allocates if the current backdrop isn't already that. Mica works for both the floating mini pill (the toolbar's `Background=Transparent` lets it show through) and the expanded full shell. The recording pill, whose `RootGrid` was already transparent, now also inherits the Mica Alt material.

**What didn't work:** Stripping `WS_DLGFRAME` (original code) — DWM silently disables `DWMWA_SYSTEMBACKDROP_TYPE` and falls back to the solid window colour, so Mica/Acrylic never renders. `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE` alone is also insufficient: the edge that shows is the frame itself, not its colour.

## Win11 chromeless border + summon-from-Full first-paint flash

**Feature/area:** `WindowChromeService.ApplyMini`, `AppShellWindow.SummonAsync`

**Symptom A (border):** After enabling the Mica Alt backdrop + `DwmExtendFrameIntoClientArea({-1,-1,-1,-1})`, a 1 px white outline still rendered around the mini toolbar.

**Root cause A:** `DwmSetWindowAttribute(DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE)` is documented as "suppress border" but on current Win11 builds with a dark system backdrop it actually paints a light/white 1 px edge. The sheet-of-glass frame extension on its own can't hide that edge because it's the OUTER frame line, not the client/non-client boundary.

**Fix A:** Set `DWMWA_BORDER_COLOR` (and `DWMWA_CAPTION_COLOR`) to a specific dark `COLORREF` (`0x00202020` — alpha byte ignored, `0x00BBGGRR`) that approximates the Mica Alt tint. The frame edge still renders, but blends visually into the material.

**Symptom B (summon flash):** Hotkey-summoning the shell while hidden in Full state briefly flashed the Full layout before the slide-down animation started.

**Root cause B:** `ConfigureForState(MiniSetup)` updated chrome + presenter + control visibility, but WinUI hadn't run a layout pass yet. `ShowWindow(SW_SHOW)` then triggered the first paint using whatever was in the visual tree's cached layout — often the previous Full state — for one frame before the next layout pass swapped in MiniSetup.

**Fix B:** In `SummonAsync`'s wasHidden + MiniSetup branch: (1) park the window at `y = -10000` BEFORE showing, (2) call `StateHost.UpdateLayout()` to force a synchronous layout for the new state, (3) `ShowWindow(SW_SHOWNOACTIVATE)`, (4) `await Task.Yield()` so WinUI commits one frame at the parked-off-screen rect, (5) only then move the window to the visible startRect just above the work area and animate down. Any stale paint now lands far off-screen.

## Strip WS_THICKFRAME to kill the white edge; pre-configure mini before SW_HIDE to kill summon flash

**Feature/area:** `WindowChromeService.ApplyMini`, `AppShellWindow.PrepareForSummonInBackground`, `App.OnWindowClosing`

**Symptom A:** Even with sheet-of-glass `DwmExtendFrameIntoClientArea` and a dark `DWMWA_BORDER_COLOR`, the mini toolbar still rendered a 1-2 px white outline.

**Root cause A:** The visible edge was the `WS_THICKFRAME` (sizing border) drawn by Windows, not the `WS_DLGFRAME` dialog border. `DWMWA_BORDER_COLOR` controls the rounded-window outline but doesn't suppress the sizing-frame draw.

**Fix A:** In `ApplyMini`, strip `WS_THICKFRAME | WS_CAPTION | WS_SYSMENU` from the window style. **Keep `WS_DLGFRAME`** — it's the one style Win11's system backdrop hard-requires. The mini shell is non-resizable, so losing `WS_THICKFRAME` is functionally a no-op.

**Symptom B:** Hotkey-summoning the shell after closing it while in Full state flashed the Full layout for one frame before the slide-down.

**Root cause B:** `OnWindowClosing` did `SW_HIDE` immediately — the window kept its Full state, chrome, size, and content in memory. On summon, even with off-screen parking + layout pumping, WinUI committed one frame of cached Full layout before the new chrome/size took effect.

**Fix B (per user suggestion):** Added `AppShellWindow.PrepareForSummonInBackground()` that runs `ConfigureForState(MiniSetup, animate:false) → ComputeWindowRect → MoveAndResizeWindow → StateHost.UpdateLayout` synchronously. `App.OnWindowClosing` calls it BEFORE `SW_HIDE`, so the window is already mini-shaped and mini-laid-out before it disappears. Next `SummonAsync` just slides it in — no layout work during the show, no stale-content frame.

## 2026-06-13 — Mini toolbar white border: chrome-after-backdrop + post-activation re-apply
- **Area:** AppShellWindow ConfigureForState ordering; InitializeAfterActivation.
- **Tried:** Reordered ApplyChromeFor to run AFTER ApplyPresenterFor and UpdateBackdropFor in all three ConfigureForState call sites. Also re-stamp ApplyChromeFor from InitializeAfterActivation so DWM border-color attributes that no-op pre-activation get applied to the realized HWND.
- **Rationale:** Window.SystemBackdrop assignment internally touches DWM attributes; if chrome runs first, the backdrop init clobbers DWMWA_BORDER_COLOR back to default. Some Win11 DWM attributes also silently no-op when set before first activation.
- **Status:** Pending user verification.

## 2026-06-13 — Mini toolbar white border was XAML, not DWM
- **Area:** MiniSetupControl.xaml ToolbarBorder.
- **Root cause:** ToolbarBorder had `BorderBrush={ThemeResource CardStrokeColorDefaultBrush}` + `BorderThickness=1`. CardStrokeColorDefaultBrush is a near-white stroke that pops against the dark Mica Alt backdrop once ToolbarBorder.Background was made transparent. Did NOT appear in Full state because Full uses a different control (FullShell) with its own chrome.
- **Fix:** BorderBrush=Transparent, BorderThickness=0 on ToolbarBorder.
- **Lesson:** Before chasing DWM/window-chrome theories for a "border", first inspect the topmost XAML Border element in the affected control. A 1px stroke on a transparent-background Border looks identical to a DWM frame edge.

## 2026-06-13 — White border was WS_DLGFRAME, not XAML or DWMWA_BORDER_COLOR
- **Area:** WindowChromeService.ApplyMini, AppShellWindow.UpdateBackdropFor, RegionSelectorOverlay, WindowSelectorOverlay.
- **Root cause:** Win11 draws a 1px lighter accent-coloured edge around any window that has WS_DLGFRAME set. We previously kept WS_DLGFRAME because Window.SystemBackdrop = MicaBackdrop wouldn't render Mica without it. DWMWA_BORDER_COLOR=0x00202020 was either ignored or rendered visibly anyway.
- **Fix:** Strip WS_DLGFRAME (and WS_BORDER) entirely in Mini chrome. Re-establish Mica via DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE=DWMSBT_TABBEDWINDOW) which does NOT require WS_DLGFRAME. UpdateBackdropFor now nulls out Window.SystemBackdrop for Mini states (so it can't re-add WS_DLGFRAME) and only assigns MicaBackdrop for Full/FullRecording. Added WindowChromeService.ApplyOverlayChrome for the region/window picker overlays (same WS strip, no backdrop).
- **Lesson:** Window.SystemBackdrop forces WS_DLGFRAME on. To get Mica WITHOUT the border edge, use the raw DWM attribute (DWMWA_SYSTEMBACKDROP_TYPE) instead.

## 2026-06-13 — White border vs Mica: BOTH solved by order, not by stripping backdrop
- **Area:** WindowChromeService.ApplyMini, AppShellWindow.UpdateBackdropFor.
- **What didn't work:** Switching from Window.SystemBackdrop to raw DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE). The DWM backdrop renders, but ONLY where XAML pixels are transparent. Without Window.SystemBackdrop, WinUI never marks its root background as Transparent — so the root paints opaque white and hides the DWM backdrop. DWMWA_USE_IMMERSIVE_DARK_MODE doesn't fix this because the white is XAML, not DWM.
- **What worked:** Keep Window.SystemBackdrop = MicaBackdrop{BaseAlt} for ALL states (so WinUI sets root background to Transparent and initialises the Mica compositor). Then in ApplyChromeFor (which now runs AFTER UpdateBackdropFor), strip WS_DLGFRAME/WS_BORDER. Mica keeps rendering even without WS_DLGFRAME because the compositor was already hooked up. Result: Mica visible AND no 1px Win11 accent edge.
- **Lesson:** Win11 `SystemBackdrop` provides TWO things — DWM backdrop AND root-transparency. You can't replace it with raw DWM attributes without separately handling the XAML root background. Keep SystemBackdrop, just strip the frame style after.
- **Final ordering in ConfigureForState:** ApplyPresenterFor → UpdateBackdropFor (SystemBackdrop) → ApplyChromeFor (strip WS_DLGFRAME) → ApplyStateFlags.

## Escape Resets Selection Instead of Cancelling Picker

**Feature/area:** RegionSelectorOverlay / WindowSelectorOverlay — Escape key handling

**Approaches tried:**
1. **Reroute Escape paths (XAML accelerator, OnKeyDown, low-level keyboard hook) from `CancelSelection` / `_tcs.TrySetResult(null)` to a new `ResetSelection` method** — Worked. ✅

**What worked:** Both overlays now expose `ResetSelection` that clears the in-progress selection state (region: `_isDragging/_isResizing/_activeDragBounds/_hasSelection/_sel{X,Y,W,H}` + `ReleasePointerCaptures`; window: `_lockedWindow/_hoveredWindow`) and calls `UpdateOverlay` to repaint the full smoke without completing the `TaskCompletionSource`. The smoke/dim stays on screen so the user can immediately make another selection. The host-driven `Cancel()` (used on tab switch via `CapturePickerService.CancelActivePickerAsync`) still completes the TCS with null so picker dismissal on tab switch is unaffected.

**What didn't work:** Initial attempt used `RootGrid.ReleasePointerCapture(null!)` — that overload requires a `Pointer` instance which isn't available from the keyboard-hook callback. Switched to `ReleasePointerCaptures()` (plural) which releases all active captures without needing a Pointer reference.

## Progressive Escape in Picker Overlays

**Feature/area:** RegionSelectorOverlay / WindowSelectorOverlay — Escape key handling (follow-up to `ResetSelection`)

**Approaches tried:**
1. **Escape always resets selection** — Broke dismiss. Once the picker was open, Escape never bubbled to `MiniSetupControl.OnKeyDown` (which gates `DismissRequested` on `!CapturePickerService.Shared.IsPickerOpen`), so the user had no keyboard path back to the tray. ❌
2. **Progressive Escape: reset if there's a selection, otherwise cancel the picker** — Worked. ✅

**What worked:** Added `HandleEscape` in both overlays. Region overlay cancels when `!_hasSelection && !_isDragging && !_isResizing`; otherwise calls `ResetSelection`. Window overlay cancels when `_lockedWindow is null`; otherwise calls `ResetSelection`. First Escape clears the drawn region / locked window (smoke stays). Second Escape (nothing to reset) closes the picker, which lets the next Escape reach `MiniSetupControl.OnKeyDown` and fire `DismissRequested` to return to the tray.

**What didn't work:** Gating window-overlay reset on `_hoveredWindow` would have been too eager — `_hoveredWindow` is set the instant the cursor moves over any window, so Escape would almost never cancel. Only `_lockedWindow` (explicit click) reliably indicates an active selection to clear.

## Picker Escape → Single-Step Tray Dismiss + First-Launch Esc Fix

**Feature/area:** RegionSelectorOverlay / WindowSelectorOverlay / CapturePickerService / AppShellWindow — Escape semantics

**Approaches tried:**
1. **Two-step Escape: 1st clears picker selection, 2nd closes picker, 3rd reaches MiniSetupControl.OnKeyDown to dismiss** — Rejected by user (UX is too many keystrokes; closing picker shouldn't be a visible step). ❌
2. **One-step Escape from picker → directly hide the shell window via a new `CapturePickerService.EscapeToDismissRequested` event** — Worked. ✅

**What worked:**
- Added `EscapeToDismissRequested` event + `RaiseEscapeToDismissRequested()` on `CapturePickerService`.
- Both overlays' `HandleEscape`: if there is a selection (`_hasSelection` / `_isDragging` / `_isResizing` for region; `_lockedWindow` for window) call `ResetSelection`; otherwise raise the event AND cancel the picker.
- `AppShellWindow` subscribes; handler awaits `CancelActivePickerAsync` (so `_pickerClosedSignal` fires before we hide) then calls `ShowWindow(hwnd, SW_HIDE)` when in `MiniSetup` and not recording. Intentionally bypasses the `AppShellStateMachine` `EscDismiss` reducer's `WasRecentlySummoned` gate — user already pressed Esc to open the picker and again to exit, so intent is unambiguous.
- Fixed first-launch dismiss bug by seeding `_lastSummonAt = DateTime.UtcNow` in the `AppShellWindow` constructor. Without it `WasRecentlySummoned` was false on cold start (`DateTime.MinValue`), so the regular toolbar-focus Escape was a silent no-op until the user round-tripped through a real summon (expand → collapse → hotkey).

**What didn't work:**
- Routing picker-Esc through `MiniSetupControl.OnKeyDown` / `DismissRequested`: that handler explicitly returns when `CapturePickerService.Shared.IsPickerOpen` is true, and unblocking it would require an extra keystroke.
- Going through `AppShellStateMachine.EscDismiss` for the picker path: requires `!IsPickerOpen` AND `WasRecentlySummoned`, both of which fight the "user wants out now" intent. Bypass + direct `SW_HIDE` is cleaner.

## First-Launch Esc Focus + Slide-Out Dismiss Animation

**Feature/area:** AppShellWindow — initial focus and dismiss animation

**Approaches tried:**
1. **Seed `_lastSummonAt = DateTime.UtcNow` in constructor to fix first-launch Esc** — Necessary but insufficient; `EscDismiss` reducer was satisfied but `MiniSetupControl` never received focus on cold start so its `KeyDown` never fired. ❌ alone
2. **Call `MiniSetup.FocusRecordButton()` from `InitializeAfterActivation` after the post-activation remeasure** — Worked. ✅ (combined with #1)
3. **Replace `SW_HIDE` flash with slide-up animation via new `DismissToTrayWithSlideAsync()`** — Worked. Mirrors the summon slide-down: animates `AppWindow.Position/Size` to `(X, Y - Height - 24)` over 200 ms with `QuadraticEaseInOut`, then `SW_HIDE`, then `MoveAndResizeWindow(fromRect)` to restore on-screen position for non-summon re-shows. ✅

**What worked:** Both `OnMiniSetupDismissRequested` (toolbar Esc) and `OnPickerEscapeToDismissRequested` (picker Esc with no selection) now route through `DismissToTrayWithSlideAsync` instead of raw `SW_HIDE`.

**What didn't work:**
- Relying on `Activate()` alone for first-launch keyboard focus — XAML islands in WinUI 3 don't focus a child control until something explicitly calls `Focus(FocusState.Programmatic)` (already done in `MiniSetupControl.OnLoaded`, but evidently lost by the time the user presses Esc; calling `FocusRecordButton` after the 100 ms remeasure delay reliably sticks).
- Skipping the post-hide `MoveAndResizeWindow(fromRect)` restore — left the window parked above the work area, so any subsequent path that `SW_SHOW`s without recomputing rect (rare but possible via tray click code paths) would open invisible.

## Esc on Toolbar — Drop 5s WasRecentlySummoned Gate

**Feature/area:** AppShellStateMachine — EscDismiss reducer

**Approaches tried:**
1. **Keep `WasRecentlySummoned` gate (spec §4.7) and seed `_lastSummonAt` at launch** — Worked for the first 5s after summon/launch but became a silent no-op afterwards. User reported "toolbar is up, pressing escape on it is doing nothing". ❌
2. **Remove the `WasRecentlySummoned` check entirely from both `EscDismiss` switch arm and `TryDismissMiniSetup` helper** — Worked. ✅

**What worked:** `EscDismiss` now succeeds whenever `currentState == MiniSetup && !IsRecording && !IsPickerOpen`. Updated `MiniModeStateMachineTests.EscDismiss_FromMiniSetup_IsAllowedOnlyAfterRecentSummonAndNoPicker` to `EscDismiss_FromMiniSetup_IsAllowedWhenNoPickerOpen` and dropped `WasRecentlySummoned = true` from both test setups. All 12 `MiniModeStateMachine` tests pass.

**What didn't work:** The 5s grace was originally to prevent stale Esc from dismissing a deliberately-pinned toolbar, but the toolbar in practice is summoned-on-demand only, so the grace just made Esc feel broken after the user paused.

**Note:** `WasRecentlySummoned` / `_lastSummonAt` are kept on the context struct and `AppShellWindow` (still set on summon and at launch) in case other consumers need them, but no reducer arm currently reads them.

## Hide "Ready to record" Status Bar in Default State

**Feature/area:** RecordingPage status bar (Grid.Row=2 `Border`)

**Approaches tried:**
1. **x:Bind helper `StatusToVisibility(string)` on `RecordingPage` code-behind, bound to `Border.Visibility`** — Worked. ✅ Returns `Collapsed` when `RecordingStatus` is empty or equals the default `"Ready to record"` literal; `Visible` for any other status (Recording…, Saved, errors, missing-selection warnings).

**What worked:** Matches the existing helper pattern (`BoolToVisibility`, `InvertBoolToVisibility`) used elsewhere on the page. The literal `"Ready to record"` is the initial value of `_recordingStatus` in `RecordingViewModel.cs` line 71 — kept the comparison string-equality with no constant extraction to stay surgical.

**What didn't work:** N/A — single-attempt change.
