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
