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
