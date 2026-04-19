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

**What didn't work:** N/A (first approach succeeded).

**Key design decisions:**
- Auto-generated (non-manual) zoom segments from clicks are visible but not editable — they don't match AutoZoomEngine's merged segments exactly.
- Left edge resize changes PreDuration+Timestamp; right edge changes HoldDuration.
- `ZoomKeyframe.MinSegmentDuration` = 200ms prevents degenerate segments.
- Build requires VS MSBuild (`dotnet build` fails due to PriGen tooling issue).
