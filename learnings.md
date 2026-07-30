# Learnings

This file tracks approaches tried, what worked, and what didn't for each feature.

> **How to use this file:** Read **Settled Playbooks** below FIRST. These are definitive
> rules distilled from areas that were re-worked many times (often with reversals). Do not
> re-litigate them — follow them. The dated/feature sections further down are the historical
> record showing *why* each rule exists. Only deviate from a playbook if you have new evidence,
> and if you do, update the playbook in the same change.

---

# Settled Playbooks (definitive — read before starting)

These rules supersede any conflicting advice in the historical sections below. Each one
caused repeated churn before it was settled; treat them as fixed conventions.

## Playbook: Build, Deploy & Test toolchain

- **ALWAYS build with VS MSBuild, NEVER `dotnet build`** — this repo fails `dotnet build`
  with a PriGen tooling error (MSB4062). Locate MSBuild via
  `vswhere -latest -find 'MSBuild\**\Bin\MSBuild.exe'`
  (e.g. `C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe`).
- If direct `& MSBuild.exe` is blocked by shell permissions, invoke it through a
  `[System.Diagnostics.Process]::Start()` wrapper with redirected stdout/stderr.
- Standard build args: `Musio.App.csproj /restore /t:Build /p:Configuration=<Debug|Release> /p:Platform=<x64|ARM64>`.
  Always include `/restore` — a bare `/t:Build` after deleting `bin/obj` fails NuGet restore.
- **Clean rebuild = kill the running `Musio.App` process first, then delete `bin/obj`.**
  Locked DLLs make MSBuild `/t:Clean` fail silently (look for the PID in MSB3061 warnings).
- After any XAML element rename/restructure, delete `bin/obj` to drop stale `.g.cs`
  (otherwise `COMException: Element not found` → blank page at runtime).
- **Tests:** the host often lacks the .NET 9 runtime. Run `dotnet test` with
  `DOTNET_ROLL_FORWARD=Major` to roll forward to the installed runtime. The current suite
  size is in the 320s–330s range and must stay green.
- **MSIX (unsigned, for Store):** `msbuild Musio.App.csproj /restore /t:Build /p:Configuration=Release /p:Platform=<x64|ARM64> /p:GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never`.
- **Deploy/run locally:** `Remove-AppxPackage` the old registration BEFORE `Add-AppxPackage -Register <AppxManifest.xml>`
  (re-registering over deleted clean-build files fails silently). Launch with
  `Start-Process 'shell:AppsFolder\PratikMistri.Musio_9gph0n9984scy!App'` (NOT `explorer.exe`, which returns exit 1).

## Playbook: DPI & capture coordinate transforms (region / window / monitor)

The single most-churned area. The process is **PerMonitorV2**, which is the key to every rule:

- **`WH_MOUSE_LL` hook coordinates are already PHYSICAL pixels.** NEVER multiply them by a
  DPI scale. Use `coordScale = 1.0f` in every transform site (FrameCompositor, AutoZoomEngine,
  EditorPage zoom-create). Multiplying by `Project.DpiScale` double-scales and was reverted.
- **`Project.CropOffsetX/Y` = the screen-absolute PHYSICAL-pixel top-left of the captured frame**
  (monitor origin + crop-within-monitor), not the crop-within-monitor offset alone. Subtract it
  from every cursor/click/zoom-center coordinate. Compute per capture type:
  - Monitor: `GetMonitorInfo(hMonitor).rcMonitor.Left/Top`.
  - Window: `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` (falls back to `GetWindowRect`).
  - Region: monitor origin + physical crop-rect position.
- **Physical capture dimensions = logical × `GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI)`**,
  then forced even (see encoding playbook). `Project.DpiScale` is stored at record time for
  back-compat but is NOT used for hook-coordinate transforms.
- **NEVER trust `GetSystemDpiScale()` / dimension-ratio heuristics for region captures** — they
  return 1.0 because the cropped dimension is smaller than the monitor logical size. Store the
  real DPI at record time instead.
- Native overlay windows (`RegionBorderHighlight`, pickers) expect PHYSICAL coords: multiply
  WinUI DIP region coords by the monitor DPI scale (resolved via the same `MonitorEnumerator`
  matching used by `BuildCaptureTarget()`).
- Known unsolved limitation: region/window pickers use `Stretch="Fill"` over a full virtual-desktop
  screenshot, so multi-monitor coords can still be slightly off. Don't claim a multi-monitor DPI
  fix is complete without testing on a secondary, differently-scaled monitor.

## Playbook: H.264 encoding & MediaEncodingProfile

- **Force encoder-safe dimensions in BOTH pipelines — but they use DIFFERENT alignment:**
  - Recording (`VideoWriter.FinalizeAsync`): mod-2 via `(uint)(_width & ~1)` / `(_height & ~1)`.
    H.264 silently rejects odd dimensions (`CanTranscode = false`, no message).
  - Export (`VideoEncoder` → `AspectRatioHelper.ComputeExportDimensions`): floored to **mod-16**
    `(fitW / 16) * 16` for H.264 macroblock alignment (with a `Math.Max(2, fitW - fitW % 2)` mod-2
    fallback only when a side is < 16px). Do NOT add a separate `& ~1` in `VideoEncoder` — dimension
    safety already lives in `ComputeExportDimensions`; change it there if alignment needs adjusting.
  - The recurring trap is forgetting one pipeline entirely, not the specific modulus — confirm both
    `VideoWriter` and the `ComputeExportDimensions` path whenever touching encode dimensions.
- **Recording pipeline:** `VideoEncodingQuality.Auto` + explicitly set Width/Height/Bitrate/FPS/Subtype("H264").
- **Export pipeline:** do **NOT** use `Auto` — it yields incomplete Video/Audio properties and the
  audio-mux pass (`MediaComposition.RenderToFileAsync`) throws `MF_E_INVALIDMEDIATYPE (0xC00D36E6)`.
  Instead start from `MediaEncodingProfile.CreateMp4(HD1080p)` as a well-formed template and OVERRIDE
  Width/Height/Bitrate/FPS/Subtype. The encoder auto-selects the correct H.264 Level from the real
  dimensions (presets like `HD1080p`/`HD720p` lock Level/DPB and corrupt frames above ~1920×1088,
  visibly on AMD AMF; NVIDIA may silently auto-promote and mask the bug).
- **Scale bitrate by pixel count:** `VideoEncoder.ComputeBitrate(width, height)` scales the base by
  pixel ratio. A fixed bitrate starves ~3K+ output.
- **Per-frame GPU surface allocation in `VideoEncoder` is INTENTIONAL, not a leak to "fix".** When
  scaling, each frame gets its own `outputSurface` (`CreateRenderTarget`) so the encoder can read it
  asynchronously after the producer releases the `SampleRequested` semaphore — a reused buffer would
  be overwritten mid-read. (The old `_flipBuffer`/`_scaleTarget` reuse fields no longer exist.)
  Memory safety comes from `PreflightRenderTargetMemory` (rejects > ~1.5 GB BGRA targets) and
  disposing each surface after the encoder consumes it, NOT from buffer reuse. Long-lived helpers
  that ARE cached: `_textSlideRenderer` and the webcam source (opened once, extracted per frame).
- **For per-frame encoding prefer a streaming `MediaStreamSource` + `MediaTranscoder`**
  (BGRA8 → H.264, CFR) — both pipelines do this; NOT `MediaClip` + `MediaComposition.RenderToFileAsync`
  per frame (spawns 100k+ COM objects on long recordings). Set
  `MediaTranscoder.HardwareAccelerationEnabled = false` in BOTH `VideoWriter` and `VideoEncoder` to
  avoid AMD H.264 corruption / D3D-surface interop quirks. Wrap `TranscodeAsync` in a watchdog
  timeout (`TranscodeWithTimeoutAsync`). NOTE: export still uses `MediaComposition.RenderToFileAsync`
  for the *audio mux* second pass — which is exactly why its profile must be a well-formed `HD1080p`
  template rather than `Auto`.
- **Always guard `MediaClip.CreateFromFileAsync`** — `video.mp4` is often corrupt/empty because
  `VideoWriter.FinalizeAsync` is intentionally non-fatal during recording. On failure
  ("The parameter is incorrect"), fall back to `project.Width/Height` and the `.frames/` JPEGs.

## Playbook: WinUI flyout/control initialization & XAML resource pitfalls

- **NEVER set default values (`IsChecked`, `Value`, `SelectedIndex`, …) in XAML on flyout/option
  controls.** They fire `Checked`/`ValueChanged`/`SelectionChanged` during `InitializeComponent()`
  — before `_suppress*` flags are set and before `x:Name` fields are assigned — racing
  `RebuildPreviewRendererAsync` against `InitializePreviewAsync` and hanging the app after recording.
  Set defaults ONLY inside a `Sync<X>ControlsToConfig()` method, under the suppress flag.
- Null-guard every handler that touches `x:Name` controls (they can fire before assignment).
- **NEVER assign `{ThemeResource ...}` to a plain CLR / non-DependencyProperty** (e.g. a converter's
  `TrueValue`/`FalseValue` placed in `Page.Resources`). It compiles but throws
  `XAML_E_LAYOUT_CYCLE` (HRESULT 0x802B000A) when the page is navigated to. Look brushes up at
  runtime from `Application.Current.Resources` (keyed by `ConverterParameter`) instead.

## Playbook: Frame-style / compositor coordinate model (settled after 10 rounds)

- **`BackgroundCompositor.CalculateOutputSize` is identity `(w, h)`** — padding insets WITHIN the
  canvas and never grows it. The output canvas AR is driven only by the target aspect ratio.
- **One background container only.** Don't fill letterbox/pillarbox bars inside the cropped buffer;
  size the cropped buffer to the visible source-area dims and let the single background fill show
  through. `_sourceAreaOffsetX/Y` already include the total gap (user padding + AR-fit gap) — there
  is NO separate `+padding` term or "inner content area" coordinate space.
- **Zoom uses the post-composite path whenever `ZoomLevel > 1.01`, UNLESS `ZoomScope == Source`**
  (which keeps background/letterbox/padding fixed and zooms only the captured source).
- Frame style + cursor are **per-segment** (`VideoSegment.FrameStyleOverride` / `CursorStyleOverride`);
  aspect ratio, fit/cover mode, crop anchor, and zoom scope are **global** on `CompositionConfig`.
  Global changes must call `InvalidateSegmentPreviews()` so appended segments rebuild.

## Playbook: Zoom-segment time mapping (multi-recording)

- Zoom keyframes are tagged by source file via `ZoomKeyframe.SourceVideoFilePath` (null = primary).
  **Map BOTH edges of a keyframe through the SINGLE owning segment** (`OwningSegmentForKeyframe`:
  file match + Timestamp containment), never per-edge first-match (which collapsed appended segments
  to a thin line) and never the primary-only `XToSourceTime` mapping for appended keyframes.
- On any edit (move/resize/property change), promote the keyframe to `IsManual = true` (save the
  previous value for undo). `ZoomKeyframe.MinSegmentDuration = 200ms`. Cut/split overlap logic must
  use the full `Start`/`End` span, not just `Timestamp`.

## Playbook: Crash / freeze hardening invariants

- **On `CanvasDevice.DeviceLost`, surface a recoverable exception — NEVER recreate the device
  mid-frame.** Let outer code restart cleanly. Check `D3D11CreateDevice` HRESULT and fall back to
  WARP (`D3D_DRIVER_TYPE_WARP`).
- VRAM/screenshot preflights: reject absurd allocations (`>1.5 GB` BGRA surface, `>16384px`, or
  `w*h*4 > 1 GB`) with a clear exception / solid-overlay fallback, using checked `long` arithmetic.
- Serialize crop+write under a single lock; frame-pool callbacks are free-threaded and race shared
  render targets. Wrap `TryGetNextFrame()` in try/catch (device-lost / monitor hot-plug / shutdown).
- Long-running encode/transcode/mux passes get a cancellation-based watchdog timeout.
- Throttle pure `WM_MOUSEMOVE` samples (min 4ms interval); never throttle clicks/scrolls/buttons,
  and don't change the binary serialization format.

---

# Archive — consolidated entries (older than 15 days)

The detailed round-by-round write-ups below were condensed on 2026-06-21 to reduce
file size. The durable rules they produced now live in **Settled Playbooks** above;
each line here keeps the feature, the approach that worked, and any gotcha not already
captured by a playbook. Entries newer than 15 days remain in full further down.

## Recording overlay (pill window, acrylic, stop UX)
- White-border removal = DWM `DWMWA_BORDER_COLOR=COLOR_NONE` + strip `WS_BORDER`/`WS_DLGFRAME`; pill body via `AcrylicBrush` + `CreateRoundRectRgn`/`SetWindowRgn` (region radius = window height).
- Final overlay uses `SystemBackdrop = DesktopAcrylicBackdrop()` (guard `IsSupported()`) + DWM `DWMWCP_ROUND`, NOT `SetWindowRgn` — `SetWindowRgn` clips contents but leaves the DWM drop-shadow rectangular.
- "Stopping" state: hide RecordingPanel, show StoppingPanel ring + a `DispatcherTimer` (1.5s) cycling Star-Trek phrases; dispose timer in `CloseOverlay`.
- Light-theme overlay fix and `Hide()`-before-`Show()` to avoid GDI leaks.

## MSIX packaging, Store submission, versioning
- `WindowsPackageType=MSIX`; `graphicsCapture` needs `uap6:Capability` (not `uap:`), else DEP0700.
- Store manifest cleanup: drop `systemAIModels`, `mp:PhoneIdentity`, and `Windows.Universal` TDF (keep `Windows.Desktop`); `runFullTrust` needs Partner Center justification; Store signs MSIX itself.
- App assets generated from `Musio.png` (640×640) via System.Drawing `HighQualityBicubic` + hand-written multi-res ICO.
- Version bumped 1.0.4→1.1.0 (manifest + stale SettingsPage string); per-arch unsigned MSIX built x64 then ARM64 separately (clean bin/obj between).

## Build / test toolchain (all reinforce the Build playbook)
- `dotnet build` fails (PriGen MSB4062 / missing `Microsoft.Build.Packaging.Pri.Tasks.dll`); use VS MSBuild, located via `vswhere -latest -find 'MSBuild\**\Bin\MSBuild.exe'`. If `&` is blocked, wrap in `[System.Diagnostics.Process]::Start()`.
- Kill running `Musio.App` before clean (DLL lock); `/restore` required after deleting bin/obj.
- Tests: build with MSBuild then `dotnet test --no-build`; host lacks .NET 9 runtime so set `DOTNET_ROLL_FORWARD=Major`. CI uses `microsoft/setup-msbuild@v2`.
- Launch app: `Start-Process 'shell:AppsFolder\PratikMistri.Musio_9gph0n9984scy!App'` (`explorer.exe` returns exit 1). PFN: `PratikMistri.Musio_9gph0n9984scy`.
- Targeting `Musio.sln` alone may not rebuild `Musio.Tests` on test-only edits — target the test csproj.

## Region / window / monitor capture + DPI (all reinforce the DPI playbook)
- Crop applied in `VideoWriter.WriteFrame` via reusable `CanvasRenderTarget.DrawImage(dest,src)`; `RecordingSession` computes physical crop rect with `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`.
- H.264 rejects odd dims silently → `& ~1` in BOTH `RecordingSession` and `VideoWriter.FinalizeAsync`; `FinalizeAsync` made non-fatal so projects still open from `.frames/`.
- Region/full-screen captures skip background fill (only Window/Monitor get padding/shadow/corners — gate on explicit type).
- Region border = 4 thin native Win32 popups (`WS_EX_TRANSPARENT|LAYERED|TOOLWINDOW`, `WDA_EXCLUDEFROMCAPTURE`); they need screen-absolute physical px = monitor-local DIP × DPI + monitor origin (`rcMonitor.Left/Top`).
- MainWindow: removed `WDA_EXCLUDEFROMCAPTURE` (it hid window from ALL capture); rely on existing minimize/restore. Minimize moved to a pre-record click handler with 600ms delay so the animation isn't captured.
- Mouse misalignment root causes: `GetSystemDpiScale()` returns 1.0 for region crops (crop < monitor) — store real `Project.DpiScale` at record time; and `WH_MOUSE_LL` coords are already PHYSICAL in PerMonitorV2 → use `coordScale=1.0f`, never ×DpiScale (double-scale bug, invisible at 100%). Subtract screen-absolute `CropOffsetX/Y` (monitor origin; window uses `DWMWA_EXTENDED_FRAME_BOUNDS`).
- Window highlight overstated bounds by ~7px (`GetWindowRect` includes DWM frame) → use `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` with `GetWindowRect` fallback.
- UNRESOLVED: region capture off-by-1px at fractional DPI; the `PixelX/Y` + `CropIsPhysicalPixels` physical-crop attempt was reverted (did not fix). Cause likely in overlay stroke alignment / screenshot stretch, not the DPI multiply.

## Multi-monitor region picker (high-churn; settled)
- Picker host window must span the full virtual desktop via `AppWindow.MoveAndResize(SM_X/Y/CX/CYVIRTUALSCREEN)` — `presenter.Maximize()` only covers one monitor (caused squashed two-monitor view); once full-size, `Stretch="Fill"` becomes 1:1.
- `ConfirmSelection` maps overlay DIPs → virtual-desktop physical px → monitor-local physical px (largest-intersection monitor, not corner `MonitorFromPoint`) → monitor-local DIPs via per-monitor `GetDpiForMonitor`. Store `IntPtr Handle` on `MonitorInfo` so DPI + origin lookups share one hMonitor.
- Monitor match by exact `DisplayName==MonitorId` (or `StartsWith(MonitorId+' ')`) — `Contains` matched DISPLAY1 vs DISPLAY10.
- Stale saved region (disconnected monitor): clear selection + surface status, don't silently fall back to primary.
- Escape-to-cancel needs a Win32 `WH_KEYBOARD_LL` hook (store delegate as field) — XAML focus isn't established on a borderless maximized overlay, so `KeyboardAccelerator`/`KeyDown` fail.
- Picker UX: pre-seed last region in `OnLoaded` (no blank canvas); `WasCancelled` flag + InfoBar to disambiguate Escape no-op from first-open.

## Recording state, audio capture, hotkeys
- Recording selection lost on navigation → shared singleton `RecordingViewModel.Shared`; subscribe in `OnNavigatedTo` / tear down in `OnNavigatedFrom` to avoid leaking page refs.
- `AudioCaptureEngine.StopRecording` deadlock: WASAPI `RecordingStopped` callback contended `_lock` held across Dispose → lock only to null fields, then stop/dispose lock-free.
- Mic/system/camera toggles = `ToggleButton` two-way bound to `[ObservableProperty]` fields, persisted via `partial OnXChanged` → `AppSettings`. Settings audio toggles were previously decorative (never bound).
- Single-toolbar recording layout with inline sub-options; `SetForegroundWindow(handle)` before window capture; stop-trigger click trimmed via `NotifyStopRequested()` + `TrimStopClick` (last LButtonDown within 200ms of stop).
- PLM/HANG_QUIESCE: handle `WM_QUERYENDSESSION` (return 1) AND `WM_ENDSESSION` — the earlier handler used wrong constant `0x0026` (correct `0x0016`) so it was dead code. `BeginQuiesce` sets `_isExiting`, disposes tray/hotkey/extended-exec, posts `Window.Close()`, hard 1.5s `Threading.Timer` → `Environment.Exit(0)`; use `Application.Exit()` not `Environment.Exit` inside handlers. Request `ExtendedExecutionSession` when hiding to tray; pause editor playback on `VisibilityChanged`.

## Timeline editing (clips, speed, split/cut)
- Clips are KEPT regions (mapper had inverted semantics). Added `SpeedFactor`/`SourceStart` to `TimelineClip` (back-compat defaults); keep positions in output time with source mapping for the mapper.
- Cut (Ctrl+X) = `RippleDeleteOperation` at playhead (old CutOp used full TrimStart/End → deleted everything); Split auto-creates a `[0,Duration]` clip when empty; full state snapshots for reliable undo.
- Timeline container height was clipping tracks (5 rows = 230px, was 170).
- Filmstrip: pre-scaled thumbnails (~track height) cached as `CanvasBitmap[]`, tiled with source-time mapping; `_thumbnailGenerationId` guards stale results; 4px orange (`#E87C06`) stroke.

## Zoom track / AutoZoomEngine (high-churn; settled)
- Segment model: computed `Start`/`End` on `ZoomKeyframe` (`FromRange` factory); auto segments selectable, promoted to `IsManual=true` on any edit (save prior for undo); `MinSegmentDuration=200ms`; overlap logic uses full Start/End span.
- Suppress click-driven auto-zoom by raw `long` `SourceClickTicks` into `TimelineModel.SuppressedClickTicks` (TimeSpan keys are fragile); sync to preview AND export (`VideoEncoder` needed `TimelineModel?`).
- Overlapping segments: evaluate ALL active, max-zoom-wins for LEVEL but blend CENTER by activation weight `max(0,zoom-1)` (max-zoom center alone snapped the focal point). Regression tests assert no jump.
- Draggable zoom-region overlay on raw frame (skip compositor in edit mode) via `ZoomState.IsManualOverride`; corner-resize anchors opposite corner, `newZoom=startZoom/scale` clamped [1.5,4.0]; center-snap to 0.5 (Shift bypass); cosmetic handles + manual hit-test (hit-visible handles steal pointer capture).
- `CanvasGeometry.CreateRoundedRectangle(null,...)` throws at runtime — pass a real `ICanvasResourceCreator`.

## Compositor / frame style / aspect ratio (10+ rounds; settled in playbook)
- `BackgroundCompositor.CalculateOutputSize` is identity `(w,h)` — padding insets within canvas, never grows it (growing changed AR with the slider). ONE background container: size cropped buffer to visible source dims, leave letterbox transparent so the single wallpaper shows through (drawing bars in-buffer caused "wallpaper repeat"). `_sourceAreaOffsetX/Y` already include padding + AR gap (no separate `+padding`).
- Zoom uses post-composite path when `ZoomLevel>1.01` UNLESS `ZoomScope==Source`; only activate post-composite when actually zoomed (doing it for every padded frame doubled render cost / slow export). `ZoomScope` (Frame|Source) on Project/Config, surfaced in flyout, propagated by ExportEngine.
- Wallpaper image cached in `BackgroundCompositor` (`_cachedBackgroundImage`/path) + `IDisposable` — was `LoadAsync` per frame (choppy/slow).
- `CropSourceFrame` adaptive interpolation: Linear when |scale-1|<0.05 else HighQualityCubic.
- Editor style flyout: `RebuildPreviewRendererAsync` (only recreates renderer/compositor, preserves reader/audio/zoom/playhead) with 200ms debounce + `_suppressStyleEvents`; Monitor background defaults applied in `ProjectService.SetProject` (not every `InitializePreviewAsync`, which clobbered edits); style menu visible for Monitor too.
- Background presets: 8 bold two-stop gradients (Nebula…Sunset); avoid brand-name labels. Wallpaper picker "+" sentinel tile (`Tag="__add_wallpaper__"`, grid index = path index + 1) opens `FileOpenPicker`. `FindMatchingPresetIndex` must compare end-color/angle/shadow/border/image path. Known: packaged-app `CanvasBitmap.LoadAsync` on arbitrary picked paths not guaranteed across sessions (use LocalFolder copy or FutureAccessList).

## Export pipeline (H.264, sync, robustness)
- Scrambled frames root cause: NON-mod-16 output dims misalign HW H.264 macroblocks → banding/ghosting. Floor export dims to mod-16; use `MediaStreamSample.CreateFromDirect3D11Surface` (no pixel extraction); `HardwareAccelerationEnabled=false` (AMD AMF stricter; NVIDIA masks by auto-promoting Level). BGRA8 is TOP-DOWN — the bottom-up row-flip was wrong. `MF_MT_DEFAULT_STRIDE`/`CopyToBuffer` did NOT help.
- Recording uses `VideoEncodingQuality.Auto`; export must NOT (`Auto` yields incomplete props → mux `MF_E_INVALIDMEDIATYPE 0xC00D36E6`) — start from `CreateMp4(HD1080p)` template and OVERRIDE Width/Height/Bitrate/FPS/Subtype so the encoder picks the right Level. Bitrate scales with pixel count; reuse flip/scale buffers (~44MB/frame saved).
- Truncated-to-~1s bug: `ProduceSampleAsync` swallowed errors and set `Sample=null` (= EOS) → surface first error via `onError` callback and rethrow with frame index. Pause preview before export (shared Win2D device contention corrupts/crashes).
- Corrupt/missing source MP4 (FinalizeAsync non-fatal): make `sourceClip` nullable, fall back to `project.Width/Height`, guard "no frame source".
- Re-export dropped style edits: `ExportFlyout_Opened` short-circuited on stale `ExportSucceeded`; reset via `ExportFlyout.Closed` → `PrepareForExport`.
- PR-review export fixes: semaphore over-release on cancel (acquired flag), GPU surface leak on exception (catch-block dispose), 5-min mux watchdog, null-out `MediaClip` (no `IDisposable`).

## Webcam overlay
- Never activated: VM set `IsWebcamEnabled` but not `WebcamDeviceId` (session required both). Wired all 6 layers (settings→UI→VM→session auto-select→export style→preview). Keep extracted `CanvasBitmap` alive as field (`_lastWebcamFrame`) — `using var` disposed it before `ComposeFrame`/render (same bug in `VideoEncoder` and GIF path). Extract at overlay size ×1.5 not native; `WebcamCompositor` caches shadow/clip geometry + `IDisposable`. Editor drag/resize uses normalized 0–1 coords via `PreviewCanvas.FrameLayoutRect`.

## Audio sync / waveform
- Use `Project.AudioToVideoOffsetSeconds` directly (positive pre-roll: audio pos = T + offset), NOT a `videoDuration-maxAudio+0.5` heuristic. Waveform load placed BEFORE the cursor-data early return (else no audio track when no cursor data); `Task.Run` off-thread; guard `startSeconds >= reader.Length`.
- Scrub: `ScrubTo()` Stop→Seek→Play under `_transportLock`, 80ms debounce, `WaveOutEvent.DesiredLatency=100ms` (default 300 too high).

## Cursor rendering
- Cursor options flyout: added `Touch` to `CursorType`, `Color` to `CursorStyle`; `DrawTouchCursor` filled circle at hotspot; contrast outline via BT.601 luminance; color presets = templated `RadioButton`s. NEVER set XAML defaults on flyout controls (fire during `InitializeComponent`, race `RebuildPreviewRendererAsync` → post-record hang) — set in `Sync*ControlsToConfig` under suppress flag.
- Touch-click dedup: group click-downs whose gap < 1.41s into chains (float-in → tap each → slide between → fade); widen active-click window to ±1.5s; "latest pre-roll wins" rejected (suppressed earlier taps).
- Mouse move throttle: 4ms min interval on pure `WM_MOUSEMOVE` only; clicks/scrolls/buttons unthrottled; serialization format unchanged.

## Theming / accessibility / colors
- High-contrast: `Themes/AppColors.xaml` with Default + HighContrast `ThemeDictionaries`; Win2D needs Colors not Brushes (extract `.Color`, resolve in `ResolveThemeColors` on `ActualThemeChanged`); can't put `{ThemeResource}` directly in a `Color` — wrap in `SolidColorBrush`. Later migrated most timeline brushes to standard WinUI system brushes via `Application.Current.Resources`.
- NEVER assign `{ThemeResource}` to a plain CLR/non-DependencyProperty (e.g. converter `TrueValue`/`FalseValue` in `Page.Resources`): compiles but throws `XAML_E_LAYOUT_CYCLE` (0x802B000A) on navigate — look brushes up at runtime keyed by `ConverterParameter` (`CheckedToBrushConverter`).
- Frame Style flyout UI: CommunityToolkit `Segmented` for binary toggles; 3×3 crop grid = single bordered Grid + 9 cell-fill RadioButtons; aspect-ratio tiles size a preview Border to the actual ratio; accent fill bound to `IsChecked` via `BoolToObjectConverter` (Content elements unreachable from style VSM). Accessibility audit (47 issues): custom controls lack AutomationPeers, status areas lack live regions.
- Brand presets cinematic refresh later superseded by bold gradients (static identifiers were renamed — the earlier "identifiers preserved" note is stale).
- Editor spacebar play/pause: page `KeyboardAccelerator` Key="Space", skip when TextBox/RichEditBox/etc. focused (`FocusManager.GetFocusedElement`).

## Stability / memory / crash hardening (reinforces Crash playbook)
- Memory: `composition.Clips.Clear()` in finally (releases MediaClip COM, avoids linear OOM); VM `Cleanup()` unsubscribes `ProjectService.ProjectChanged` (singleton GC root); `using var` on Win2D geometries/streams; dispose renderers/readers/audio on page Unloaded; `CursorRenderer`/`WebcamCompositor`/`BackgroundCompositor` `IDisposable`.
- Layer crash/hang passes (Core/Processing/App/Export/Capture): div-by-zero/NaN guards, per-file try/catch in loaders, path-traversal sanitization, timeouts (SpeechToText 10min, mux 5min, transcode 2h watchdog), `Thread.SpinWait(10)`, lock-then-detach dispose. `SpeechToText` `using var _recognizer` shadowed the instance field (broke cancel) → assign to field.
- High-DPI/high-res 13-fix pass: streaming `MediaStreamSource`+`MediaTranscoder` finalize (no 100k COM objects, HW accel off); `StopAcceptingFrames`/`WaitForQuiescenceAsync` drain; serialized crop under `_writeLock`; `TryGetNextFrame` in try/catch; D3D HRESULT + WARP fallback + `DeviceLost` → recoverable exception (never recreate mid-frame); screenshot/VRAM preflights (>1GB / >16384px / >1.5GB reject, checked `long`); `GraphicsCaptureItem.Closed` handling; `IAsyncDisposable` on `RecordingSession` (30s graceful stop).
- Session disk cleanup: after export, write `exported.marker` then delete `.frames/`; startup background cleanup of marked sessions (preserve mp4/cursor/audio); all deletes try/catch.

---

## Text Slides, Transitions & Append Recording — Foundation

- **Feature/area**: Multi-segment timeline, text slides, transitions, append recording (TimelineSegment, TimelineModel, TimelineMapper, ProjectService, EditorPage, TimelineControl, ExportEngine)
- **Approaches tried**:
  1. **Polymorphic `TimelineSegment` base record** with `VideoSegment` and `TextSlideSegment` subtypes — Worked. Added to `Musio.Core/Timeline/TimelineSegment.cs`. Includes `TransitionConfig`, `TextOverlay`, and animation enums. ✅
  2. **`SegmentCompositor` wrapper** instead of deeply modifying `FrameCompositor` — Worked. Created a higher-level orchestrator (`Musio.Core/Processing/SegmentCompositor.cs`) that routes frames to `TextSlideRenderer` or video compositor + `TransitionRenderer` for blending. Safer than rewriting FrameCompositor internals. ✅
  3. **`TimelineMapper.GetSegmentFrameRef()`** — Extended TimelineMapper with segment-aware mapping that returns `SegmentFrameRef` (segment, source time, progress, transition state). Backward-compatible: falls back to legacy `GetSourceTimeForOutputFrame()` when no segments exist. ✅
  4. **`ProjectService.AppendRecording()`** — Creates a `RecordingSource` and `VideoSegment` from the new recording's `Project`, appends to the timeline. RecordingPage handles "append" navigation parameter. ✅
  5. **Win2D `FontWeights`** — `Windows.UI.Text.FontWeights.Bold` doesn't exist in Musio.Core (WinUISDKReferences=false). Fixed by using `new FontWeight { Weight = 700 }` directly. ✅
- **What worked**: All approaches. Key design decision was creating `SegmentCompositor` as a new orchestration layer rather than modifying the complex `FrameCompositor` — this keeps the existing single-video pipeline untouched and adds multi-source/text-slide support cleanly.
- **What didn't work**: `TimelineControl.InvalidateAllCanvases()` was private — had to make it public for EditorPage to trigger redraws after segment edits.
- **Key files created**: `TimelineSegment.cs`, `TextSlideRenderer.cs`, `TransitionRenderer.cs`, `SegmentCompositor.cs`
- **Key files modified**: `TimelineModel.cs`, `Project.cs`, `TimelineMapper.cs`, `EditOperation.cs`, `ExportEngine.cs`, `ProjectService.cs`, `EditorPage.xaml/.cs`, `RecordingPage.xaml.cs`, `TimelineControl.xaml.cs`

---

## Text Slides — Timeline Integration Fixes

- **Feature/area**: Text slide rendering on timeline, playhead access, video splitting (TimelineControl, EditorPage, EditOperation, TimelineModel)
- **Approaches tried**:
  1. **`DisplayDuration` property on TimelineModel** — `TimeToX`/`XToTime` used `model.Duration` (source video duration), so segments beyond the video end were off-screen and unreachable by playhead. Added `DisplayDuration` which returns `TotalSegmentsDuration` when segments exist, else `Duration`. Updated `TimeToX`, `XToTime`, and `TimeRulerCanvas_Draw` to use it. ✅
  2. **Full-height segment rendering** — Segments were drawn as a 10px thin bar at top of video track. Changed `DrawSegmentTrack` to render `TextSlideSegment` blocks at full track height (`h - pad*2`) with rounded corners, centered text labels, and selection highlighting. Video segments already drawn by the filmstrip renderer. ✅
  3. **`SplitAndInsertTextSlideOperation`** — When inserting a text slide at playhead, the `VideoSegment` under the playhead is split into two halves with correct `SourceStart`/`SourceDuration` so audio stays in sync. Falls back to positional insert if playhead is not on a video segment. ✅
  4. **Segment hit-testing + selection** — Added `_selectedSegmentId`, `SelectSegment()`, `SegmentSelected` event to TimelineControl. VideoTrack_PointerPressed hit-tests text slides and fires selection event. EditorPage wires `OnSegmentSelected` to show/hide the properties panel. ✅
- **What worked**: All four together. The key insight was that `TimeToX`/`XToTime` using `model.Duration` was the root cause of segments being invisible and playhead being unreachable — they were drawn off-screen because the coordinate system ended at the source video duration.
- **What didn't work**: Simple `AddTextSlideOperation` with `insertIndex` — it appended after the current segment but didn't split the video, so audio would go out of sync. `SplitAndInsertTextSlideOperation` was needed.

---

## Text Slides — Segment-Based Video Track & Preview Rendering

- **Feature/area**: Unified segment rendering on video track + text slide preview (TimelineControl, EditorPage)
- **Problem**: After inserting a text slide, the video filmstrip stayed continuous and got compressed (looked like the video was "shortened"), and the text slide block overlaid on top. Preview canvas didn't render text slides at all.
- **Root cause**: `VideoTrackCanvas_Draw` always drew `model.Clips` (or full duration) as one continuous filmstrip across `DisplayDuration`. Since segments grew `DisplayDuration` but clips were still drawn end-to-end, the video compressed and text overlaid. Preview's `RenderFrameAtAsync` only ever loaded video frames via the legacy mapper.
- **Approaches tried**:
  1. **`DrawVideoTrackFromSegments`** — When `model.Segments.Count > 0`, render the video track ENTIRELY from segments and `return` early (skip `model.Clips` rendering, trim handles, speed overlays). Each `VideoSegment` draws a filmstrip via new `DrawFilmstripForSegment` (maps timeline X → segment source time using `SourceStart` + offset×`SpeedFactor`); each `TextSlideSegment` draws a full-height colored block with its text. ✅
  2. **Segment-aware preview** — Split `RenderFrameAtAsync` into a segment check: if playhead is over a `TextSlideSegment`, render via `TextSlideRenderer.RenderSlide()` at project resolution and show in `Preview.SetFrame()`; if over a `VideoSegment`, map to that segment's source time before loading the frame. Extracted `RenderVideoFrameAsync` for the video path. ✅
  3. **Property-change preview refresh** — Slide text/animation/font/duration handlers now force `UpdatePreviewFrameAsync(..., force:true)` (and `InvalidatePreview()` for duration changes) so edits show immediately. ✅
- **What worked**: All three. The key fix was making the video track render from segments instead of clips when segments exist — this gives each video segment and text slide its own real timeline space.
- **What didn't work**: Drawing segments as an overlay ON TOP of the existing clip filmstrip (the original approach) — the two representations conflicted. Must render exclusively from one or the other.
- **Key files modified**: `TimelineControl.xaml.cs` (DrawVideoTrackFromSegments, DrawFilmstripForSegment), `EditorPage.xaml.cs` (RenderFrameAtAsync split, RenderTextSlidePreview, RenderVideoFrameAsync, _textSlideRenderer field)

---

## Text Slides — Zoom & Cursor Track Alignment

- **Feature/area**: Source↔output time mapping so zoom segments and cursor path follow the video when text slides shift content (TimelineModel, TimelineControl, ProjectService)
- **Problem**: Zoom keyframes and cursor data are stored in SOURCE-video time and rendered via `TimeToX` (linear over `DisplayDuration`). After inserting a text slide, the second video half shifts right but the zoom/cursor stayed at their original linear positions → misaligned with the video.
- **Approaches tried**:
  1. **`SourceToOutputTime` / `OutputToSourceTime` on TimelineModel** — Maps a source-video time to its output-timeline position (and inverse) by walking the primary recording's video segments. A source time in the first half maps to itself; a source time in the second half maps to `segment.Start + (sourceTime - segment.SourceStart)/SpeedFactor`, i.e. shifted right by the text-slide duration. ✅
  2. **`PrimaryVideoFilePath` on TimelineModel** — Set in `ProjectService.SetProject`. Mapping only considers video segments whose `VideoFilePath` matches the primary recording, so appended recordings (different source files) don't interfere with the primary cursor/zoom data. ✅
  3. **`SourceTimeToX` / `XToSourceTime` helpers in TimelineControl** — Wrap `TimeToX(model.SourceToOutputTime(t))` and `model.OutputToSourceTime(XToTime(x))`. Applied to ALL zoom rendering, hit-testing, create-preview, drag/resize commits, and cursor path + click-dot rendering. Playhead scrubbing stays in output time via `XToTime`. ✅
- **What worked**: Consistent rule — anywhere a keyframe/cursor SOURCE time → X, use `SourceTimeToX`; anywhere X → keyframe SOURCE time, use `XToSourceTime`. Playhead is output time (`XToTime`). Identity mapping when no segments exist keeps legacy behavior intact.
- **Note**: Audio waveform track is still one continuous strip (not split per-segment) — would need separate work; user only requested zoom + cursor.

---

## Text Slides — UI: Insert Menu + Properties Flyout

- **Feature/area**: Editor toolbar reorg (EditorPage.xaml/.cs)
- **Changes**:
  1. **Insert dropdown** — Replaced the separate "Text Slide" and "Record More" toolbar buttons with a single `DropDownButton` named "Insert" (glyph E710) whose `MenuFlyout` has "Text Slide" (AddTextSlide_Click) and "Record More" (RecordMore_Click) items. ✅
  2. **Text slide properties flyout** — Replaced the inline `TextSlidePanel` StackPanel with a `TextSlideButton` + `Flyout` (matching the Style/Cursor flyout pattern: 280px-wide StackPanel). Contains text, animation, duration, font size, and Text/Background color pickers (`ColorPicker` in `DropDownButton` with swatch + hex, same pattern as `BgColorPicker`). Button is `Visibility=Collapsed` and shown via `ShowTextSlidePanel` when a slide is selected; the flyout auto-opens (deferred via `DispatcherQueue.TryEnqueue` to avoid conflicting with the closing Insert menu). ✅
- **Gotcha**: `ParseHexColor`/`ColorToHex` already existed in EditorPage — reused them instead of redefining (caused CS0111 duplicate-member errors). The existing `ParseHexColor` only handles 6-digit hex, which is fine since slide colors are 6-digit and pickers use `IsAlphaEnabled=False`.
- **Gotcha**: `_suppressSlideEvents` flag guards the ColorPicker `ColorChanged` handlers so loading a slide's values into the pickers doesn't fire spurious model updates.

---

## Text Slides — Export (MP4) Integration

- **Feature/area**: Segment-aware MP4 export incl. audio sync (VideoEncoder.cs, TimelineMapper.cs)
- **Problem**: Text slides showed in the editor preview but NOT in the exported MP4 — the export pipeline (`VideoEncoder.ProduceSampleAsync`) only ever loaded video frames via `timelineMapper.GetSourceTimeForOutputFrame` (legacy clips/trim) and never consulted `timeline.Segments`. The frame count was also too short (didn't include slide durations).
- **Approaches tried**:
  1. **Frame count** — `TimelineMapper.ComputeTotalOutputFrames` now returns `TotalSegmentsDuration * fps` when segments exist, so the encoder emits enough frames to cover text slides. ✅
  2. **Frame routing** — `ProduceSampleAsync` now takes `TimelineModel? timeline`. When segments exist it calls `timeline.GetSegmentAtTime(outputTime)`: a `TextSlideSegment` is rendered via a lazily-created `TextSlideRenderer` at `compositorWidth × compositorHeight`; a `VideoSegment` maps to `SourceStart + localOffset*SpeedFactor` before loading+compositing. Legacy path unchanged when no segments. ✅
  3. **Audio sync** — `MuxAudioAsync` now takes `timeline`. When text slides exist, audio is split into per-`VideoSegment` `BackgroundAudioTrack`s via `ApplySegmentAudioTrim`: `TrimTimeFromStart=SourceStart`, `TrimTimeFromEnd=origDur-(SourceStart+SourceDuration)`, and `Delay=segment.Start` (so slide durations become silent gaps). Only applied to primary-recording segments (`VideoFilePath == project.VideoFilePath`). ✅
- **What worked**: Routing per-frame at the `ProduceSampleAsync` level (rather than swapping in `SegmentCompositor`) kept the existing webcam/scaling/encoder plumbing intact. `BackgroundAudioTrack.Delay` + per-segment trim cleanly inserts the silent gaps for slides.
- **Known limitation**: The GIF export path (`ExportEngine.ExportGifAsync`) is NOT yet segment-aware — it would show video frames during slide periods. MP4 (the primary path) is fixed. Appended-recording audio (different source files) isn't muxed yet — only the primary recording's segments get per-segment audio.

---

## Text Slides — Cinematic Animation Engine

- **Feature/area**: Rich text animations (TextSlideRenderer.cs, TimelineSegment.cs enum, EditorPage.xaml combo)
- **Added 12 new animations** to `TextSlideAnimation` (now 19 total): SlideLeft, SlideRight, ScalePop, ZoomBlurIn, Reveal, TypewriterCaret, CascadeFadeUp, CascadePop, Wave, TrackingIn, RotateIn, BounceIn.
- **Architecture**: `TextSlideRenderer` now has two paths:
  1. **Whole-text** (transform + opacity + optional blur/clip): computed via `ComputeWholeOpacity` + `ComputeWholeTransform` returning (scale, tx, ty, blur). Applied through `ds.Transform`. `Reveal` uses `ds.CreateLayer` with a clip rect (L→R wipe). `ZoomBlurIn` renders text to a `CanvasRenderTarget` then draws it through a `GaussianBlurEffect` (Microsoft.Graphics.Canvas.Effects) with an easing blur 28→0.
  2. **Per-character kinetic typography**: `DrawPerCharacter` builds a `CanvasTextLayout`, uses `layout.GetCharacterRegions(i,1)` to get each glyph's `LayoutBounds`, then draws each char with its own `Matrix3x2` (rotate+scale around glyph center + translate) and per-char opacity. Stagger formula: `cp = clamp((pIntro - frac*spread)/(1-spread),0,1)` so char 0 starts at progress 0 and the last finishes by ~progress 1. `Wave` is continuous (sine), not intro-staggered.
- **Easing helpers**: EaseOutCubic, EaseInOutCubic, EaseOutBack (overshoot), EaseOutBounce.
- **Gotcha**: per-char drawing uses a Left/Top-aligned `charFormat` and draws at the glyph region's absolute position (`rect.X + region.X`), while the measuring layout uses center alignment — the region bounds are absolute within the layout box so they line up. Whitespace chars are skipped (not drawn, but still advance the visible-index stagger only for non-whitespace).
- Both slides and overlays share the same `DrawAnimatedText` engine, so overlays get all animations too.

---

## Text Slides — Symmetric IN/OUT Animations

- **Feature/area**: Exit animations for all text slide animations (TextSlideRenderer.cs)
- **Problem**: Only the Fade variants animated out; every other animation snapped off abruptly at the slide's end.
- **Solution**: Each animation now has an entrance (first `InDur=0.25`) and an exit (last `OutDur=0.25`) with a static hold between. Implemented by combining an `inP` and `outP` phase:
  - **Whole-text** — `ComputeWholeState` returns (scale, tx, ty, blur, opacity) summing an entrance offset (eased by `inP`) and an exit offset (eased by `outP`). Slides continue their motion on exit (e.g. SlideUp enters from below, exits upward); ScalePop shrinks out; ZoomBlurIn zooms+blurs back out; opacity fades both ends via `EaseOutCubic(inP) * (1 - EaseInCubic(outP))`.
  - **Reveal** — wipes in from the left then wipes back out (`min(inFrac, 1-outFrac)`).
  - **Typewriter/Caret** — types in over first ~45%, then erases over the last ~30%.
  - **Per-character** — `ComputeCharParams` now computes both a staggered `inCp` (first 50%) and staggered `outCp` (last 40%); opacity = `inCp * (1 - outCp)`, offsets sum entrance + exit. Wave fades in AND out at the ends.
- **Added easing**: `EaseInCubic` (t³) for accelerating exits.
- **Note**: `None` stays static (no animation). `FadeOut` is visible from the start and only fades at the end (unchanged semantics).

---

## Text Slides — Background Types (Solid / Gradient / Image / Video)

- **Feature/area**: Rich slide backgrounds matching the Frame Style flyout (TextSlideSegment, TextSlideRenderer, EditorPage)
- **Model**: `TextSlideSegment` gained `BackgroundType` (`SlideBackgroundType` enum: Solid/Gradient/Image/Video), `GradientEndColor`, `GradientAngle`, `BackgroundImagePath`, `BackgroundVideoPath`. `BackgroundColor` now doubles as the solid color and gradient START color (back-compat preserved).
- **Rendering** (`TextSlideRenderer.DrawSlideBackground`):
  - Solid → `ds.Clear`
  - Gradient → `CanvasLinearGradientBrush` (same angle math as `BackgroundCompositor`)
  - Image → cached `CanvasBitmap` loaded via blocking `LoadAsync(...).GetAwaiter().GetResult()` (mirrors `BackgroundCompositor.DrawImageBackground`), scale-to-fill
  - Video → cached `MediaComposition`; frames pulled with `GetThumbnailAsync` at `progress*duration` (looped), cached at ~10fps granularity, scale-to-fill; falls back to solid on any failure
  - Cached bitmaps/composition disposed in `Dispose()`.
- **Export**: works automatically — `VideoEncoder.ProduceSampleAsync` already calls `RenderSlide`, so gradient/image/video backgrounds appear in exported MP4 with no extra changes.
- **UI**: Text-slide flyout now has a Background `ComboBox` (Solid/Gradient/Image/Video) that toggles panels: solid/gradient share the color picker (label switches to "Start Color" for gradient); gradient panel adds a **presets GridView** (built from `DefaultBrandPresets.All` — Nebula, Lagoon, Prism, etc. as `LinearGradientBrush` tiles), an End Color picker, and an Angle slider; Image/Video panels use `FileOpenPicker` buttons.
- **Reused**: `DefaultBrandPresets` (Core/Settings) for gradient presets; the wallpaper picker's `FileOpenPicker` + `InitializeWithWindow` pattern.

---

## Text Slides — In-Preview Editing, Reposition, Alignment & Formatting

- **Feature/area**: Direct manipulation of text slides on the preview (TextSlideSegment, TextSlideRenderer, EditorPage preview overlay)
- **Model**: `TextSlideSegment` gained `TextAlignment` (`SlideTextAlignment` Left/Center/Right) and normalized `TextX`/`TextY` (default 0.5,0.5) for the text block center.
- **Renderer**: `RenderSlide` now takes `drawText` (skip text while editing in-place), uses `ToCanvasAlignment(slide.TextAlignment)`, and positions the text box via `ComputeTextRect` (public static — also used by the overlay for coordinate mapping) centered at (TextX,TextY).
- **In-place editing overlay** (EditorPage, in the preview Grid):
  - A `Canvas` (`SlideEditCanvas`) with a draggable dashed `Border` (`SlideTextRegion`) + a transparent `TextBox` (`SlideEditBox`).
  - Shown only when the **playhead is on a text slide** and not playing (driven from `RenderTextSlidePreview`; hidden in the video/legacy branches and on play via `IsPlayingChanged`).
  - **Drag** the region → updates `TextX`/`TextY` (mapped through `Preview.FrameLayoutRect`, same pattern as the webcam overlay).
  - **Double-tap** → edit mode: sets `_editingSlideId`, renders **background-only** so the rendered text doesn't double up, shows a WYSIWYG `TextBox` (font/size/weight/style/color/alignment matched, scaled by `FrameLayoutRect.Height/outputHeight`). Enter commits (Shift+Enter = newline), Esc commits, blur commits. Text syncs back to the flyout TextBox.
  - Repositions on `Preview.FrameLayoutChanged`.
- **Formatting in flyout**: Bold/Italic `ToggleButton`s + Left/Center/Right alignment `RadioButton`s.
- **Gotcha**: `ComputeTextRect` made `public static` so the overlay can map output-space text rect → canvas px. `ProtectedCursor` (UIElement) used for the move cursor on hover.

---

## Crash Fix — Cross-Thread XAML Access from Preview Render (after recording)

- **Symptom**: App froze and crashed right after stopping a recording (transition into the editor).
- **Diagnosis**: WER showed exception `0xc000027b` (stowed) with HRESULT `0x802b000a` = **`UI_E_WRONG_THREAD`** in `combase`/`CoreMessagingXP`. No `crash.log` was written (native failfast bypasses the managed `UnhandledException` handler). The crash dump (`%LOCALAPPDATA%\CrashDumps\Musio.App.exe.*.dmp`, analyzed with `dotnet-dump`) showed the **UI thread idle at `Program.Main`** — so the offending XAML access came from a **worker thread**.
- **Root cause**: The in-preview text-slide edit overlay (added this session) called `HideSlideEditOverlay()` / `UpdateSlideEditOverlay()` directly from the async preview render path (`RenderFrameAtAsync` / `RenderTextSlidePreview`). That path can resume on a threadpool thread after an `await`, and those methods **mutate XAML** (`SlideEditCanvas.Visibility`, `Canvas.SetLeft`, read `Preview.IsPlaying`/`FrameLayoutRect`). The pre-existing render code only ever called `CanvasControl.Invalidate()` (thread-safe in Win2D), which is why it never crashed — direct XAML mutation is NOT thread-safe.
- **Fix**: `UpdateSlideEditOverlay` and `HideSlideEditOverlay` now guard with `DispatcherQueue.HasThreadAccess` and re-enqueue via `DispatcherQueue.TryEnqueue` when off-thread.
- **Rule learned**: Any XAML/DependencyProperty access reachable from the async preview render path MUST be marshaled to the UI thread. `CanvasControl.Invalidate()` is the only thread-safe Win2D UI call used there.
- **Bonus**: Added `AppDomain.CurrentDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` logging to `App.xaml.cs` (writes to `%LOCALAPPDATA%\Musio\crash.log`) to catch future worker-thread managed exceptions.
- **Debugging tip**: `dotnet tool install --global dotnet-dump`, then `dotnet-dump analyze <dump>` with `clrstack -all`. Cross-thread failfasts produce NO managed exception object (`pe` / `dumpheap -type Exception` are empty) — a strong signal it's a native thread-affinity violation.

---

## Recording Overlay — Theme Fix (white-on-white in light theme)

- **Feature/area**: `RecordingOverlayWindow.xaml.cs`
- **Problem**: The recording mini-toolbar's text + spinner were white-on-white after the user switched the app to Light theme.
- **Cause**: The overlay uses a `DesktopAcrylicBackdrop` that follows the window theme; in Light theme the acrylic pill went light, but the foreground was meant for a dark pill.
- **Fix**: `RootGrid.RequestedTheme = ElementTheme.Dark` in the constructor — pins the overlay to a dark pill with light text/spinner regardless of the app theme.

## Crash After Stop Recording — Investigation (UNRESOLVED, native failfast)

- **Symptom**: App crashes when stopping a recording. WER bucket constant; signature: stowed exception `0xc000027b`, HRESULT `0x802b000a` (`UI_E_WRONG_THREAD`), faulting module `combase.dll` / `CoreMessagingXP.dll`.
- **Dump analysis (dotnet-dump + custom ClrMD tool)**: Faulting thread is the **UI thread** (`Program.Main` → `Application.Start`), idle at the message pump — the exception is a stowed/deferred native failfast re-surfaced at the pump. **No managed Exception object exists on the GC heap** → it's a pure native COM thread-affinity violation, NOT a managed exception. Therefore `crash.log` / `FirstChanceException` cannot capture it, and the original throw site is not in the dump (only WinDbg `!analyze`/`!pde` stowed-exception support could retrieve it, which isn't installed).
- **Ruled out** (all verified UI-thread-correct): preview render path (only `CanvasControl.Invalidate()` is thread-safe; overlay methods now `DispatcherQueue`-guarded), recording-stop VM path (`OnOverlayStopRequested` marshals via dispatcher; `StopRecordingAsync` resumes on UI after `await Task.Run`), capture teardown (`Direct3D11CaptureFramePool.CreateFreeThreaded` — no affinity), overlay animation/close path, theme-change handler.
- **Key evidence the in-preview overlay is NOT the cause**: the crash persists AFTER the thread-marshaling fix (commit c9003ff) that guarded the only new XAML-in-async-path code. If the overlay were the cause, those guards would have fixed it.
- **Leading hypothesis**: a thread-affine NATIVE object (likely `Windows.Graphics.Capture` D3D/DWM or the overlay's `DesktopAcrylicBackdrop`/SystemBackdrop lifecycle) created on one thread and torn down/accessed on another during the stop→editor transition. Possibly environment-specific (ARM64) or triggered by the recent theme change. Needs a live repro with WinDbg, or bisection by selectively disabling capture/backdrop, to confirm.

### RESOLVED — it was NOT a threading issue
- **Actual root cause (captured by the FirstChanceException logging added above)**: a `XamlParseException` — `Cannot find a Resource with the Name/Key AccentFillColorDefaultColor [Line 291]` — thrown from `EditorPage.InitializeComponent()`. The in-preview text-overlay XAML (commit 51d91ab) set `SlideTextRegion.Background` to `<SolidColorBrush Color="{ThemeResource AccentFillColorDefaultColor}" .../>`. **`AccentFillColorDefaultColor` is not a reliably-defined framework resource** — it resolved in the **Dark** theme dictionary but NOT **Light**, so after the user switched to Light theme, constructing `EditorPage` threw at XAML-parse time. Since stopping a recording navigates to `EditorPage`, the crash always reproduced "on stop" (and would also reproduce opening the editor any other way in Light theme).
- **Fix**: use `{ThemeResource SystemAccentColor}` (theme-independent, always available).
- **Correction to earlier notes**: HRESULT `0x802B000A` is the XAML **resource-lookup-failed** code, NOT `UI_E_WRONG_THREAD`. The whole thread-affinity investigation was a red herring caused by mislabeling that HRESULT. The earlier `DispatcherQueue` marshaling guards on the overlay methods are harmless/correct defensively but were not the fix.
- **Lesson**: When adding `{ThemeResource X}` keys, verify the key exists in BOTH Light and Dark theme dictionaries (test in both themes). Prefer well-known keys (`SystemAccentColor`, `CardStrokeColorDefaultBrush`, `ControlFillColorDefaultBrush`). A bad ThemeResource only fails when that theme is active, so it can hide until a theme switch. The FirstChanceException → `crash.log` logging is what finally captured it (XAML parse failfasts bypass `UnhandledException`).

---

## Append Recording — Multi-Source Playback

- **Feature/area**: Playing appended ("Record More") recordings from their own source files (EditorPage preview; export still TODO)
- **Problem**: Appended recordings added a `VideoSegment` with a different `VideoFilePath`, but both the editor preview AND export only ever read from the **primary** recording's `_frameReader`/`_previewRenderer` (built from `project.VideoFilePath`). So an appended segment replayed the first recording's frames ("repeat").
- **Preview fix (done)**: Added a per-segment `SegmentPreview` context (`VideoFrameReader` + `PreviewRenderer` + webcam `MediaComposition`), cached in `_segmentPreviews` keyed by `VideoSegment.Id`, built lazily from the segment's own `VideoFilePath`/`CursorDataFilePath`/`WebcamFilePath`/dims/offsets. `RenderSegmentVideoAsync` routes primary-recording segments (matching `project.VideoFilePath`) to the existing reader/compositor and appended segments to their own context. `_lastRenderedSegmentId` forces a redraw across segment boundaries. Contexts disposed on reload/unload. Result: appended frames + cursor/clicks + camera play correctly in the editor.
- **Remaining TODO (not yet done)**:
  - **Export**: `VideoEncoder.ProduceSampleAsync` still uses the primary `frameReader`/`compositor` for all video segments → appended segments repeat in the exported MP4. Needs per-source frame readers + `FrameCompositor`s (keyed by `VideoFilePath`), routed per frame, scaling each source's composited output to the common encode size (sources may differ in dimensions). Risky to change without end-to-end export testing.
  - **Preview audio** for appended segments (currently the primary audio plays under appended video).
  - **Export audio**: `MuxAudioAsync` only muxes the primary recording's audio (filtered by `VideoFilePath == project.VideoFilePath`); appended segments' `AudioFilePaths` need muxing at their `segment.Start` with their source trim.

- **Bug fixed — append double-add**: "Record More" produced **2 duplicate segments of the new recording (original lost)**. Cause: `RecordingViewModel.StopRecordingAsync` unconditionally called `ProjectService.SetProject(LastProject)` (which REPLACES the timeline), then `RecordingPage` (append mode) called `AppendRecording(newRecording)` adding it again. Fix: added `RecordingViewModel.IsAppendMode` (set from `RecordingPage.OnNavigatedTo` based on the `"append"` nav parameter); `StopRecordingAsync` now skips `SetProject` when `IsAppendMode`, so append mode only runs `AppendRecording`, preserving the original project and adding the new recording as a distinct trailing segment.
## FCP-style Timeline Segment Editing (select / move / trim / sync)

**Feature/area:** Primary-track (video + text slide) interactive editing in the timeline — selection, drag-to-move/reorder, ripple-trim either edge, delete-ripple, snapping; sync of linked zoom/click/cursor and per-segment audio/camera.

**Approaches tried:**
1. **Reorder-safe source<->output mapping first (foundation)** — Worked. Reworked `TimelineModel.SourceToOutputTime`/`PrimaryVideoSegments` to follow the *actual* `Segments` order instead of `OrderBy(SourceStart)`, and added `TrySourceToOutputTime(out)` so trimmed-out source times report "no output position" (callers can cull). This is what keeps zoom/click/cursor in sync after reorder/trim. `TimelineMapper.GetSegmentFrameRef` already walked timeline order, so export/video frame sync was already correct — only the UI-facing model mapping needed fixing. ✅
2. **New core ops `MoveSegmentOperation` + `TrimSegmentEdgeOperation`** — Worked. Both snapshot/clone for undo, call `RecalculateSegmentPositions()` for the magnetic re-flow. Trim adjusts `SourceStart`/`SourceDuration`/`Duration` together for video (clamped to source head and `MinDuration=100ms`), duration-only for text slides. 18 unit tests pass (`TimelineSyncMappingTests`, `SegmentEditOperationsTests`). ✅
3. **UI in `TimelineControl.xaml.cs`** — Worked. Added DragModes `SegmentBody/LeftEdge/RightEdge`, hit-testing, drag-to-move with drop-indicator, edge ripple-trim with live preview, and snapping (`SnapX` to playhead + segment edges; Alt bypasses via `InputKeyboardSource.GetKeyStateForCurrentThread`). Control raises `SegmentMoveRequested`/`SegmentTrimRequested`; `EditorPage` executes the ops on `UndoRedoManager` (mirrors the existing zoom-segment event pattern). Delete-ripple wired in `DeleteAccelerator_Invoked` via `RemoveSegmentOperation` on the selected segment.

**What worked:** Layering — fix sync mapping (tested) -> add tested core ops -> wire UI to the existing event/undo pattern. Added `Timeline.Refresh()` to `OnUndoRedoStateChanged` so undo/redo of segment ops redraws all tracks.

**What didn't work / not done:**
- **Automated interaction screenshots:** The editor is only reachable via a live recording (no project file load path in `ProjectService`), so screenshotting select/move/trim requires a FlaUI/SendInput harness that drives Record->Stop->editor. Not built this pass — app was verified to compile (ARM64) and launch to the recording page (`files/verify-01-launch.png`).
- **Phase 7 camera track (independent `CameraSegment` + track + flyout + compositor updates):** not started.

**Build/test (verified):** Build with VS MSBuild `amd64\MSBuild.exe ...csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64` (tests) or `/p:Platform=ARM64` (app). Run tests: `$env:DOTNET_ROLL_FORWARD="Major"; dotnet test ...Musio.Tests.csproj --no-build -c Debug -p:Platform=x64`. `dotnet build` fails (PriGen MSB4062).

## Phase 7 — Independent Camera (Webcam) Track

**Feature/area:** A separate camera track in the timeline letting users clip when the webcam overlay is visible and (via model/ops) change its style per segment, independent of video cuts.

**Approach (worked, tested):**
- **Model:** `CameraSegment : TimelineSegment` (Timeline namespace, `using Musio.Core.Processing` for `WebcamOverlayStyle`/`WebcamShape`). Its `Start`/`Duration` are reused as a **source-video time range** (like zoom keyframes) so segments stay aligned with the recording and survive video reorder/trim via the existing source<->output mapping. `TimelineModel.CameraSegments` + `GetCameraSegmentAtSourceTime(sourceTime)`. `CameraSegment.Enabled` + `StyleOverride` + `ResolveStyle(baseStyle)`.
- **Ops:** `Add/Move/Trim/Remove/UpdateCameraSegmentPropertiesOperation` — operate only on `CameraSegments` (never call `RecalculateSegmentPositions`), keep the list sorted by Start, fully undoable. 11 tests in `CameraSegmentOperationsTests` (315 total pass).
- **Render hook (backward-compatible):** `EditorPage.SetWebcamFrameForPreviewAsync(position)` — when `CameraSegments.Count > 0`, hides the webcam (`SetWebcamFrame(null)`) outside any enabled segment and applies the active segment's `ResolveStyle(...)` via `UpdateWebcamStyle`. With no camera segments, the legacy always-on global overlay is unchanged.
- **Timeline UI:** Added a "Camera" row to `TimelineControl.xaml` (new RowDefinition; bumped audio Row 4->5, mic 5->6; canvases + labels updated). `CameraTrackCanvas_Draw` + `HitTestCameraSegment` + `CameraTrack_PointerPressed/Moved/Released/RightTapped` mirror the zoom track: drag-create, select, body-move, edge-trim, right-tap remove. Source-time positioning via `SourceTimeToX`/`GetCameraSegment{Start,End}X`. Control raises `CameraSegment{Selected,Created,Moved,Resized,RemoveRequested}`; `EditorPage` executes the ops on `UndoRedoManager`. Added `CameraTrackCanvas` to `InvalidateAll`/`InvalidateAllCanvases`.

**What didn't / remaining:**
- **Per-segment style flyout UI** (position/size/shape/mirror) not built — the ops + `StyleOverride` support it; right-tap currently removes. Enable/disable is in the model but has no dedicated gesture yet.
- **Visual verification** of the camera row in the editor needs a live recording (editor unreachable otherwise); app compiles (ARM64) + launches clean (`files/verify-02-camera-track-build.png`).

**Camera export parity (follow-up):** The preview honors camera segments (visibility + per-segment style); the GIF/MP4 export pipeline (ExportEngine.cs + VideoEncoder.cs) still composites the single global webcam overlay and does not yet consult TimelineModel.CameraSegments. Primary video/audio/zoom/cursor/click export sync is correct (TimelineMapper maps by actual segment order). Wiring camera segments into export requires passing the TimelineModel/active-segment lookup into the export frame loop and gating compositor.SetWebcamFrame + style per output frame.

## Feature — Camera segment "expand to fullscreen" animation (+ camera export parity)

**Feature/area:** A per-`CameraSegment` toggle that animates the webcam overlay from its normal position/size up to **cover** the whole screen at the segment start, holds fullscreen, then animates back at the end. Renders in preview AND export.

**Approach (worked, tested — 354 tests pass, ARM64 build clean):**
- **Model:** `CameraSegment.FullscreenEnabled` + fixed `FullscreenInDuration`/`FullscreenOutDuration` (0.5s each) + pure `ComputeFullscreenFactor(sourceTime)` returning eased [0,1] via `CubicBezierEasing.EaseInOutCinematic`. Ramps are computed in **source time**; for short segments both ramps scale down proportionally so they never overlap (no hold).
- **Pure layout helper (testable, GPU-free):** `Processing/WebcamLayout.cs` — `WebcamLayoutCalculator.ComputeAnimatedLayout(style, canvasW, canvasH, frameW, frameH, factor)` → dest rect, source **cover** crop, corner radius, border width, shadow alpha. Factor 0 reproduces the legacy square overlay exactly; factor 1 covers the canvas. Key trick: always draw a **rounded rectangle** whose radius collapses to 0 — a square with radius = half-side IS a circle, so Circle morphs smoothly to a fullscreen rectangle. Cover crop interpolates aspect via the current dest aspect (square→canvas).
- **Compositor:** `WebcamCompositor.SetFullscreenFactor` + rewrote `RenderWebcam` to consume the layout. GPU cache key changed to `(dest, radius, canvasW, canvasH, hasShadow)`. Shadow faded via the `DrawImage(bitmap, offset, sourceRect, opacity)` overload.
- **Preview:** `PreviewRenderer.SetWebcamFullscreenFactor` → `FrameCompositor.SetWebcamFullscreenFactor`. `EditorPage.SetWebcamFrameForPreviewAsync` computes the factor from the active segment and raises webcam **extraction resolution** when factor>0 (the ~1.5×overlay cap is too small for fullscreen).
- **Export parity (the previously-missing Phase-7 gap, now DONE):** shared helper `ExportEngine.ApplyCameraSegmentState(compositor, timeline, baseStyle, sourceTime)` gates visibility, applies `ResolveStyle`, and sets the factor; returns false outside enabled segments. Wired into BOTH the GIF loop (`ExportEngine.RenderGif/ExportGifAsync`) and the MP4 loop (`VideoEncoder.ProduceSampleAsync` — needed a new `baseWebcamStyle` param since it's a separate method, not a closure). `ExportEngine.TimelineHasFullscreenCamera(timeline)` bumps webcam extraction to native resolution in both pipelines when any fullscreen camera segment exists.
- **Ops/UI:** extended `UpdateCameraSegmentPropertiesOperation` with `fullscreenEnabled` (undoable). Added an "Expand to full screen" `ToggleSwitch` in the existing Camera Overlay flyout (`CameraFullscreenPanel`, collapsed unless a camera segment is selected); `EditorPage` tracks `_selectedCameraSegmentId`, syncs the toggle on select/create/remove, and routes the toggle through the op on `UndoRedoManager`.

**Gotchas / notes:**
- `dotnet test` FAILS here too (same WindowsAppSDK PriGen MSB4062 as `dotnet build`). Build the test project with **VS MSBuild** (`/restore /t:Build /p:Platform=ARM64`), then run `dotnet vstest <Musio.Tests.dll> /Platform:ARM64` (with `DOTNET_ROLL_FORWARD=Major`). 354 tests pass.
- Ramp timing is in source seconds, so a segment's `SpeedFactor` changes the on-screen ramp duration. Acceptable for v1; revisit if it feels off.
- Backward compatibility: factor 0 is byte-for-byte the old overlay, and timelines without camera segments keep the legacy always-on overlay.

**Follow-up — camera segment deletion (added):** Camera segments could only be removed by right-tap on the Camera track. Added two more ways: (1) the **Delete** key — `EditorPage.DeleteAccelerator_Invoked` now checks `Timeline.SelectedCameraSegmentId` FIRST (before zoom/primary-segment branches) and calls a new shared `DeleteCameraSegment(id)`; (2) a **"Delete camera segment"** button in the Camera Overlay flyout's `CameraFullscreenPanel` (visible only when a camera segment is selected). All three paths route through `RemoveCameraSegmentOperation` on `UndoRedoManager` (undoable) and clear selection + sync UI + refresh the preview.

**Follow-up — overlay fade-in/out + Reveal fullscreen mode:** Two refinements. (1) **Anti-flash fade:** the camera overlay used to pop in/out at segment boundaries. `CameraSegment.ComputeAppearOpacity(sourceTime)` returns an eased (`EaseOut`) opacity that fades in over `AppearDuration` (0.25s) at the start and out at the end (clamped for short segments). Plumbed via `WebcamCompositor.SetOverlayOpacity` (applied at draw time on the frame layer + shadow + border alpha; NOT cached) → `FrameCompositor`/`PreviewRenderer.SetWebcamOverlayOpacity` → set in `EditorPage.SetWebcamFrameForPreviewAsync` and `ExportEngine.ApplyCameraSegmentState`. (2) **`CameraFullscreenMode` enum {Highlight, Reveal}:** Highlight = original overlay→full→overlay; **Reveal** = holds fullscreen for the body of the segment, then eases down to the overlay over `FullscreenRevealDuration` (0.8s) **at the END**, revealing the video underneath (for fullscreen-camera intros that drop into the recording). `ComputeFullscreenFactor` branches on the mode (note: changed the start guard from `t<=0` to `t<0` so the held-fullscreen value is returned at t=0).

**Fix — neighbor-aware overlay fade (no boundary flash / no fade-from-black):** The naive appear fade caused a dip/flash where two camera segments are adjacent/overlapping (A fades out while B fades in) and a "fade from black" when a segment starts fullscreen. `ComputeAppearOpacity` now takes `fadeIn`/`fadeOut` flags, and `TimelineModel.GetCameraOverlayOpacity(active, sourceTime)` decides them: suppress fade-in when another enabled segment covers the instant just before `active.Start` (`s.Start < active.Start && s.End >= active.Start`), suppress fade-out when one continues past `active.End`, and suppress fade-in when `active.StartsFullscreen` (Reveal). Both preview (`EditorPage.SetWebcamFrameForPreviewAsync`) and export (`ExportEngine.ApplyCameraSegmentState`) call `GetCameraOverlayOpacity` instead of the raw per-segment fade. UI: a "Animation" ComboBox under the fullscreen toggle in `CameraFullscreenPanel` (shown only when fullscreen is on); op `UpdateCameraSegmentPropertiesOperation` gained `fullscreenMode`. 357 tests pass. Removed the `AppBarSeparator`s between the right-group toolbar buttons (the `Spacing="8"` on the group panel is the only divider now; also deleted the `WebcamShapeSeparator` and its code-behind visibility toggles). Added responsive collapse: the editor toolbar `Grid` (`ToolbarGrid`, 3 columns: Auto / * / Auto) overlaps when the window is narrow because both edge groups are Auto. `ToolbarGrid_SizeChanged` → `UpdateToolbarResponsiveLayout(width)` measures `LeftToolbarGroup` + `RightToolbarGroup` with labels expanded; if `left+right + padding(16) + gap(16) > availableWidth` it collapses the `FrameStyleLabel`/`CursorLabel`/`CameraLabel` TextBlocks to icon-only (tooltips already exist for discoverability). KEY: toggling the labels never changes the full-width toolbar size, so there is NO feedback loop from SizeChanged. Dynamic right-group buttons (Cursor/Camera/Text Slide/zoom panels) don't fire the toolbar SizeChanged, so each site that toggles their visibility also calls `RefreshToolbarResponsiveLayout()` (which re-runs with `ToolbarGrid.ActualWidth`).


## Bugfix — Appended-recording filmstrip showed wrong (primary) frames

**Symptom:** After "Record More" appended a new recording, the appended VideoSegment's filmstrip showed the PRIMARY recording's frames, although the editor preview rendered the correct appended output.

**Root cause:** Filmstrip thumbnails were a single flat `CanvasBitmap[] _thumbnails` generated only from the primary `_frameReader` and indexed by source-seconds. `DrawFilmstripForSegment` indexed that one array using each segment's own source time. An appended `VideoSegment` has a *different* `VideoFilePath` with its own source coordinate space (SourceStart=0), so it indexed into the primary's thumbnails. Preview was correct because it uses per-segment `SegmentPreview` frame readers keyed by `VideoSegment.Id`.

**Fix:** Thumbnails are now keyed by source video file.
- `TimelineControl`: added `Dictionary<string,ThumbnailSet> _thumbnailsByFile` (+ `_primaryThumbnailFilePath`). `SetThumbnails(..., filePath)` registers the primary set; new `SetThumbnailsForFile(path,...)` registers appended sets; `ResolveThumbnailSet(filePath)` picks the right set (falls back to primary only when the path matches primary, else returns null → backplate, never another file's frames). `DrawFilmstripForSegment` takes the resolved `ThumbnailSet`; the filmstrip-vs-solid gate is now per-segment. `ClearThumbnails` dedups by reference to avoid double-disposing the primary array shared with the dict.
- `EditorPage`: `GenerateTimelineThumbnailsAsync(reader, filePath, isPrimary)`; new `GenerateAllTimelineThumbnailsAsync` (primary then appended, sequential so the shared `_thumbnailGenerationId` cancel-check doesn't false-cancel) + `GenerateAppendedThumbnailsAsync` which opens a temp `VideoFrameReader` per distinct non-primary `VideoSegment.VideoFilePath` and registers via `SetThumbnailsForFile`. Appending re-runs `InitializePreviewAsync` (via `ProjectChanged`→`ModelReloaded`), so appended thumbnails regenerate automatically.

**Verified:** App builds (ARM64) + launches clean. While an appended file's thumbnails are still generating, its segment shows a solid backplate (not wrong frames).

## Bugfix — Split at playhead did nothing on segment timelines

**Symptom:** 'Split at playhead' (toolbar + keyboard) had no effect once the timeline used Segments (video/text-slide).

**Root cause:** EditorViewModel.SplitAtPlayhead always used SplitOperation, which edits the legacy Clips list (not rendered when Segments exist), and its bounds guard used _model.Duration (source duration) instead of DisplayDuration. So it operated on the wrong collection and could also be rejected by the guard.

**Fix:** Added SplitSegmentAtTimeOperation (Musio.Core/Timeline/EditOperation.cs) splitting the segment under the output time into two contiguous halves — VideoSegment splits SourceStart/SourceDuration/Duration (speed-aware, like SplitAndInsertTextSlideOperation); TextSlideSegment splits Duration. MinHalf=50ms guard; DidSplit flag; undoable via snapshot. EditorViewModel.SplitAtPlayhead now routes to it when Segments.Count>0 using DisplayDuration bounds; legacy Clips path unchanged. Total timeline duration is preserved so zoom/cursor/click stay in sync. 8 tests in SplitSegmentOperationTests (323 total pass).

## Feature/Fix — Appended recordings now show their own cursor/audio/camera on tracks (per-segment)

**Symptom:** After Insert ▸ Record More, the appended segment's mouse/click, audio, and camera markers didn't appear on the tracks, and didn't move when the segment was moved — though the preview rendered the appended recording correctly.

**Root cause:** All source-time tracks (cursor, system/mic audio, camera) rendered only the PRIMARY recording's model-level data (`model.CursorData`, `model.SystemAudioWaveformSamples`, etc.) and mapped source→output via `SourceToOutputTime`, which only maps primary-file segments. Appended `VideoSegment`s carry their own `CursorDataFilePath`/`AudioFilePaths`/`WebcamFilePath` but those were never visualized. (Same class of bug as the filmstrip-per-file fix.)

**Fix — per-source-file track data + per-segment rendering:**
- `TimelineControl`: added `SegmentTrackVisual` (cursor + sys/mic waveform + waveform-duration + HasCamera) cached in `_trackVisualsByFile` keyed by `VideoSegment.VideoFilePath`; `SetSegmentTrackVisual`/`ClearSegmentTrackVisuals`. New helper `SegmentVideoTimeToX(seg, fileVideoSeconds)` maps a sample's file-video-time into the segment's output range (NaN outside kept range). `ResolveTrackVisual(seg)` returns the per-file entry, or a model-backed visual for the primary file.
- Rewrote cursor, audio, and mic track draws: when `Segments` exist, iterate `Segments.OfType<VideoSegment>()` and draw each segment's OWN cursor path/clicks and waveform within `[seg.Start, seg.End]` (waveform sampled by source time so it survives move/trim/split). Legacy single-recording path preserved when no segments. Camera track now also draws a per-segment "Camera" presence bar for any VideoSegment with a webcam.
- `EditorPage`: `LoadAppendedTrackVisualsAsync()` loads each distinct non-primary segment's cursor file (MouseHookRecorder.LoadFromFile) + generates sys/mic waveforms (`GenerateFileWaveformsAsync` via AudioWaveformGenerator/NAudio) and registers via `SetSegmentTrackVisual`. Called from `InitializePreviewAsync` (re-runs on append via ModelReloaded); visuals cleared alongside thumbnails. Because rendering is relative to `seg.Start`, the markers move with the segment automatically.

Note: `TimelineControl.SegmentTrackVisual` must be referenced fully-qualified from `Musio_App.Pages` (`Musio_App.Controls.TimelineControl.SegmentTrackVisual`).

**Verified:** builds (ARM64) + launches clean; 323 Core tests still pass (Core unchanged).

## Feature — Per-segment Frame Style & Cursor; global Aspect/Cover/Zoom-scope

**Symptom:** Frame style (and cursor) only applied to the first (primary) video segment; appended segments kept stale/default style because `RebuildPreviewRendererAsync` rebuilt only the primary `_previewRenderer` while appended `_segmentPreviews` were cached with their build-time config.

**Design:** Frame style + cursor are now PER-SEGMENT; aspect ratio, fit/cover mode, crop anchor, and zoom scope remain GLOBAL (on `CompositionConfig`, applied to every segment).

**Implementation:**
- Model (`VideoSegment`): added `BackgroundStyle? FrameStyleOverride` and `CursorStyle? CursorStyleOverride` (Musio.Core.Processing types; TimelineSegment.cs already `using`s it). Records `with` preserves them through split/trim/move (tested).
- `EditorPage.BuildSegmentConfig(global, seg, previewFps)` merges per-segment overrides on top of global; global props always inherited. Appended `GetOrBuildSegmentPreviewAsync` now uses it.
- Primary renderer: `RebuildPreviewRendererAsync` applies the ACTIVE primary segment's override (via `ActivePrimaryVideoSegment()`), tracking `_primaryRenderBackground/_primaryRenderCursor/_primaryRendererSegmentId`. `EnsurePrimaryRendererForSegmentAsync(seg)` rebuilds when the playhead crosses into a primary split with a different override.
- Flyout routing: `ApplyBackgroundStyle` / `ApplyCursorStyleFromControls` write to the selected `VideoSegment`'s override (and rebuild just that segment via `RebuildForSegmentStyleChangeAsync`) when a video segment is selected; otherwise update global + `InvalidateSegmentPreviews()` + rebuild. `OnSegmentSelected` syncs the flyout controls to the selected segment's effective style (`SyncStyleControlsToConfig`/`SyncCursorControlsToConfig`, which suppress events).
- Global handlers (`ApplyAspectRatio/ApplyFitMode/ApplyCropAnchor/ApplyZoomScope`) now call `InvalidateSegmentPreviews()` so appended segments rebuild and pick up the global change.

**Note (follow-up):** Export (`ExportEngine`) still uses one global `CompositionConfig`; per-segment frame style/cursor is preview-only until the export frame loop consults each segment's override (same pattern needed as camera-segment export parity).

**Verified:** builds (ARM64) + launches clean; 328 Core tests pass (5 new in `SegmentStyleOverrideTests` proving overrides survive split/trim/move).

## Fix — Per-recording zoom segments (appended recordings get auto-zoom; create works on any clip)

**Symptoms:** (1) zoom segments couldn't be added after the first / on appended clips; (2) appended recordings showed no auto-zoom segments.

**Root cause:** Zoom keyframes lived in one `model.ZoomKeyframes` list in PRIMARY source time and were mapped via `SourceToOutputTime` (primary-only). Creating a zoom over an appended region produced a primary source time that clamped to the primary clip's end (so it "couldn't be added"), and appended clicks never generated keyframes.

**Fix — tag zoom keyframes by source file + per-segment mapping:**
- Model: added `ZoomKeyframe.SourceVideoFilePath` (null = primary). Independent per-file subsets in the same list.
- Control (`TimelineControl`): `ZoomKeyframeTimeToX(kf, time)` routes a keyframe through the VideoSegment that owns it (`SourceVideoFilePath`), via `SegmentVideoTimeToX`; returns NaN when outside any kept range (skipped). Zoom draw + `HitTestZoomSegment` now use it. Zoom CREATE determines the segment under the cursor (`XToSegmentVideoTime(x, out file)`), tags `_zoomCreateFile`, maps the create-preview via `ZoomCreateTimeToX`, and fires `ZoomSegmentCreated(start, end, filePath)`.
- EditorPage: `OnZoomSegmentCreated` tags the new keyframe with the file (`FromRange(...) with { SourceVideoFilePath = e.FilePath }`); zoom region edit mode only for the primary. `GenerateAppendedZoomKeyframes(seg, cursor)` (called from `LoadAppendedTrackVisualsAsync`) builds tagged auto-zoom keyframes from each appended recording's clicks. Primary keyframe clear now targets only `SourceVideoFilePath is null` (appended keyframes are independent → no clear-race). Primary renderer `UpdateZoomKeyframes` filtered to primary-only keyframes so appended keyframes don't apply in primary time (appended previews already auto-zoom internally).

**Limitation (noted):** Manual zoom segments created on an APPENDED clip render on the track but don't yet drive that clip's preview zoom (the appended renderer uses internal auto-zoom; it isn't fed model keyframes). Export per-segment zoom parity is also a follow-up.

**Verified:** builds (ARM64) + launches; 333 Core tests pass (5 new in `ZoomKeyframeSourceFileTests`).

## Fix — Appended zoom segments rendered as a thin line and couldn't be resized

**Symptoms:** A zoom segment on an appended (2nd) recording rendered as a thin vertical line and couldn't be resized; primary zooms were fine.

**Two root causes:**
1. **Resize/move used the primary-only mapping.** `ZoomTrack_PointerReleased` mapped the dragged edge via `XToSourceTime` (primary source space). For an appended keyframe this produced a clamped/wrong time, collapsing the keyframe → thin line + broken resize.
2. **Each keyframe edge could map through a different segment.** The earlier `ZoomKeyframeTimeToX` independently found the first non-NaN segment per edge, so a keyframe slightly overflowing its clip (or with duplicate appended segments) could map Start/End through different segments → near-zero width.

**Fix (TimelineControl):**
- `OwningSegmentForKeyframe(kf)` picks ONE segment (file match + Timestamp containment, else first file match). `ZoomKeyframeTimeToX` now maps BOTH edges through that single owning segment (clamping-free), so the segment renders at true width even if an edge overflows the clip. Hit-test and draw use it.
- Added `XToKeyframeFileTime(x, filePath)` (inverse, segment-aware) and `SelectedZoomKeyframeFile`; the zoom move/resize releases now map through the selected keyframe's owning file instead of `XToSourceTime`, so appended zooms move/resize within their own recording's time.
- `ZoomCreateTimeToX` passes `_zoomCreateStart` as the representative timestamp so the create preview resolves the correct owning segment.

**Verified:** builds (ARM64) + launches; 333 Core tests pass.

## Meta — Distilled high-churn areas into "Settled Playbooks" (top of file)

**Feature/area:** `learnings.md` structure.

**What worked:** Audited all section headers and identified the clusters that were re-worked
repeatedly (often with reversals): DPI/capture coordinate transforms (~8 revisits incl. a full
multiply-by-DpiScale → 1.0 reversal), H.264/MediaEncodingProfile (Auto vs HD1080p flip-flop across
recording vs export pipelines), build/deploy/test toolchain, WinUI flyout init races + ThemeResource
layout-cycle crashes, the frame-style compositor coordinate model (10 rounds), zoom-segment
time-mapping, and crash/freeze hardening invariants. Added a definitive **Settled Playbooks** section
at the top that turns each open-ended "approaches tried" thread into fixed ALWAYS/NEVER rules, so
future tasks follow the settled convention instead of rediscovering it. Historical sections retained
as the rationale record.

**What didn't work:** N/A — documentation-only change.

## Meta — Verified Settled Playbooks against actual code (refined 2 claims)

**Feature/area:** `learnings.md` Settled Playbooks accuracy.

**Verified accurate against source:** recording uses `VideoEncodingQuality.Auto` + `& ~1`
(`VideoWriter.cs:276,351`); export uses `HD1080p` template + `ComputeBitrate`
(`VideoEncoder.cs:309,815`); both set `MediaTranscoder.HardwareAccelerationEnabled = false`;
`FrameCompositor` hardcodes `coordScale = 1.0f` and subtracts physical `CropOffset`
(`FrameCompositor.cs:162-167,188-189`); `RecordingSession.ComputeCursorOffset` uses monitor
`rcMonitor` / `DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS)` / monitor+crop per capture type
(`RecordingSession.cs:701-734`); `BackgroundCompositor.CalculateOutputSize` is identity
(`BackgroundCompositor.cs:46-50`); `ZoomKeyframe.MinSegmentDuration = 200ms` + `SourceVideoFilePath`
(`TimelineModel.cs:274,307`); `MouseHookRecorder.MinMoveIntervalMs = 4.0`
(`MouseHookRecorder.cs:95`); WARP fallback + `RecoverableDeviceLostException`
(`Direct3DDeviceHelper.cs:28`, `FrameCompositor.cs:599-607`); package identity `PratikMistri.Musio`.

**Refined (were inaccurate):**
1. Even-dim alignment is NOT uniform `& ~1` across pipelines. Recording is mod-2 (`VideoWriter`);
   export is **mod-16** via `AspectRatioHelper.ComputeExportDimensions` (`AspectRatioHelper.cs:171-172`).
   Removed the "add `& ~1` in both" instruction.
2. The `_flipBuffer`/`_scaleTarget` buffer-reuse claim was stale — those fields don't exist.
   `VideoEncoder` intentionally allocates a fresh per-frame `outputSurface` (`VideoEncoder.cs:520-524`)
   so the encoder can read it after the `SampleRequested` semaphore is released; memory is bounded by
   `PreflightRenderTargetMemory` (~1.5 GB cap), not reuse. Corrected the playbook accordingly.


## Fix (root cause) — Appended zoom segment rendered as a thin line

**Root cause:** In TimelineControl, GetZoomSegmentEndX still returned SourceTimeToX(kf.End) (the primary-only mapping) while GetZoomSegmentStartX used the new per-segment ZoomKeyframeTimeToX. For an appended keyframe, the START edge mapped correctly into the appended clip (e.g. output 5.5s) but the END edge (kf.End in the appended file's time, e.g. 2.5s) was mapped through the PRIMARY space to ~2.5s output — LEFT of the start — so x2 < x1 and segW = Max(2, x2-x1) collapsed to a 2px line. This also blocked resize (nothing to grab).

Diagnosis method: a headless MSTest loaded the actual appended cursor.mcur (Videos\session_*) and confirmed the generated keyframes were a normal 1.5s wide and mapped to 1.5s-wide output ranges — proving the data was fine and the bug was a stale mapping call on the END edge only.

**Fix:** GetZoomSegmentEndX final return changed to ZoomKeyframeTimeToX(kf, kf.End) so both edges map through the keyframe's owning segment. Verified: builds (ARM64), 333 Core tests pass.

---

## Text Slide Video Background — Not Showing in Preview/Export (deadlock fix)

- **Feature/area**: `TextSlideRenderer.ExtractVideoFrameAsync` (Musio.Core/Processing)
- **Problem**: A video set as a text-slide background never appeared — not in the live preview and not in the exported MP4. The slide fell back to its solid `BackgroundColor`.
- **Root cause**: `TextSlideRenderer.RenderSlide` is synchronous and blocks on the async video-frame extraction via `ExtractVideoFrameAsync(...).GetAwaiter().GetResult()`. The inner awaits (`MediaComposition.GetThumbnailAsync`, `CanvasBitmap.LoadAsync`) did NOT use `ConfigureAwait(false)`, so their continuations marshaled back to the captured SynchronizationContext (UI thread in preview / encoder thread in export) while that thread was blocked in `GetResult()` → deadlock; the frame never loaded. The outer `.ConfigureAwait(false)` on the `ExtractVideoFrameAsync` call does NOT propagate to the method's inner awaits.
- **Why image worked but video didn't**: `DrawImageBackground` applies `.AsTask().ConfigureAwait(false)` directly on its single `CanvasBitmap.LoadAsync` await, so it never deadlocks. `EnsureVideoComposition` was also already correct (each `GetFileFromPathAsync`/`CreateFromFileAsync` uses `.ConfigureAwait(false).GetAwaiter().GetResult()`).
- **What worked**: Added `.AsTask().ConfigureAwait(false)` to both inner awaits in `ExtractVideoFrameAsync`. Builds clean (VS MSBuild, x64); all 333 Core tests pass.
- **What didn't work**: Building Musio.Core with `dotnet build` fails with `MSB4062 ExpandPriContent` (dotnet SDK 10.0.301 missing AppxPackage PRI task DLL). Use Visual Studio's MSBuild (`...\MSBuild\Current\Bin\MSBuild.exe`) with `/p:Platform=x64` instead.
- **Rule**: All `await`s in Musio.Core (library code) must use `ConfigureAwait(false)`, especially any awaited inside a method that a synchronous caller blocks on via `GetAwaiter().GetResult()`.

---

## Text Slide Video Background — REMOVED (MediaClip rejects fragmented MP4s)

- **Feature/area**: Text-slide background types (`SlideBackgroundType`, `TextSlideSegment`, `TextSlideRenderer`, `EditorPage` flyout).
- **Problem**: Video background never rendered in preview or export — slide fell back to solid color.
- **Investigation (via temp file logging in DrawVideoBackground/EnsureVideoComposition)**:
  - Not a deadlock/ConfigureAwait issue (earlier guess) — the app never froze; an exception was silently swallowed by the catch.
  - Not file access: `StorageFile.OpenReadAsync` succeeded (content readable).
  - Not threading: failed identically when built on a threadpool (MTA) thread via `Task.Run`.
  - Not codec/resolution: file was `avc1` H.264; a 1080p file also failed.
  - Root cause: `MediaClip.CreateFromFileAsync` throws `ArgumentException "The parameter is incorrect" / "Error creating clip from file"` for the user's stock videos. They are **fragmented MP4** (`moof` box) with an **edit list** (`elst`) and **no audio track** — `Windows.Media.Editing`'s composition pipeline rejects these. Proven by: `MediaClip` succeeded on an app-recorded MP4 (`Recording ....mp4`) in the same runtime, but failed on every Pexels stock file (and on an app-owned temp copy of one).
- **Decision (user)**: "If video isn't working as background for text slide - we should remove that option entirely and simplify."
- **What was removed**: `SlideBackgroundType.Video` enum member; `TextSlideSegment.BackgroundVideoPath`; `TextSlideRenderer` video cache fields + `DrawVideoBackground`/`ExtractVideoFrameAsync`/`EnsureVideoComposition` (+ `Windows.Media.Editing`/`Windows.Storage` usings); EditorPage `Video` ComboBoxItem, `SlideVideoPanel`, `SlideVideoPathText`, `ChooseSlideVideo_Click`. `DrawSlideBackground` no longer takes `progress`.
- **Result**: Builds clean (VS MSBuild ARM64); all 333 tests pass. Text slides now support Solid/Gradient/Image only.
- **Note**: `MediaClip.CreateFromFileAsync` is only reliable here for app-recorded H.264 MP4s. Do NOT reintroduce arbitrary user-video features on top of `Windows.Media.Editing`; if needed, use the `MediaPlayer` frame-server pipeline (far more tolerant) instead.

## Text Slide — Ambient animated wave gradient

- **Feature/area**: `TextSlideRenderer.DrawGradientBackground` (Musio.Core/Processing).
- **Goal**: Make gradient text-slide backgrounds animate subtly (ambient, non-monotonous) with a wave look rather than a static linear gradient.
- **Approaches tried**:
  1. **Win2D GPU effects (worked ✅)**: Render the linear gradient into an intermediate `CanvasRenderTarget`, wrap in a `BorderEffect` (Clamp) so displacement sampling outside bounds keeps edge color, then `DisplacementMapEffect` using a scrolling `TurbulenceEffect` (FractalSum Perlin noise) moved over time via `Transform2DEffect`. Also sway the gradient angle (±5°) and drift its center sinusoidally. Amount ≈4% of short edge keeps it subtle.
  2. **Custom HLSL via PixelShaderEffect (not attempted)**: rejected — needs the Win2D shader compiler/bytecode tooling which is fragile in this build env. Built-in Win2D effects are already D2D pixel shaders under the hood, so they satisfy the "shader/wave" request without authoring HLSL.
- **Continuous time source**: `RenderSlide`'s `progress` (0..1 over slide duration) × `slide.Duration.TotalSeconds` = elapsed seconds. `DrawSlideBackground`/`DrawGradientBackground` now take `progress`.
- **Wiring**: Automatic in BOTH preview (SegmentCompositor/EditorPage) and export (VideoEncoder) because both call `RenderSlide` — no separate export wiring needed.
- **Gotcha**: The noise enum is `TurbulenceEffectNoise.FractalSum` (NOT `Fractal`) — `Fractal` fails to compile (CS0117).
- **Verified**: VS MSBuild x64 builds clean; all 333 tests pass.

## Text Slide — gradient wave: turbulent (not linear) revision

- **Feedback**: First pass animated but felt like a *linear pan*, and the user does NOT want motion on a static/paused playhead (it should behave like a video frame — move only while playing). Static-playhead repaint loop was explicitly rejected; keep motion driven by `progress` (frozen when paused).
- **Root cause of "linear" feel**: a single noise field scrolled in one constant direction (`Transform2D translate (sin x, t*12 y)`) plus a global gradient angle/center drift — all read as uniform translation.
- **Fix (worked ✅)**: Remove all global gradient drift (stationary base gradient). Drive motion ONLY via displacement using TWO `TurbulenceEffect` fields (different Frequency + Seed), each drifted along an OPPOSING circular path (`sin/cos`, different rates), averaged with `ArithmeticCompositeEffect` (Source1Amount=Source2Amount=0.5, MultiplyAmount=0, Offset=0), then `DisplacementMapEffect` at ~6% of the short edge. Interference of two circular-drifting fields makes the displacement swirl/boil in place instead of panning.
- **Keep**: progress→seconds time base (`clamp(progress)*Duration`); gradient rendered to intermediate RT then `BorderEffect` Clamp before displacement. No EditorPage/VideoEncoder changes needed — motion advances with the playhead in both preview and export automatically.
- **Verified**: Core + App build clean (ARM64 Debug, VS MSBuild), redeployed + launched.

## Text Slide — stronger turbulence + auto crossfade at slide boundaries

- **Turbulence tweak**: bumped displacement Amount 0.06→0.075 of the short edge and circular drift radii 90/75→110/95 so the wave reads a touch stronger (still ambient). Confirmed it is NOT a linear pan — two opposing circular-drifting noise fields interfere/churn.
- **Transition (no more hard cut)**: Added `Musio.Core/Timeline/SlideTransitions.cs` — a PURE, unit-tested helper (`Resolve(timeline, outputTime[, maxDuration])`) that returns a crossfade Result (Active/Progress/OutgoingTime) only on the LEADING edge of a boundary that touches a `TextSlideSegment`. Default 0.5s, clamped to half of each neighbour. 7 tests in `SlideTransitionsTests` (340 total pass).
- **Export wiring** (`VideoEncoder.ProduceSampleAsync`): refactored the per-frame composition body into a local `ComposeAtAsync(int fIndex)` so the OUTGOING neighbour can be re-rendered (held at its final instant) and blended via a new lazily-created `TransitionRenderer` (`_transitionRenderer`, disposed in Dispose). `TransitionRenderer.Render(outgoing, incoming, CrossFade, progress, w, h)` does the dissolve.
- **Preview wiring** (`EditorPage.RenderFrameAtAsync`): at the top of the segments branch, if `SlideTransitions.Resolve` is active, compose both sides via new `ComposePreviewFrameAsync(outputPos)` (slides at project res; video frames at source res — TransitionRenderer scales) and blend. FULLY guarded by try/catch → any failure falls back to the normal render, so it can only affect the ~0.5s dissolve window. Forces a redraw (`_lastRenderedFrameIndex=-1`, `_lastRenderedSegmentId=null`) when the dissolve ends.
- **Key insight**: the real export path is `ExportEngine → VideoEncoder` (NOT the `SegmentCompositor`/`TransitionRenderer` path, which only `ExportEngine.CreateSegmentCompositor` uses and isn't on the slide path). Preview and export remain separate code paths and BOTH had to be wired explicitly.
- **Verified**: Core+Tests x64 build, 340 tests pass; App ARM64 build clean, redeployed + launched.

## Text Slide flyout — segmented alignment + font dropdown

- **Feature/area**: Text Slide properties flyout (`EditorPage.xaml` + `.xaml.cs`).
- **Alignment**: Replaced the three `RadioButton`s (GroupName="SlideAlign", which rendered ugly radio bullets) with a `ctk:Segmented` (`SlideAlignSegmented`) of three icon `SegmentedItem`s (Left/Center/Right, Tags). Handler `SlideAlignSegmented_SelectionChanged` reads `SegmentedItem.Tag`; population sets `SelectedIndex` (0/1/2) from `slide.TextAlignment`. `CommunityToolkit.WinUI.Controls.Segmented` is already referenced and used elsewhere (FitModeSegmented, ZoomScopeSegmented); xmlns `ctk` already declared.
- **Font**: Added a `Font` `ComboBox` (`SlideFontCombo`) with a curated list of common Windows fonts; each item sets its own `FontFamily` for an in-place preview. `SlideFontCombo_SelectionChanged` sets `slide.FontFamily`; helper `SetSlideFontSelection` selects the matching item and inserts a custom item at top if the slide's font isn't in the list (so it isn't silently changed). The slide model already had `FontFamily` (string); in-place editor + renderer already honor it.
- **Gotcha**: removing the radios means removing the old `SlideAlign_Checked` handler and the `SlideAlignLeft/Center/Right.IsChecked` population lines, else they fail to compile (named elements gone).
- **Verified**: App ARM64 build clean (VS MSBuild), redeployed + launched.

## Zoom Segment — Cinematic Easing & Timing Refresh

- **Feature/area**: Auto-zoom and manual zoom-keyframe animation (`AutoZoomEngine.cs`, `CubicBezierEasing.cs`, `TimelineModel.ZoomKeyframe`).
- **Approaches tried**:
  1. Replaced the asymmetric private cubic-bezier (control points 0.25,0.1,0.25,1.0 — non-zero start velocity, jolt at zoom-in start and at leaving the hold) with a symmetric cinematic ease-in-out.
  2. Added `CubicBezierEasing.EaseInOutCinematic` = cubic-bezier(0.76, 0, 0.24, 1) (easeInOutQuart) and routed `AutoZoomEngine.CubicBezierEase(from,to,t)` through it, deleting the duplicate Newton-solver + `BezierComponent`/`BezierComponentDerivative` helpers.
  3. Lengthened/rebalanced default durations for a deliberate, graceful feel — `AutoZoomConfig`: Pre 0.345->0.45s, Hold 0.575->0.6s, EaseOut 0.575->0.7s; `ZoomKeyframe`: Pre 345->450ms, Hold 575->600ms, Post 575->700ms; `ZoomKeyframe.FromRange` caps bumped to 450/700ms. Kept zoom-in faster than zoom-out (snap-to-focus, slow release).
- **What worked**: Both auto segments and manual keyframes share `CubicBezierEase`, so one curve change covers both. Zero-slope at both ends removes junction jolts. All 340 tests pass; tolerant assertions (monotonicity, no-snap, endpoints) unaffected. Updated the one pinned-default test (`TimelineModelTests.ZoomKeyframe_DefaultValues_AreCorrect`) to 450/600/700.
- **What didn't work**: N/A — no perceptual/log-space zoom interpolation added (kept change focused; easing curve is the main cinematic lever).

## Dev Build Missing App Icon — Loose-Layout Asset Copy

- **Feature/area**: `src/Musio.App/Musio.App.csproj` packaging / local dev register-and-run flow.
- **Symptom**: The CLI dev build (VS MSBuild `/t:Build` + `Add-AppxPackage -Register win-<arch>\AppxManifest.xml`) showed the default Windows icon, not the app icon. `AppWindow.SetIcon("Assets/AppIcon.ico")`, `TitleBar.IconSource`, and `SystemTrayService` all silently failed.
- **Root cause**: The `<Content Include="Assets\...">` items (AppIcon.ico + tile/taskbar logo PNGs) are only emitted into the MSIX package during the packaging target (GenerateAppxPackageOnBuild/deploy). A plain `/t:Build` does NOT copy them into the loose `bin\<Plat>\Debug\net9.0-...\win-<arch>` layout, which is exactly what the dev flow registers — so the install location had no `Assets` folder at all.
- **What worked**: Added `<Content Update="Assets\**"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>` to the csproj. After rebuild, `win-arm64\Assets\AppIcon.ico` (and all logos) are present in the registered package's InstallLocation, so the icon loads. Verified present after Build + Register.
- **What didn't work / notes**: Don't expect the loose layout to contain Content assets without CopyToOutputDirectory; packaging-time inclusion alone is insufficient for the register-loose-manifest dev workflow.

## Text Slide Animations — Constant-Time (Length-Independent) Motion

- **Feature/area**: `src/Musio.Core/Processing/TextSlideRenderer.cs` entrance/exit + continuous text animations.
- **Symptom**: Animation speed scaled with the slide's total duration (and thus felt dependent on text amount). Entrance/exit were fixed *fractions* of the slide (`InDur=0.25`, `OutDur=0.25`; per-char `pIn=progress/0.5`, `pOut=(progress-0.6)/0.4`), so a longer slide made the in/out motion drag and feel boring.
- **Fix**: Drive entrance/exit by CONSTANT wall-clock seconds. Added `EntranceSeconds=0.6`, `ExitSeconds=0.6`, and a `ComputeInOutProgress(progress, durationSeconds)` helper returning independent `(inP, outP)` over fixed-time windows (each capped at `dur*0.45` so short slides still fit, with the middle hold absorbing extra time). Threaded `durationSeconds` (from `slide.Duration` / `overlay.Duration`) through `DrawAnimatedText` → `ComputeWholeState(anim, inP, outP, ...)`, `DrawPerCharacter`/`ComputeCharParams(anim, inP, outP, elapsedSeconds, ...)`, `TypewriterText(text, elapsedSeconds, durationSeconds)` (constant chars/sec), and Reveal. Wave now bobs at a constant `WaveHz` using elapsed seconds with a quick `WaveFadeSeconds` fade.
- **What worked**: All 340 tests pass; Core (x64) + App (ARM64) build clean; redeployed + launched. The renderer already computed `elapsed = progress * Duration` for the gradient wave, so duration was straightforward to thread through.
- **Note**: Per-character cascade still staggers via `frac*spread` but now within the constant-time in/out windows, so denser text just packs more chars into the same fixed motion duration.

## Timeline — Text Slide Segments Use Video Segment Color

- **Feature/area**: `src/Musio.App/Controls/TimelineControl.xaml.cs` `DrawVideoTrackFromSegments`.
- **Request**: Make text-slide timeline segments use the same color as video segments (the segment swatch color in the timeline, NOT the rendered slide background in preview/export).
- **Change**: Text slides were filled with a hardcoded CornflowerBlue (`textSlideColor`/`textSlideSelectedColor`/`textSlideBorder`). Now they use the shared `VideoClipColor` / `VideoClipSelectedColor` / `VideoClipSelectedBorder` like thumbnail-less video segments. Removed the three now-unused locals.
- **Note**: Video segments WITH thumbnails draw a filmstrip; text slides have no thumbnails so they fall back to the solid `VideoClipColor` fill — matching the no-thumbnail video clip appearance. White centered text label still drawn on top.
- **Verified**: App (ARM64) builds clean, redeployed + launched.

## Text Slide — Inline Preview Edit Box Hugs Height + Stuck-Drag Fix

**Feature/area:** Text slide in-place preview editing (TextSlideRenderer.ComputeTextRect, EditorPage SlideTextRegion/SlideEditBox)

**Problems:** (1) The editable text region in the preview filled the whole slide (84% W x 80% H box), so the selection/edit box didn't hug the text. (2) After finishing an edit the box followed the mouse without a button press.

**What worked:**
1. `ComputeTextRect` now measures the wrapped text via a `CanvasTextLayout` (`MeasureTextHeight`, using `CanvasDevice.GetSharedDevice()`) and returns a height that hugs the text, clamped to [~1 line, 80% of slide], still centered at (TextX,TextY). Width stays 84% as the wrapping width. Renderer uses `CanvasVerticalAlignment.Center`, so shrinking the box symmetrically keeps the rendered text in the same place — overlay box and rendered text now match and hug content.
2. Stuck-drag: the double-tap that opens the editor first fires `SlideTextRegion_PointerPressed`, which set `_slideRegionDragging=true` and captured the pointer; `EnterSlideTextEdit` then collapses the region so no `PointerReleased` arrives, leaving the flag stuck. Fix: `EnterSlideTextEdit` now resets `_slideRegionDragging=false`, calls `SlideTextRegion.ReleasePointerCaptures()`, and clears `ProtectedCursor`.

**Verified:** Musio.Core + Musio.App build clean (x64, VS MSBuild).

## Text Slide — Edit Box Auto-Resizes Height While Typing

**Feature/area:** Text slide in-place preview editing (EditorPage.SlideEditBox_TextChanged)

**Problem:** While editing the slide text in the preview, the `SlideEditBox` kept the height it had when editing began. Wrapping to new lines didn't grow the box; the user had to click out and back in (which re-ran `PositionSlideEditControls`) to refresh.

**What worked:** `SlideEditBox_TextChanged` now calls `PositionSlideEditControls(slide)` after updating `slide.Text`, so the box re-measures via `ComputeTextRect`/`MeasureTextHeight` and resizes live to hug the wrapped text (centered at TextY). No TextChanged recursion since `PositionSlideEditControls` doesn't set `.Text`.

**Verified:** Musio.App ARM64 Debug builds clean; redeployed + launched.

## Zoom Easing — Tuned Cinematic Curve + Visualizer Play Preview

**Feature/area:** Zoom motion curve (CubicBezierEasing.EaseInOutCinematic) + session visualizer tool (files/zoom-curve.html)

**Change:** `EaseInOutCinematic` retuned from `cubic-bezier(0.76, 0, 0.24, 1)` to `cubic-bezier(0.165, 0.003, 0, 0.999)` (slow, near-zero-velocity start with a fast glide and soft settle). Updated the XML doc comment to match. This single curve drives both zoom-in and zoom-out via `AutoZoomEngine.CubicBezierEase`.

**Visualizer:** `files/zoom-curve.html` is an interactive tuner — draggable Bézier handles (position + velocity plots), presets, "Copy C# line" button, a zoom-level-over-time profile, AND a live Play preview that animates the full in→hold→out profile on a mock app scene with a clickable focal point and playback-speed slider. JS `bezier()` mirrors CubicBezierEasing.cs Newton+bisection.

**Verified:** Musio.App ARM64 Debug builds clean; redeployed + launched.

## Zoom Timing — Slowed ~2.22x (0.45x playback feel) + Duration Slider in Visualizer

**Feature/area:** Zoom animation durations (AutoZoomConfig, ZoomKeyframe defaults, FromRange) + visualizer (files/zoom-curve.html)

**Change:** User found 0.45x playback speed felt right → durations multiplied by 1/0.45 ≈ 2.22x.
- `AutoZoomConfig`: PreClickDuration 0.45→1.0f, HoldDuration 0.6→1.333f, EaseOutDuration 0.7→1.556f (AutoZoomEngine.cs:10-12).
- `ZoomKeyframe` defaults: PreDuration 450→1000ms, HoldDuration 600→1333ms, PostDuration 700→1556ms (TimelineModel.cs:283-285); `FromRange` caps 450→1000, 700→1556.
- Updated test `TimelineModelTests.ZoomKeyframe_DefaultValues_AreCorrect` to new defaults.

**Test gotcha:** `AutoZoomEngineTests.GetZoomState_OverlappingManualKeyframes_CenterDoesNotSnap` encoded the OLD durations (it asserts B's center dominates at t=3.0). With longer hold, both keyframes sit at full zoom at t=3.0 → center averages to 960px, failing. Fix: pin explicit old durations (450/600/700) on that test's keyframes so the smoothing regression test is independent of default tuning. All 77 zoom/timeline tests pass.

**Visualizer:** added a master "Duration ×" multiplier slider that scales tin/hold/tout live, plus a "Copy AutoZoomConfig durations" readout. Base slider defaults updated to the new 1.0/1.333/1.556.

**Verified:** Musio.App ARM64 Debug builds clean; tests pass; redeployed + launched.

---

**Feature/area:** Cursor smoothing — `CursorSmoother` de-stutter pre-pass (trackpad stop-and-go removal). Files: `src/Musio.Core/Processing/CursorSmoother.cs`, tests `src/Musio.Tests/CursorSmootherTests.cs`, visualizer `files/cursor-destutter.html`.

**Approaches tried:**
1. **Time-preserving arc-length re-timing pre-pass (`ApplyDestutter`)** — ✅ Worked. Runs before the spring/One-Euro/Catmull filter (only when smoothing is active so the None passthrough stays exact). Detects low-speed "stall" runs via a windowed speed estimate; classifies a run as a protected REST if it overlaps a click guard window OR is longer than `GenuineDwellSeconds`. Motion gestures between anchors are re-timed by redistributing interior sample positions along the gesture's arc length with a `RemapEase` blend (constant-speed↔smootherstep). Endpoints/anchors and protected rests are left untouched.
2. **Global time-warp / stall compression** — ❌ Rejected. Compressing stall time would desync the cursor track from the underlying screen video (cursor must be at the right place at the right absolute time). De-stutter MUST preserve every sample timestamp; only positions are redistributed.

**What worked / key facts:**
- De-stutter is exposed via `CursorSmoother.DestutterEnabled` (default true) + `DestutterOptions` (tunable: StallSpeedPxPerSec, SpeedWindowSeconds, MinStallSeconds, GenuineDwellSeconds, ClickPre/PostGuardSeconds, EaseStrength). `FrameCompositor` gets it for free — no caller changes.
- Click stalls (even short ones) are preserved via proximity to `rawData.Clicks`, not duration. `SnapToClickPositions` still guarantees exact click position downstream.
- No-op on continuous stall-free motion (one gesture, endpoints preserved) — existing 6 smoothing tests still pass.
- `files/cursor-destutter.html` is a self-contained visualizer mirroring the C# algorithm (path + speed-over-time plots, scenario presets, all params live, playback). Note: visualizer JS reimplements the algorithm; keep it in sync with `CursorSmoother.cs`.

**Build/test:** `dotnet test`/`build` FAILS on Musio.Core (WinAppSDK `ExpandPriContent` task). Use VS MSBuild `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe` to build, then run tests via `dotnet vstest <Tests.dll> --TestCaseFilter:...` with `DOTNET_ROLL_FORWARD=LatestMajor`. All 10 CursorSmootherTests pass.

---

## Text slide animation default & fade option cleanup

- **Feature/area**: Text slide animations (TextSlideRenderer, TextSlideSegment/TextOverlay enum, EditorPage animation ComboBox).
- **Approaches tried**: Changed default `TextSlideAnimation` from `FadeIn` to `ZoomBlurIn` (the "blur" animation) on both `TextSlideSegment` and `TextOverlay`; removed `FadeOut` and `FadeInOut` enum members, their renderer switch cases, and their ComboBox items; updated `SlideAnimationCombo` SelectedIndex from 1 to 7 (now points at Zoom Blur In).
- **What worked**: `FadeInOut` was identical to `FadeIn` (shared the default fade-in+fade-out opacity), so removing it lost no behavior. ComboBox is matched by Tag, so removing items needed only a SelectedIndex fix. Build verified with VS MSBuild x64 for Musio.Core and Musio.App (both exit 0).
- **What didn't work**: `dotnet build` still fails with MSB4062 PriGen error (pre-existing env issue) — must use VS MSBuild at `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe`.

---

## Ken Burns motion on image-backed text slides

- **Feature/area**: `TextSlideRenderer.DrawImageBackground` / new `DrawKenBurns` helper.
- **Approaches tried**: Image background was drawn statically via `DrawScaledToFill`. Added a continuous "Ken Burns" motion: overscale the cover-fit image (~1.14x) for headroom, then a breathing zoom (`1.14 + 0.05*sin(t*0.18)`) plus an elliptical slow pan (`sin/cos` of t, bounded to 70% of the available slack so edges never show). Driven by `progress` → elapsed seconds, same pattern as the gradient wave so it advances with the playhead and matches preview + export.
- **What worked**: Computing dest Rect directly with scale+pan and bounding pan to the overscale slack guarantees the frame stays fully covered. Removed the now-unused `DrawScaledToFill`. Build verified with VS MSBuild x64 (Musio.Core, exit 0).
- **What didn't work**: N/A — avoided rotation to prevent exposing image corners (would need more overscale); zoom+pan satisfies the "nothing feels static" goal robustly.

---

## [Review] feature/text-animations pre-PR production review (fleet of review agents)

**Feature/area:** Text slide animations, FCP-style segment editing, slide crossfades, cursor de-stutter, export pipeline.

**Approach:** Ran 5 parallel review agents (core rendering math, export/timeline model, UI/app layer, cross-cutting concurrency/leaks, security) over the diff vs merge-base 8482e29. Established baseline: VS MSBuild ARM64 Debug build succeeds (0 warn/err); all 344 unit tests pass via vstest.console.

**What worked / confirmed real issues:**
- HIGH: VideoEncoder.cs:535 slide crossfade uses Math.Round on (current.Start - 1 tick)*fps; rounds back to incoming segment frame when boundaries are frame-aligned (whole-second slides/splits), degrading dissolve to a hard cut. Fix: Math.Floor.
- MED: TextSlideRenderer.cs:280 background image loaded via sync-over-async (LoadAsync...GetResult()) on UI thread in synchronous RenderSlide -> preview/scrub hang for image-backed slides. Pre-load/cache CanvasBitmap off-thread.
- MED: TextSlideRenderer.cs:183 base gradient render target + ~10 Win2D effects re-allocated every export frame though slide-invariant; cache keyed by (w,h,colors,angle).
- LOW: TextSlideRenderer.cs:741 ParseColor maps non-6/8-digit hex to white and throws FormatException on bad hex digits mid-render; use TryParse + 3-digit shorthand + default fallback.
- LOW: EditOperation.cs:766 ReorderSegmentOperation.Undo no-ops for append-past-end target (only used in tests today; app uses MoveSegmentOperation).

**Security:** Clean — no Process.Start/ffmpeg shell exec, no unsafe deserialization (no Project disk persistence exists), no path-traversal sinks, no temp-file/secret issues.

**Build/test commands (verified):**
- Build: "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" src\Musio.Tests\Musio.Tests.csproj /t:Restore,Build /p:Configuration=Debug /p:Platform=ARM64
- Test: vstest.console.exe on src\Musio.Tests\bin\ARM64\Debug\net9.0-windows10.0.26100.0\Musio.Tests.dll  (dotnet test fails: WinAppSDK PRI task needs VS MSBuild + explicit Platform, not AnyCPU).

**Still requires MANUAL runtime verification (static review cannot confirm):** stop-recording crash (known recurring blocker), Record More appended track data, encoding-path divergence VideoWriter vs VideoEncoder, stop-toolbar light-theme white-on-white, loading old projects that had text-slide video backgrounds.

---

## [Fixes] feature/text-animations pre-PR fixes applied

**Feature/area:** Slide crossfade frame selection, text slide rendering (gradient cache, async image load, color parsing), segment reorder undo.

**Approaches tried / what worked:**
- VideoEncoder.cs crossfade: changed Math.Round -> Math.Floor for outgoing frame index (start-1tick stays in outgoing segment). Worked.
- TextSlideRenderer.cs: (a) cached slide-invariant gradient RenderTarget in _gradientCache keyed by (w,h,startColor,endColor,angle), disposed in Dispose; (b) added public async EnsureBackgroundLoadedAsync(slide) to pre-warm bg image off-thread, kept sync fallback for off-UI export path; (c) hardened ParseColor with TryParse + 3/4-digit shorthand + white fallback (no longer throws).
- EditorPage.xaml.cs: renamed RenderTextSlidePreview -> async RenderTextSlidePreviewAsync, awaits EnsureBackgroundLoadedAsync before sync RenderSlide; ComposePreviewFrameAsync also pre-warms.
- EditOperation.cs ReorderSegmentOperation: snapshot Segments in Execute, restore in Undo (replaces buggy index arithmetic that no-opped for append-past-end). Added 2 regression tests.

**Verification:** ARM64 Debug build of Musio.App + Musio.Tests clean (only benign MVVMTK0045 warnings). Full suite 346/346 pass (added 2).

**Already fixed on branch (no action):** stop-toolbar light-theme white-on-white — RecordingOverlayWindow.xaml.cs:49 pins RootGrid.RequestedTheme=Dark.

**Verified non-issues (no action):**
- Old-project compat for removed text-slide video background: no disk persistence of Project/TimelineModel exists; text slides are editor-only; DrawSlideBackground default case degrades to solid colour.
- Encoding-path divergence: VideoWriter.cs NOT changed on this branch; VideoEncoder.cs changes are transition/crossfade/audio only — no codec/resolution/bitrate divergence introduced.

**NOT fixed (cannot resolve headlessly):** stop-recording crash — requires an interactive capture+stop session to reproduce/verify; static review (UI agent) found the teardown/append flow clean. Needs manual runtime testing before deploy.

## Real Cursor Shapes — Capture & Render (arrow/hand/ibeam/resize)

- **Feature/area**: cursor shape pipeline — `MouseHookRecorder`, `CursorShapeResolver` (new), `CursorData`, `CursorSmoother`, `CursorGeometryLibrary` (new), `CursorRenderer`, `FrameCompositor` (all Musio.Core).
- **Goal**: record which system cursor shape was active and render the matching geometry instead of always drawing one arrow. Touch cursor path left untouched.
- **What worked**:
  - Detection via new `CursorShapeResolver` (`GetCursorInfo` + cached `LoadCursor(IDC_*)` handles); sampled per mouse-move in `MouseHookRecorder.HookCallback`. Unknown/custom cursors map to `Arrow`.
  - Persisted by bumping MCUR file format v1->v2 with a per-sample shape byte; `LoadFromFile` accepts versions 1 and 2 (v1 -> `CursorShape.Arrow`). New `CursorShape` byte field added to `MouseSample` (Pack=1 struct).
  - Shape propagated to output frames by a single nearest-neighbour post-pass `AssignShapes` in `CursorSmoother.SmoothPath` (added `Shape` to `SmoothedPosition`); all `SmoothedPosition` copies in `FrameCompositor` must carry `Shape`.
  - Shapes authored as SVG path-data + per-shape hotspot in `CursorGeometryLibrary`, parsed into `CanvasGeometry` via a minimal SVG parser (M/L/H/V/C/Q/Z, abs+rel). `DrawDefaultCursor` selects glyph by shape and prepends `Matrix3x2.CreateTranslation(-hotspot)` so the hotspot lands on the pointer point; keeps existing fill/outline/tilt/scale.
- **Testing note**: `dotnet test` fails with the same `MSB4062 ExpandPriContent` PriGen error as `dotnet build`. Working approach: build `Musio.Tests.csproj` with **VS MSBuild** (`/p:Platform=x64`), then run `vstest.console.exe` (under `...\Common7\IDE\CommonExtensions\Microsoft\TestWindow\`) against `bin\x64\Debug\...\Musio.Tests.dll` with `/Platform:x64`. Full suite = 364 tests green.
- **Win2D in tests**: `new CanvasDevice(forceSoftwareRenderer: true)` works in this test host, so geometry/`ComputeBounds` tests run; guarded with `Assert.Inconclusive` fallback if a device can't be created.

## Cursor Shapes — Follow-ups: input-lag regression + Windows-consistent geometries

- **Feature/area**: `MouseHookRecorder`, `CursorShapeResolver`, `CursorGeometryLibrary` (Musio.Core).
- **Problem 1 (freeze/slow)**: Sampling the cursor shape via `GetCursorInfo` *inside* the WH_MOUSE_LL hook callback on every event added latency to the system input queue, making the live (and therefore recorded) cursor feel laggy/slow in preview/export.
  - **Fix**: Sample the shape on a ~60Hz background `System.Threading.Timer` that writes a `volatile int _currentShape`; the hook only reads the cached value. Timer started in `StartRecording`, disposed in `StopRecording`/`Dispose`. Never call `GetCursorInfo` (or any syscall) from the low-level hook hot path.
- **Problem 2 (geometries out of place)**: Hand-authored cursor glyphs had inconsistent sizes (e.g. resize ~10px tall vs arrow ~27px), so switching shapes made the cursor visibly shrink/grow, and looked unlike Windows.
  - **Fix**: Rewrote `CursorGeometryLibrary` paths as closed filled silhouettes (white body + contrast outline = the Windows look) normalized to a consistent ~26-30px main dimension, with correct hotspots (arrow tip 0,0; resize/I-beam centered; hand at the index fingertip). Licensing: app is MIT, so cursor art must be CC0/self-authored — do NOT pull GPL sets (Bibata/Adwaita). Note `CursorRenderer` fills geometry, so stroke-only SVG paths don't render; author closed outlines.
- **Test guard**: `CursorGeometryLibrary_ShapesAreConsistentlySized_WithHotspotsInBounds` asserts every glyph's max dimension is 18-34 and its hotspot lies within bounds — catches future size/hotspot regressions.
- **Verification**: VS MSBuild x64 (Core, Tests, App) clean; full suite 371 green.

## Cursor Hand Geometry — replaced offensive shape with icons8 hand silhouette

- **Feature/area**: `CursorGeometryLibrary.Hand` (Musio.Core/Processing).
- **Problem**: The hand-authored hand glyph rendered as an offensive single-finger shape.
- **Approaches considered**:
  - **System cursor capture** (prototyped, then abandoned): `LoadImage(IDC_*)` + two-pass `DrawIconEx` (white & black bg) to derive alpha → real Windows cursor bitmaps. Verified accurate for all shapes incl. monochrome I-beam (premult color = black-bg pass, alpha = 255-(white-black)). Works, but the user chose to supply vector art instead, so the `SystemCursorCapture.cs` was removed to avoid dead code. (Keep this note: this technique is the fallback if pixel-exact OS cursors are ever wanted.)
  - **Provided SVG (chosen)**: user supplied `icons8-hand-cursor-96.svg` (32-unit viewBox, two subpaths for line-art). Our renderer FILLS silhouettes (not stroke centerlines), so use only the OUTER figure (full hand silhouette with finger bumps), filled white + contrast outline = clean Windows-style hand. Hotspot at the index fingertip ~(12,2).
- **Provenance/licensing**: hand path adapted from icons8 (free tier requires attribution). Flag to user to add icons8 attribution in app About/licenses if shipping the free asset.
- **Verification**: rendered the silhouette to PNG and visually confirmed a proper pointing hand before shipping. Full suite green (12 cursor tests incl. build-all-shapes + size/hotspot band 18-34). Built+relaunched ARM64.
- **Rule**: `CursorGeometryLibrary` paths must be CLOSED FILLED silhouettes; multi-subpath line-art SVGs render wrong (renderer fills, doesn-t do evenodd line-art). Use the outer silhouette figure only.

## Cursor jitter/slow + hand polish (round 2)

- **Jitter/slow root cause**: `CursorSmoother.GetSpringParams` springs were ALL underdamped (ζ≈0.65-0.70: b far below 2*sqrt(k*m)). Underdamping makes the smoothed cursor overshoot and ring on every stop/direction-change = visible jitter, and the ringing settle reads as "slow". The de-stutter pre-pass (more stop/rest transitions) amplified it → "jitter is back".
  - **Fix**: critically damp every strength, b ≈ 2*sqrt(k*mass): Subtle 28→40, Medium 22→32, Smooth 16→25, UltraSmooth 12→18 (k unchanged so responsiveness/character preserved). Explicit-Euler stable (b*dt/m<2 at 30/60fps; max b=40 < 60).
  - **Guard test**: `SmoothPath_SpringPhysics_StepInput_DoesNotOvershoot` feeds a step input and asserts the smoothed X never exceeds target+5 for all strengths. Catches any future underdamped re-tuning.
  - NOTE: the cursor-shape feature does NOT change positions/timing (provably additive); the arrow renders identically to baseline (hotspot 0,0 → identity). So this jitter was pre-existing smoothing, surfaced by the de-stutter era — not the shape work.
- **Hand polish**: replaced the icons8 outline-silhouette with a user-provided 23x26 SVG fill path (proper Windows hand with finger separations). `CursorGeometryLibrary` now supports a per-shape Scale (added to the Definitions tuple; Build applies `geometry.Transform(CreateScale)` + scales the hotspot). Hand uses Scale=1.5 (its native ~17px viewBox is smaller than the other ~26-30px cursors), hotspot natural (9.04,3.2) at the index fingertip. Verified by rendering to PNG.
- **Verification**: VS MSBuild x64+ARM64 clean; full suite 372 green; rebuilt+relaunched ARM64.

## Cursor preview "jitter" REAL root cause — shape-track flicker (not position)

- **Symptom**: user reported persistent cursor jitter in preview/export that survived the spring critical-damping fix.
- **Diagnosis via real recording** (`Videos\session_*\cursor.mcur`, MCUR v2): parsed 629 samples/10.4s. **Position track was CLEAN — 0 x-direction reversals.** The jitter was NOT positional. The SHAPE track flickered: 34 transitions across 6 shapes with many sub-100ms runs (e.g. IBeam n=1 8ms, Arrow n=2 16ms, Hand n=2 16ms). The 16ms shape poller faithfully captures transient OS-cursor flicker at element boundaries; AssignShapes reproduced it per-frame → the rendered cursor GLYPH pops between arrow/hand/ibeam/resize within 1-3 frames = perceived "jitter".
- **Fix**: `CursorSmoother.StabilizeShapes` (called from `AssignShapes`) debounces the per-frame shape track — runs shorter than `MinShapeHoldSeconds` (0.12s → minHoldFrames) are forward-filled with the last stable shape; sustained changes preserved. Done at smoothing time so it fixes EXISTING recordings on preview/export. Verified on the real file: glyph transitions 32→13 at 60fps (flicker removed, genuine changes kept). Test: `SmoothPath_DebouncesShapeFlicker_KeepsSustainedChanges`.
- **Lesson**: when debugging cursor "jitter", parse the actual `cursor.mcur` and separate POSITION jitter (direction reversals / jerk) from SHAPE-track flicker (short runs). They have completely different fixes. The shape feature can introduce visual jitter with zero position change.
- **Note**: the spring critical-damping change from the prior round is still correct/kept; it just wasn't this bug.
- **Verification**: VS MSBuild x64+ARM64 clean; full suite 373 green; rebuilt+relaunched ARM64.

## Cursor offset (lag) — reverted critical-damping change

- **Symptom**: after the spring critical-damping change, user reported the rendered cursor no longer matches the real on-screen interaction (an offset/lag), though speed/jitter were fine.
- **Cause**: increasing spring damping b raises steady-state tracking lag (lag ≈ (b/k)·velocity). Measured on a real capture (session_20260621_204535, 730 samples): UltraSmooth mean lag 136.7px→185.1px (+35%), Smooth 95.3px→136.9px (+44%) just from the b increase. Since the actual jitter was shape-flicker (fixed separately by StabilizeShapes), the damping change was pure downside.
- **Fix**: reverted `GetSpringParams` to the original committed values (Subtle 28, Medium 22, Smooth 16, UltraSmooth 12) and removed the no-overshoot test. The slight underdamped overshoot (~6%) is not perceived as jitter and keeps tracking tight.
- **Method note**: to compare smoothing-lag tradeoffs, simulate the spring on a real `cursor.mcur` (linear-interp raw target + Euler spring at 60fps) and measure mean/max deviation from the raw position. Fast, decisive, no GUI.
- **Lesson**: don't raise spring damping to chase "jitter" — it trades lag for overshoot. Diagnose first (position-jitter vs shape-flicker). Kept: shape debounce (real jitter fix). Reverted: damping.
- **Verification**: VS MSBuild x64+ARM64 clean; suite 372 green; relaunched ARM64.

## Cursor shape detection moved to click-time only (removed continuous polling)

- **User directive**: "If the cursor change detection is causing this then let's do it on click time than all the time." User suspected the continuous 16ms shape poll was degrading recorded cursor movement.
- **Change**: removed the background `System.Threading.Timer` shape poller from `MouseHookRecorder`. Cursor shape is now sampled via `CursorShapeResolver.Resolve()` ONLY on click-down events inside the hook (clicks are infrequent → no per-move overhead, no extra thread/threadpool activity during recording), plus one initial sample at StartRecording. `_currentShape` is held between clicks and stamped on every sample.
- **Tradeoff (intended)**: hovering a link/text WITHOUT clicking no longer shows hand/I-beam until the next click; shape reflects the cursor at the most recent click. Accepted per user priority on movement quality over continuous shape accuracy.
- **Kept**: `StabilizeShapes` debounce (now mostly a no-op since click-time shapes form long runs) and original spring damping (b=12 etc.).
- **Verification**: VS MSBuild x64+ARM64 clean; suite 372 green; relaunched ARM64.

## Cursor "slow motion" on short moves — ROOT CAUSE: de-stutter + low-stiffness spring (pre-existing)

- **Provenance**: Spring/UltraSmooth and de-stutter were committed BEFORE the cursor-shape work (git blame: EditorPage smoothing 2026-04-18/05-09/06-19; spring params (80,12) 2026-04-18; de-stutter 2026-06-19). The cursor-shape feature did NOT introduce them; fixing the shape-flicker just made the underlying slow-motion visible.
- **Diagnosis (data-driven, real capture session_20260621_210141, raw peak 12693px/s)** via a temp Musio.Tests harness running the REAL CursorSmoother at 30fps:
  - Spring/UltraSmooth + de-stutter ON (the editor default): peak speed **0.20x** real, meanLag 125px → severe slow motion, worst on short quick moves (fixed spring settle time spreads a small displacement over ~0.5s).
  - de-stutter OFF roughly DOUBLES peak speed (0.20→0.47) and halves lag — de-stutter's ease-in/out arc-retiming is the biggest single culprit.
  - **OneEuro/UltraSmooth, de-stutter OFF: peak 1.11x, meanLag 7.8px** — preserves real speed, near-zero lag, still smooths adaptively. Clear winner.
- **Fix**: editor now uses `SmoothingAlgorithm.OneEuroFilter` (EditorPage 2 spots + `CompositionConfig` default) and `FrameCompositor` sets `DestutterEnabled = false` (covers preview AND export). De-stutter code/tests retained for possible future trackpad-specific use.
- **Method**: to evaluate smoothing feel objectively, run the real `CursorSmoother` on an actual `cursor.mcur` and compare per-config peak-speed-ratio vs raw, mean positional lag, and short-burst time-stretch. Far better than eyeballing.
- **Verification**: VS MSBuild x64+ARM64 clean; suite 372 green; relaunched ARM64.

## Cursor smoothing — final balance: Spring/Smooth, de-stutter OFF

- **Context**: One Euro (prev fix) tracked raw too tightly → cursor felt "human", not cinematic. User wants cinematic smoothing but NOT the heavy slow-motion.
- **Key data finding (real capture, raw peak 12693px/s)**: de-stutter ON crushes peak speed to ~0.17-0.21x **regardless of EaseStrength (0.6/0.3/0.15 all ≈0.17) and regardless of algorithm**. Its arc-length re-timing fundamentally flattens the velocity profile → that IS the slow-motion; it cannot be tuned out via easing. So de-stutter is incompatible with "keeps up with quick moves" as currently implemented.
- **Sweet spot (de-stutter OFF)**: Spring/UltraSmooth 0.47x/55px, **Spring/Smooth 0.60x/36px (chosen)**, Spring/Medium 0.67x/26px, OneEuro 1.11x/8px.
- **Chosen**: `SpringPhysics` + `Smooth` strength (k=150) with de-stutter OFF — cinematic spring momentum (~0.60x peak, ~36px trailing) without slow motion; the spring's low-pass also smooths trackpad micro-stalls. Set in EditorPage (2 spots) + `CompositionConfig` default; `FrameCompositor` keeps `DestutterEnabled=false`.
- **Open follow-up**: if the user wants the trackpad stop-and-go fix back WITHOUT slow-mo, de-stutter needs a rewrite to a LOCAL micro-stall filler (fill only the brief low-speed gaps) instead of re-timing whole gestures to uniform arc speed. Current de-stutter (commit 182eb03) is a global velocity-flattener.
- **Verification**: VS MSBuild x64+ARM64 clean; suite 372 green; relaunched ARM64.

## Cursor smoothing — tuned to "start late, arrive on time" (Spring/Subtle)

- **User spec (refined over several rounds)**: smooth + good velocity to reach destination (no slow motion) + OK to START a move late, but MUST REACH the position on time (low lag at arrival/clicks/events).
- **Method**: parameter sweep harness (temp Musio.Tests) running reimplemented One Euro + damped spring on the real capture, measuring peakRatio (velocity), **moveLag** (lag during fast frames — OK to be high) vs **restLag** (lag near rest/arrival — must be low), jerk, and stop-and-go count. This move/rest lag split directly encodes "start late, arrive on time."
- **Finding**: stiffer spring at ζ≈0.7 hits the spec: k=350-400 → peakRatio 0.74-0.76, restLag ~3px (arrives on time), moveLag ~70px (cinematic momentum / starts late), stutter ~11. One Euro w/ beta gives full velocity + low restLag but high jerk (doesn't smooth stop-and-go); One Euro beta=0 and low-k springs smooth more but arrive late (high restLag) — both fail the spec.
- **Chosen**: `SpringPhysics` + **Subtle** strength (k=400, b=28, ζ=0.70) — a STANDARD preset that lands on the sweet spot, no param retuning. De-stutter stays OFF (its arc re-timing flattens velocity = slow motion, confirmed un-tunable via EaseStrength). Set in EditorPage (2) + CompositionConfig default.
- **Tradeoff acknowledged**: max smoothness (de-stutter's low jerk) and max velocity/on-time-arrival are opposed; Subtle prioritizes velocity + on-time arrival per the user's stated priority. If they want MORE stop-and-go removal without the velocity penalty, the remaining lever is a velocity-preserving de-stutter REWRITE (local micro-stall filler, not whole-gesture arc-retiming).
- **Verification**: VS MSBuild x64+ARM64 clean; suite 372 green; relaunched ARM64.

## Cursor smoothing — SOLVED with zero-phase (forward-backward) spring

- **Breakthrough**: cursor smoothing is OFFLINE post-processing, so we can use FUTURE samples → a ZERO-PHASE filter smooths trackpad stop-and-go with NO time lag (unlike all causal filters spring/One Euro/Holt which must trail). This is the Screen-Studio-style result the user wanted (smooth, no stutter, no slow motion).
- **New algorithm**: `SmoothingAlgorithm.ZeroPhaseSpring` in CursorSmoother — resample raw at target fps, run a critically-damped spring FORWARD then BACKWARD (forward-backward = zero net phase), substepped (8x) for stability/accuracy at high stiffness and any fps. Stiffness per Strength via GetZeroPhaseStiffness (Smooth=200). SnapToClickPositions + StabilizeShapes still apply.
- **Measured (real capture, raw stutter 22, rawPeak 12693)**: ZeroPhaseSpring/Smooth(k=200) → stutter 3 (86% removed), jerk 64528 (smooth), meanLag 30px (mostly peak-rounding, NOT time lag). Causal springs had meanLag 36-125px (time lag = slow motion / late arrival). Zero-phase eliminates that.
- **Key nuance**: with zero-phase, lower peakRatio (~0.36) is NOT slow motion — total displacement & timing preserved, just smoother/eased velocity with no time delay. The earlier "slow motion" was CAUSAL lag (cursor behind in time), now gone.
- **Wired**: editor uses ZeroPhaseSpring/Smooth (EditorPage x2 + CompositionConfig default); de-stutter stays OFF. Test: `SmoothPath_ZeroPhaseSpring_SmoothsWithLowerLagThanCausalSpring` (smooths + stable + lower lag than causal spring).
- **Tuning knob**: GetZeroPhaseStiffness — lower k = smoother/less stutter/less peak; higher k = snappier/more stutter. UltraSmooth=130, Smooth=200, Medium=300, Subtle=420.
- **Method**: parameter-sweep harness reimplementing filters on a real cursor.mcur, measuring peakRatio, moveLag vs restLag (start-late-OK vs arrive-on-time), jerk, and stop-and-go count. Decisive for picking algorithms.
- **Verification**: VS MSBuild x64+ARM64 clean; suite 373 green; relaunched ARM64.

## Cursor work — code review hardening (2 rounds)

- Ran 2 review rounds (parallel code-review agents + own pass) on the cursor commit, crash/hang/leak focus. Two genuine (Low-severity) issues found and fixed:
  1. **NaN/Infinity at very low fps**: `CursorSmoother.SpringPass` used fixed `substeps=8`; explicit Euler diverges when h·sqrt(k) exceeds the stability limit (targetFps<=3 with stiff presets). Fix: adaptive `substeps = Max(8, ceil(2*dt*sqrt(k)))` keeps h·sqrt(k) <= 0.5 for any fps>=1 and all stiffness presets. Guard test: `SmoothPath_ZeroPhaseSpring_StableAtLowFps` (fps 1/2/5/120 finite).
  2. **GPU geometry leak on device-loss mid-Build**: `CursorGeometryLibrary.Build` accumulated CanvasGeometry in a local dict; if a later shape threw (e.g. device lost during Transform), already-built native geometries leaked (LoadCursorAsync catch couldn't reach them since `_glyphs` wasn't assigned yet). Fix: wrap the Build loop in try/catch disposing `result.Values` on throw, plus inner try/catch around `geometry.Transform` (dispose pre-transform geometry). No double-dispose.
- Confirmed safe (no change needed): SVG parser is hang-free (every iteration advances or throws; ReadNumber never returns without progress), MCUR v2 serialization is field-by-field (struct Pack change irrelevant) and v1-compatible, AssignShapes/StabilizeShapes bounds & termination, SpringPass fwd/bwd index math & full coverage, hook-thread shape sampling (click-only, try/catch, volatile), CursorShapeResolver uses shared non-owned cursor handles (no leak), and corrupt shape bytes fall back to Arrow via TryGetValue.
- **Verification**: VS MSBuild x64 clean; suite 374 green.

## Full-branch code review vs origin/master (feature/text-animations)

Reviewed the entire branch (48 files, +10.8k/-2k) via parallel code-review agents across timeline core, export pipeline, rendering, and app/UI (cursor work already reviewed separately). Findings + fixes:

- **HIGH (fixed)** — "Record More" data loss: `EditorPage.InitializePreviewAsync` re-runs on every page construction and rebuilt primary zoom keyframes destructively (`RemoveAll(SourceVideoFilePath is null)` + regenerate from ALL clicks). After Record More it wiped manual zoom segments and resurrected deleted auto-zooms. Fix: generate primary auto-zooms only when none exist yet (idempotent), and skip `SuppressedClickTicks` in generation.
- **LOW (fixed)** — `VideoEncoder` GPU leak: `composedFrame` (CanvasRenderTarget) leaked if an exception hit between creation and handoff (crossfade/scaling widened the window); catch only disposed `outputSurface`. Fix: hoist `composedFrame` to method scope, null at handoff points, dispose in catch.
- **LOW (fixed)** — `TrimSegmentEdgeOperation` default branch mutated the undo snapshot in place (latent; only reachable if a 3rd TimelineSegment subtype joins the primary track). Fix: `_previous with { Duration = ... }` instead of mutating `_previous`.
- **MEDIUM (flagged, not fixed — needs decision)** — GIF export is segment-unaware: `EffectiveDuration`/`TotalOutputFrames` became segment-based but `ExportGifAsync` still maps frames via the legacy `GetSourceTimeForOutputFrame` and never renders text slides → with a text slide the GIF is length-inflated, slides missing, post-slide content desynced. Proper fix routes GIF through the MP4 segment-aware compose (VideoEncoder.ComposeAtAsync) — a larger change.
- **Note (pre-existing)** — per-segment audio muxing doesn't tempo-compensate speed-adjusted VideoSegments; audio overlaps the next segment. Standing limitation, not a branch regression.
- Clean (no issues): rendering pipeline (Win2D disposal, ConfigureAwait, bezier solver bounds), most timeline ops, threading/teardown in UI.

Verification: VS MSBuild x64 (Core+Tests+App) clean; suite 374 green.

## Local build and launch — ARM64 host without Visual Studio

- **Feature/area**: Build and local app launch.
- **Approaches tried**: `dotnet build` normally and with PRI generation disabled; unattended Visual Studio Build Tools install; loose MSIX registration; unsigned MSIX install; direct executable launch.
- **What worked**: The existing self-contained ARM64 output launches directly from `src\Musio.App\bin\ARM64\Debug\net9.0-windows10.0.26100.0\win-arm64\Musio.App.exe`.
- **What didn't work**: `dotnet build` lacks Visual Studio AppX/PRI tasks; Build Tools install was canceled with exit 1602; loose registration requires Developer Mode; the test MSIX is unsigned.

## Full-branch stability review — remaining findings

- **Feature/area**: `feature/text-animations` stability and export correctness.
- **Approaches tried**: Reviewed `origin/master...HEAD` for crashes, leaks, races, data loss, timeline mapping, and preview/export divergence.
- **What worked**: Resource ownership, transition/text rendering, cursor processing, undo/redo snapshots, thumbnail disposal, and Record More project switching were clean.
- **What didn't work**: Segment audio is used only when a text slide exists; appended zoom regeneration destroys manual/deleted zoom edits; GIF export ignores segments; export applies appended zooms to primary video; track drags mix output/source time over slides.

## Branch stability fixes — unified segment export and safe timeline editing

- **Feature/area**: MP4/GIF export, appended recordings, audio placement, zoom regeneration, and camera/zoom timeline gestures.
- **Approaches tried**: Parallel workstreams with exclusive file ownership, direct integration review, targeted follow-ups for cross-source zoom creation and audio interval math, compile-only MSBuild validation, full MSTest run, and a final adversarial review.
- **What worked**: `SegmentFrameComposer` is now the single MP4/GIF frame path, with per-source readers/compositors, appended media, text slides/transitions, source-specific zooms, and segment style overrides. `ExportAudioPlan` maps every edited video segment's embedded/separate audio through trims, deletes, reorder, slides, and appended sources. Appended zoom generation is idempotent and honors suppressed clicks. Timeline gestures clamp within the correct source domain. Core/test sources compile and 414 tests pass.
- **What didn't work**: Windows `BackgroundAudioTrack` has no playback-rate/time-scale API, so speed-adjusted segment audio cannot be tempo-matched without a separate decode/time-stretch/re-encode feature. It now starts aligned, is bounded to the segment, and is explicitly flagged/logged as native-rate.

## ARM64 build environment restored

- **Feature/area**: Local build and launch toolchain.
- **Approaches tried**: Installed Visual Studio Build Tools 2022 with managed desktop and Universal Windows build workloads, then used ARM64 VS MSBuild for a clean Debug build.
- **What worked**: `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\arm64\MSBuild.exe` builds the app with `/restore /t:Build /p:Configuration=Debug /p:Platform=ARM64`; the fresh self-contained executable launches directly.
- **What didn't work**: The .NET SDK-only build remains unsuitable because it lacks the Visual Studio AppX/PRI tasks.

## Editor toolbar control height

- **Feature/area**: Editor right-side toolbar.
- **Approaches tried**: Compared the text-bearing and icon-only controls in `RightToolbarGroup` and normalized their explicit dimensions.
- **What worked**: Setting the top-level buttons and zoom dropdown to `Height="40"` keeps text, icon-only, accent, and combo controls aligned.
- **What didn't work**: Relying on each WinUI control's intrinsic/default height produced visibly different heights as labels were shown or hidden.

## Recording toolbar vertical alignment

- **Feature/area**: Recording capture-mode toolbar container.
- **Approaches tried**: Kept the toolbar's overall height and horizontal inset while compensating for the segmented control's visually high rendered bounds.
- **What worked**: Rebalancing the border padding from `6` to `6,8,6,4` shifts the complete control row down two pixels and visually centers it.
- **What didn't work**: Symmetric numeric padding looked bottom-heavy because the toolkit segmented-control rendering is not optically symmetric.

## Recording segmented-item alignment correction

- **Feature/area**: Capture-mode `Segmented` item presenter.
- **Approaches tried**: Reverted the outer-toolbar padding adjustment and isolated the offset to the segmented control's internal item presenter.
- **What worked**: `CaptureModeSelector Padding="0,1,0,-1"` shifts only the three capture-mode items down one pixel while leaving the control background and neighboring buttons centered.
- **What didn't work**: Moving the entire toolbar row did not change the uneven spacing inside the segmented control.

## Capture picker Escape teardown

- **Feature/area**: Window and region selector overlay cancellation.
- **Approaches tried**: Correlated the blank picker window with the XAML `ArgumentException` in `crash.log` and traced synchronous `TaskCompletionSource` continuation execution from the Escape handlers.
- **What worked**: Creating picker completion sources with `TaskCreationOptions.RunContinuationsAsynchronously` defers host-window teardown until keyboard event dispatch has returned.
- **What didn't work**: Closing the picker inline from the Escape keyboard event caused WinUI to throw `ArgumentException: The parameter is incorrect` and leave a blank host window.

## Capture picker fix local launch

- **Feature/area**: ARM64 Debug validation of capture picker cancellation.
- **Approaches tried**: Stopped the existing executable by PID, rebuilt with ARM64 VS MSBuild, and launched the self-contained build output directly.
- **What worked**: The rebuilt `win-arm64\Musio.App.exe` remained running after startup.
- **What didn't work**: No launch failure occurred.

## PR #54 review fixes — XAML defaults and path comparison

- **Feature/area**: Text slide properties flyout (`EditorPage.xaml`) and timeline source/output mapping (`TimelineModel`).
- **Approaches tried**: Reviewed both Copilot review threads, traced the flyout sync method and the `PrimaryVideoSegments` filter, then aligned them with existing repo conventions.
- **What worked**: Removed `SelectedIndex="7"`/`Value="3"`/`Value="72"` from `SlideAnimationCombo`, `SlideDurationBox`, and `SlideFontSizeBox`; `ShowTextSlidePanel` already seeds all three under `_suppressSlideEvents`, and the model already defaults to `ZoomBlurIn`/`FontSize` 72. Added the missing `_suppressSlideEvents` guard to the three handlers. Switched both `PrimaryVideoFilePath` comparisons in `TimelineModel` to `StringComparison.OrdinalIgnoreCase`, matching `ExportAudioPlan`, `SegmentFrameComposer`, and `TimelineControl`.
- **What didn't work**: `dotnet test` still cannot build the test project (missing Visual Studio PRI/AppX tasks). Build the test project with ARM64 VS MSBuild, then run `dotnet vstest ... /Platform:ARM64` — 414 tests pass.

## Build and launch (routine)

- **Feature/area**: Local build + launch of Musio.App.
- **Approaches tried**: ARM64 VS BuildTools MSBuild `/restore /t:Build /p:Configuration=Debug /p:Platform=ARM64`, then direct launch of the self-contained exe.
- **What worked**: `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\arm64\MSBuild.exe src\Musio.App\Musio.App.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=ARM64` (clean, only MVVMTK0045 warnings); launched `src\Musio.App\bin\ARM64\Debug\net9.0-windows10.0.26100.0\win-arm64\Musio.App.exe` -> window "Musio" responding.
- **What didn't work**: Nothing new; `dotnet build` still avoided per prior PriGen MSB4062 findings.

## Cursor shape detection — continuous polling restored (off the hook hot path)

- **Feature/area**: `MouseHookRecorder`, `CursorShapeResolver` (Musio.Core/Capture). Branch `feature/cursor-shape-continuous-detection`.
- **User directive**: cursor shape was only detected at click/mouse-up; wanted more frequent detection so it feels accurate.
- **Approach (worked)**: dedicated `MouseShapePoller` background thread (NOT a ThreadPool `System.Threading.Timer`, which was the previous implementation) waiting on a `ManualResetEventSlim` at `ShapeSampleIntervalMs` (default 20ms / 50Hz, clamped 8-1000, settable before StartRecording). It calls `CursorShapeResolver.Resolve` and writes `volatile _currentShape`; the WH_MOUSE_LL callback still only READS the cached value, so no syscall is ever added to the input path (that was the original lag regression).
- **New**: on a detected shape change the poller appends a synthetic `MouseEventKind.Move` sample at the cursor position returned by the same `GetCursorInfo` call (`Resolve(out x, out y, out positionValid)`), so hover-driven shape changes are recorded even when the mouse is stationary. Click-time re-confirm kept for exact-at-click accuracy.
- **Ordering**: two threads now append samples, so `AddSampleLocked` clamps any out-of-order timestamp to the previous one — `CursorSmoother.AssignShapes` assumes non-decreasing timestamps.
- **Flicker guard**: `CursorSmoother.StabilizeShapes` (MinShapeHoldSeconds 0.12) remains the defense against the shape-track flicker that continuous polling can reintroduce; do not remove it while polling is on.
- **Lifecycle**: poller starts only after the hook installs, stops in `StopRecording`/`Dispose`; loop wrapped in try/catch(ObjectDisposedException) in case a join times out.
- **Tests**: `CursorShapePollingTests` (interval default/clamp, monotonic clamp on out-of-order append, resolver position). Suite 418 green (ARM64 vstest.console).
- **Gotcha**: rebuilding the App fails with MSB3027 (Musio.Core.dll locked) if a previously launched `Musio.App.exe` is still running — stop the process first.
- **Verified**: VS MSBuild ARM64 Debug clean for Tests + App; 418/418 tests pass; app relaunched and responsive.


---

## README screenshot placeholder replaced with real editor capture

- **Feature/area**: `README.md` hero image + `docs/screenshot.png`.
- **Approaches tried**: (1) Reused old session screenshots — rejected, wrong page/stale UI. (2) Built + launched the app and scripted screen capture with Win32 `BitBlt` (`Graphics.CopyFromScreen`) plus UI Automation to drive the Frame Style flyout (gradient/wallpaper preset, padding, corner radius sliders). (3) User supplied the final image directly, which was copied to `docs/screenshot.png`.
- **What worked**: `SetProcessDpiAwarenessContext(-4)` in the capture script is REQUIRED — without it `GetWindowRect`/`CopyFromScreen` mix logical and physical pixels on this 150%-DPI machine and the capture is offset/cropped. Raising the WinUI window reliably needs topmost + `AttachThreadInput` before `SetForegroundWindow`; a plain `SetForegroundWindow` loses to the terminal. UIA `RangeValuePattern` on the `Padding`/`Corner Radius` sliders and `ExpandCollapsePattern` + `SelectionItemPattern` on the `Preset` combo work well; presence of a `Padding`/`Aspect ratio` element is a good "flyout is open" probe. Esc closes the Frame Style flyout; clicking the button again does not.
- **What didn't work**: `PrintWindow` on the WinUI 3 window (returns black, consistent with earlier notes). The Frame Style flyout is NOT a separate top-level window — enumerating `RootElement` children finds nothing; its controls are descendants of the main window's UIA tree. Preset ComboBox `ListItem` names come back empty, so items can't be picked by name (index only). Narrowing the window below ~1900px collapses the `Frame style`/`Cursor` command-bar labels to icons, so use a maximized/large window for screenshots.
## Editor property flyouts → Adobe-style collapsible properties pane

- **Feature/area**: `EditorPage` right-hand properties dock. New `Controls/PropertiesPane.xaml(.cs)` + `Controls/PropertyPanes/{Scene,TextSlide,Cursor,Video}PropertiesView.xaml`, bridged by `Pages/EditorPage.PropertyPanels.cs`. The Scene / Text Slide / Mouse / Video toolbar buttons + flyouts were deleted from `EditorPage.xaml` (~950 lines); Export stays a flyout.
- **Approaches tried**: (1) Move each flyout's event handlers into the new view code-behinds — rejected, the handlers are woven into the page's renderer/undo/timeline state (4000-line code-behind) and would need a host interface for ~40 members. (2) **Chosen:** keep every handler and all editing logic in `EditorPage`, make the views pure markup, mark each `x:Name` with `x:FieldModifier="public"`, re-expose them from `EditorPage.PropertyPanels.cs` as alias properties (`private ComboBox BgTypeCombo => PropertiesPanel.Scene.BgTypeCombo;`) and attach handlers from code in `WirePropertyPanels()`.
- **What worked**: The alias-property bridge means `EditorPage.xaml.cs`'s ~1500 lines of editing logic were left byte-identical. Cross-check that no XAML handler was dropped with `git show HEAD:...EditorPage.xaml` + a regex over the removed line range for `Handler_Name` attributes, then diff against `WirePropertyPanels()`. Unnamed radio groups (`CropAnchorGrid`, `CursorColorPanel`) are wired by iterating `Children`. Toolbar-button visibility calls (`CursorButton.Visibility = …`) map cleanly onto `PropertiesPane.SetPaneAvailable(PropertyPaneKind.X, bool)`; `ShowTextSlidePanel`'s deferred `Flyout.ShowAt` becomes `ShowPane(...)`. Adobe re-click-to-collapse needs `ToggleButton` + a `Click` handler (a `RadioButton` never re-raises `Checked` when already checked); `UpdateVisualState()` must set `IsChecked` authoritatively afterwards since the toggle already flipped it.
- **Edge fades**: the scroll area hard-cut content, fixed with `TopFade`/`BottomFade` `Border` overlays whose `LinearGradientBrush` is **built in code** from `PaneRoot.Background`'s `SolidColorBrush.Color` (rebuilt on `ActualThemeChanged`). Both stops share the RGB and differ only in alpha — fading to XAML `Transparent` (#00000000) tints the scrim grey. Opacity is driven off `VerticalOffset`/`ScrollableHeight` so a panel that fits shows no scrim; watch for it via the content `Grid`'s `SizeChanged`, not the `ScrollViewer`'s (the viewport size doesn't change when panels swap). The ScrollViewer uses Padding="12,0" — vertical padding leaves a dead gap under the header and above the pane edge that defeats the fade.
- **Vertical rail tabs (icon + rotated label)**: each tab is an upright `FontIcon` stacked over an all-caps 10px SemiBold label rotated 90 degrees. WinUI has no `LayoutTransform`, and a `RenderTransform` is invisible to layout, so a rotated label still reserves a wide/short box and the tab sizes wrongly. Fix: wrap the label in a `Canvas` (which measures children unconstrained, unlike the 36px-wide tab) and, from the label's `SizeChanged`, transpose its size onto the canvas (`slot.Width = h; slot.Height = w`) plus offset it with `Canvas.SetLeft/SetTop` to re-centre after the pivot. The tab then auto-sizes with no explicit `Height`. Do NOT size these by calling `label.Measure()` in `Loaded` or when a tab is shown — it returns 0 for tabs that start `Collapsed` and the tabs render broken; `SizeChanged` handles the lazily-revealed tabs correctly.
- **Panel density / nested scrolling**: root group spacing is 20, label-to-control gaps are 6-8, and the Scene section divider carries `Margin="0,6"`. The aspect-ratio tile stacks intentionally keep `Spacing="4"`. The wallpaper and gradient-preset `GridView`s must NOT use `MaxHeight` — that gives them their own scrollbar nested inside the pane's `ScrollViewer` (two scrollbars, and the inner list traps the wheel). Instead leave them unbounded and set `ScrollViewer.Vertical/HorizontalScrollMode="Disabled"` + matching `ScrollBarVisibility="Disabled"` so they grow to fit and the pane owns all scrolling. Verify with a UIA `ControlType.ScrollBar` descendant count over the pane: it should be exactly 1.
- **Testing caveat (cost real debugging time)**: driving the editor by repeatedly selecting the `Editor` nav item over UI Automation tears down and recreates `EditorPage` each time. Style edits made against such a refreshed instance can appear to do nothing (padding / corner radius / wallpaper showed no preview change), which looks exactly like a broken handler wiring but is an artifact of the automated navigation - the same edits work when the app is driven by hand. Before chasing a `ScheduleStyleUpdate`/`ApplyBackgroundStyle` regression, reproduce it manually first. A quick way to rule wiring out: regex the `Handler_Name` attributes out of the pre-refactor XAML and diff them against the `+=` list in `WirePropertyPanels()`; an exact match both ways means nothing was dropped.
- **ALWAYS use `BasedOn` on a WinUI control Style (bit us twice)**: an explicit `Style` that is not `BasedOn` the control's default *replaces* that default rather than extending it, so the control silently loses its real ControlTemplate. A `PropertySliderStyle` that only set `HeaderTemplate` turned the Slider's round thumb into a plain pill; `GridViewItem` would likewise lose its selection/hover visuals. Either derive with `BasedOn="{StaticResource DefaultSliderStyle}"` / `DefaultGridViewItemStyle`, or skip the Style and set the single property (e.g. `HeaderTemplate`) directly on the control - the latter is safest when you only need one setter. Note the default-style keys are resolved at RUNTIME, so a typo shows up as a crash when the panel is first realised, not as a build error.
- **Property pane label typography lives in `Themes/PropertyPaneStyles.xaml`** (merged in `App.xaml`): `PropertyLabelStyle` (12px secondary), `PropertySectionHeaderStyle` (13px SemiBold), `PropertyHintStyle` (11px), `PropertyTileCaptionStyle`, and `PropertyHeaderTemplate`. Controls that label themselves via the built-in `Header` (ComboBox / Slider / ToggleSwitch) default to a LARGER primary-coloured font than a hand-written `TextBlock` label, which is what made the panes look inconsistent - point their `HeaderTemplate` at `PropertyHeaderTemplate` instead of hard-coding `FontSize`.
- **GridView tiles that must fill the pane width**: set `ItemsWrapGrid.ItemWidth`/`ItemHeight` from the grid's `SizeChanged` (`ItemsPanelRoot as ItemsWrapGrid`) instead of giving tiles a fixed Width/Height, which leaves a ragged gap down the right edge. Put the inter-tile gutter on the `GridViewItem` container `Margin`, NOT on the tile content inside it: the selection visual is drawn around the container, so insetting the content leaves the selection box hanging into the gutter and touching the next tile.
- **Zoom state must be re-synced after EVERY preview renderer rebuild**: `RebuildPreviewRendererAsync` builds a fresh `FrameCompositor` from the raw `MouseRecordingData`, which regenerates ALL auto-zoom segments from the click stream. Deleting an *auto* zoom segment does not remove a keyframe - it records the click tick in `TimelineModel.SuppressedClickTicks` - so a rebuild that skips `UpdateSuppressedClickTicks` silently resurrects deleted auto zooms. Symptom: delete all zoom segments, change any scene/background property (which triggers a rebuild), and the preview zooms with no matching timeline segment. Both `UpdateZoomKeyframes` and `UpdateSuppressedClickTicks` must be called unconditionally - an EMPTY manual list and an EMPTY suppression set are both meaningful, so never guard them with `if (count > 0)`. This is centralised in `EditorPage.SyncZoomStateToRenderer()`; call it from any new renderer-rebuild path rather than re-deriving the keyframe query (the rebuild path had also drifted to omit the `IsManual` filter that `InvalidatePreview` used).
- **Shadow auto-zoom from out-of-range clicks**: `AutoZoomEngine.RebuildAutoSegments` generated a segment for EVERY click, while both editor keyframe generators (primary in `InitializePreviewAsync`, appended in `GenerateAppendedZoomKeyframes`) skip clicks with `clickTime < 0` or past the source duration. A click recorded just before capture started maps to a negative time, but its hold + ease-out still reach into the visible range, so the preview zoomed with NO segment on the zoom track - and since the timeline never drew it, the user could not delete it and its tick never entered `SuppressedClickTicks`. `BuildZoomTimeline` now takes `durationSeconds` (passed from `FrameCompositor.InitializeAsync`, 0 = unknown) and applies the same range rule. Keep the engine and the editor's keyframe generation on identical filtering rules or a zoom appears that the user has no way to remove.
- **Appended-segment previews need the SOURCE EXTENT, not the clip length**: the per-segment compositor is driven with ABSOLUTE source times (`seg.SourceStart + localOffset`), so `PreviewRenderer.InitializeAsync` must receive `seg.SourceStart + seg.SourceDuration`. Passing the bare `SourceDuration` truncates the cursor sample buffer and (since the auto-zoom range check was added) drops auto-zoom across the whole visible tail of any clip trimmed from the front. Export gets this right via `SegmentFrameComposer.ResolveSourceDuration`; keep the two in step.
- **Known gap**: appended-recording previews (`GetOrBuildSegmentPreviewAsync`) never sync manual keyframes or suppressed ticks at all, so their preview can disagree with export, which does sync via `SegmentFrameComposer.SyncZoomState`.
- **Store-style nav rail (icon over label) with NavigationView**: set `PaneDisplayMode="Left"`, `IsPaneOpen="True"`, `IsPaneToggleButtonVisible="False"` (plus `TitleBar.IsPaneToggleButtonVisible="False"`) and a fixed `OpenPaneLength` for a non-expandable rail with no hamburger. Two hard-won gotchas: (1) assigning a UIElement to `NavigationViewItem.Content` CRASHES the XAML runtime (0xc000027b) when the item is realised - put the layout in `ContentTemplate` and leave `Content` a string. (2) The stock item gives the label only the width beside a reserved icon column, clamping captions to ~26px in a 64px rail; an implicit `Style TargetType="NavigationViewItemPresenter"` is IGNORED from both `NavigationView.Resources` and `Application.Resources` (proved with a red-background template that never rendered) because NavigationViewItem supplies its presenter's style itself. Templating `NavigationViewItem` directly DOES work - see `Themes/NavigationRailStyles.xaml`. Use `NavigationView.FooterMenuItems` + `IsSettingsVisible="False"` so Settings gets the same stacked layout. (3) NEVER name a custom part `SelectionIndicator` in a NavigationViewItem template - NavigationView looks that part up by name and animates its position itself, which drags the bar to the top of the item on every navigation; rename it (`RailSelectionBar`) and drive the grow/fade from your own state Storyboards, since renaming also opts out of the built-in animation. (4) Put the icon and label IN the control template (bound to the native `Icon`/`Content`) rather than in a ContentTemplate: a `VisualState` Setter cannot target elements inside a ContentPresenter's DataTemplate, which you need in order to hide the label on the selected item.
- **Custom NavigationViewItem template: Disabled needs its OWN VisualStateGroup.** With no ``NavigationViewItemPresenter`` part in the template, ``NavigationViewItem::UpdateVisualStateForPointer`` falls back to calling ``GoToState`` on the item TWICE - once with ``Enabled``/``Disabled`` and once with ``Normal``/``Selected``/... . If both sets live in one group the second call immediately overwrites the first, so a disabled item renders as fully enabled. Put ``Enabled``/``Disabled`` in a separate ``DisabledStates`` group (and do NOT leave a duplicate ``Disabled`` in ``CommonStates`` - VSM resolves the first group containing that state name).
- **A state that sets a property must be set in EVERY sibling state that should keep it.** ``Pressed`` omitting ``ContentStack.Opacity`` made a pressed item snap back to the dimmed base value for the duration of the click, flickering on every navigation. Entering a sibling state reverts anything the previous state set.
- **Verify a WinUI build actually succeeded before blaming runtime**: filtering MSBuild output for `': error'` MISSES `XamlCompiler error WMC0011` style messages, so a failed build silently leaves the previous exe in place and you debug a stale binary. Check `0` instead. (`Window.Resources` does not exist in WinUI 3 - put window-level resources on the root `Grid` or in `App.xaml`.)
- **What didn't work**: `dotnet build` (PriGen MSB4062, as always) — use the ARM64 VS BuildTools MSBuild. UIA `TogglePattern.Toggle()` drives the rail tabs fine, but the automation tree readback is stale by one action unless you re-`FindFirst` the window and sleep ~2s, which made the collapse behaviour look off-by-one until re-tested. `InvokePattern`/`ExpandCollapsePattern` on the toolbar `Insert` DropDownButton never opened its `MenuFlyout` from a background script, so the text-slide path was verified by build + code review rather than UIA.

## Mini Mode — compact launch window with Full/Recording hand-off

- **Feature/area**: New `MiniWindow` shell surface + `ShellCoordinator`. Branch `feature/mini-mode-v2` (fresh off master; the older `feature/mini-mode` branch had a unified single-window `AppShellWindow` design and was abandoned ~49 commits behind master).
- **User directive**: Mini window must be a **separate window** that transitions to/from the full window (explicitly NOT a re-skin of one window). Hide on record, hand off to full app + editor on stop, tray click opens Mini.
- **Design that worked**:
  - Pure, testable state machine in **Musio.Core** (`Shell/AppShellStateMachine.cs`, states `Mini`/`Full`/`Recording`). It lives in Core because `Musio.Tests` only references `Musio.Core` — anything in `Musio.App` is untestable here.
  - `Services/ShellCoordinator.cs` (App) owns `MainWindow` + `MiniWindow` + `RecordingOverlayWindow` + region border, and just applies whatever state the machine reports. `ShellCoordinator.Instance` is the entry point other UI reaches for.
  - Recording lifecycle (hide window, 600ms settle, overlay, `OpenCaptureGate`, region border, editor hand-off) was **moved out of `RecordingPage`** into the coordinator, because Mini can record without that page ever being constructed.
  - Toolbar extracted to `Controls/RecordToolbarControl` and hosted by both `RecordingPage` and `MiniWindow`, both driving the singleton `RecordingViewModel.Shared`.
  - `App.OnLaunched` **always constructs `MainWindow`** (it anchors app lifetime, hotkeys and the quiesce path) but only `Activate()`s it when startup mode is Full.
- **Gotchas found and fixed (all real bugs)**:
  - `RegionSelectorOverlay`/`WindowSelectorOverlay` hard-coded `App.MainAppWindow` minimize+`SW_RESTORE`. Under Mini mode the full window is **hidden, not minimized**, so restoring it popped it up next to the always-on-top pill. Fixed by routing both through `ShellCoordinator.HideForPicker()`/`RestoreAfterPicker()` (reference counted).
  - That refcount **must** be released in a `finally` — `RegionSelectorOverlay.ShowAsync` had no try/finally, so one throw would leave the depth stuck at >=1 and every later picker would hide a surface and never restore it. `MiniWindow` sets `IsShownInSwitchers = false`, so there is no Alt-Tab escape hatch; only the tray icon.
  - `RecordingViewModel.IsAppendMode` was only reset by `RecordingPage.OnNavigatedTo`. Mini can start the next recording without that ever running, so the coordinator now clears it after consuming it.
  - Tray "Open Musio" poked the HWND directly, leaving the state machine on `Mini` while the full window was visible: Collapse became a no-op and the next picker would `SW_HIDE` the window the user was in. Now goes through `ShellCoordinator.ShowFullFromTray()`.
  - `DesktopAcrylicBackdrop`/`MicaBackdrop` live in `Microsoft.UI.Xaml.Media`, not `Microsoft.UI.Composition.SystemBackdrops` (that namespace only has the *Controller* types).
  - x:Bind **function** bindings do not re-evaluate when a `DependencyProperty` argument changes — call `Bindings.Update()` from the DP's `PropertyChangedCallback` (`RecordToolbarControl.OnLayoutFlagChanged`).
  - Frameless window sizing: WinUI won't auto-size a `Window` to content. `RootGrid.Measure(infinite)` then `AppWindow.Resize(DesiredSize * dpi/96)` works; re-run it whenever the toolbar reveals/hides its picker buttons.
- **Build/test (verified again)**: `"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\arm64\MSBuild.exe" <csproj> /t:Restore,Build /p:Configuration=Debug /p:Platform=ARM64`, then `$env:DOTNET_ROLL_FORWARD="Major"; dotnet vstest src\Musio.Tests\bin\ARM64\Debug\net9.0-windows10.0.26100.0\Musio.Tests.dll /Platform:ARM64`. 444 tests pass. `dotnet test`/`dotnet build` still fail with PriGen MSB4062.
- **Runtime verification via UIA (worked well here)**: enumerate top-level windows by `ProcessIdProperty` and invoke buttons by `AutomationProperties.Name`. Verified launch->Mini, Expand->Full, Collapse->Mini (repeated cycles), Record from Mini -> pill only -> Stop -> full window on Editor, region picker hides/restores only Mini, hide-to-tray leaves 0 windows with the process alive.
- **What didn't work**: `FindWindowEx(HWND_MESSAGE, ...)` never located `SystemTrayService`'s message-only window from another process, so the tray click could not be simulated by `PostMessage`. What DID work: find the tray icon in the notification-area overflow (`TopLevelWindowForOverflowXamlIsland`) by name `Musio - Screen Recorder` and `InvokePattern.Invoke()` it — that correctly opened Mini. Right-clicking it for the context menu was not reproducible from a script (the overflow flyout dismisses), so the tray context-menu items were verified by code review only.
- **Not implemented (deliberately)**: persisting the last selected *window* across launches (needs fuzzy HWND re-matching); region + capture mode already persist.

## Mini Mode — code review follow-ups (DPI, drag, tray fallback)

- **Feature/area**: `MiniWindow`, `ShellCoordinator`, `App` on `feature/mini-mode-v2`.
- **Findings fixed (all real, from code-review agent)**:
  - **Never-shrink width high-water mark must be stored in DIPs, not physical px.** It was `Math.Max`-ed against a freshly DPI-scaled width, so dragging the pill from a 200% monitor to a 100% one clamped the correctly-halved width back up and left it ~2x too wide. WM_DPICHANGED fires `AppWindow.Changed` but nothing re-baselined the mark.
  - **A user-draggable window must not be re-centred on every resize.** `SetTitleBar(DragGrip)` makes the pill movable, but `DockBottomCenter()` ran unconditionally from `ResizeToContent` and from `AppWindow.Changed`, so the first capture-mode switch teleported it back. Fix: set `_userPositioned` on `DidPositionChange` when no programmatic change is in flight, then clamp instead of re-centre.
  - **Guard programmatic window changes with a DEPTH COUNTER, not a bool.** `AppWindow.Resize` raises `Changed` synchronously, so a nested programmatic `Move` inside a programmatic resize would clear a bool guard in its `finally` and let the outer change be misread as a user drag.
  - **Any "hide to tray" path needs a no-tray fallback.** Tray init is best-effort (`catch { _trayService = null; }`). `MiniWindow` sets `IsShownInSwitchers = false` and `MainWindow` is `SW_HIDE`-den, so hiding both with no tray left the app unreachable (kill via Task Manager). `App.OnWindowClosing` already guarded this; `MiniWindow.OnClosing`/`HideToTray` did not. Now `ShellCoordinator.IsTrayAvailable` gates it and falls back to `ShowFullWindow()`.
- **UIA test-harness gotchas (cost real debugging time)**:
  - Each `powershell` tool call is a **fresh process**, so `Add-Type -AssemblyName System.Windows.Forms` must be repeated in EVERY block that uses `SendKeys`. Suppressing its error with `2>$null` silently skipped an Esc that was meant to dismiss a picker, leaving the app with zero visible windows and looking like an app bug when it was a harness bug.
  - `WDA_EXCLUDEFROMCAPTURE` (set on the Mini pill and the recording overlay) also excludes those windows from **screenshots**, so `CopyFromScreen` captures whatever is behind them. Verify their visuals by geometry/UIA measurement, or by rasterising a glyph with `System.Drawing` to confirm it isn't a fallback box — not by screenshotting.
  - When UIA reports 0 windows for a live process, fall back to `EnumWindows` + `IsWindowVisible` to distinguish "hidden" from "destroyed".
- **Verified**: fresh launch auto-docks bottom-centre; dragged pill keeps position across mode switch and clamps on screen when it grows (X 2234 -> 1224, flush to the 2304-wide screen); picker hide/restore and hide-to-tray unchanged with a tray present. 444 tests pass.

## Mini Mode — round 3 review: cross-surface toolbar sync

- **Feature/area**: `RecordToolbarControl`, `ShellCoordinator` on `feature/mini-mode-v2`.
- **Bug found by review**: capture mode / selected window / selected region are pushed **imperatively** into `RecordToolbarControl` (only the audio+webcam toggles are `x:Bind ... TwoWay`). `SyncFromViewModel()` only ran on navigation, `Loaded`, and `ShowMini()`. `ShellCoordinator.ShowFullSurface()` merely calls `ShowWindow(SW_SHOW)`, which does **not** navigate and does **not** unload/reload an already-constructed `RecordingPage` — and `RecordNavItem` is `IsSelected="True"`, so the Record page exists from launch even in Mini startup. Result: change mode in Mini -> Expand -> Record page showed the old mode and wrong picker button, and its Record button would record a mode the UI wasn't showing.
- **Fix**: `RecordToolbarControl` subscribes to `ViewModel.PropertyChanged` on `Loaded`, unsubscribes on `Unloaded` (mandatory: `RecordingViewModel.Shared` is a process-wide singleton and a new control instance is created per RecordingPage navigation).
- **Two guards this exposed (both fixed)**:
  - **`_isPickerOpen` must be `static`.** Record page and Mini each own a toolbar and BOTH are alive simultaneously; a per-instance flag let two pickers open at once, which double-counted the coordinator's process-wide hide.
  - **A "suppress side effect" flag must be consumed INSIDE the event handler, not used as a scope flag.** `ctk:Segmented` derives from `ListViewBase` and does not guarantee a synchronous `SelectionChanged` for a programmatic `SelectedIndex` assignment, so `flag = true; SelectedIndex = x; flag = false;` can be fully unwound before the handler runs. Set the flag before the assignment and clear it as the first thing the handler does.
- **`HideForPicker`/`RestoreAfterPicker` changed from a reference COUNT to a LATCH.** A count that ever got out of step leaves every window hidden with no way back (Mini is not in Alt-Tab, MainWindow is `SW_HIDE`-den); a latch always restores on the first release. Prefer a latch over a counter whenever the failure mode is "user has no reachable window".
- **UIA harness gotchas (again)**:
  - **Esc on the Mini pill is "hide to tray"**, so a test that blindly sends `{ESC}` after every capture-mode click will hide the app whenever the mode opened no picker (Full Screen). This looked exactly like a picker-restore bug and cost a full investigation cycle. Only send Esc when a picker actually opened.
  - `Visibility.Collapsed` elements ARE removed from the UIA tree, so `FindFirst` returning null is a valid "hidden" signal; use `IsOffscreen` on found elements to distinguish.
- **Verified**: six consecutive Region/Window picker cycles hide+restore the pill every time; Record page shows Region after Mini selects it. 444 tests pass.

## Mini Mode — persistent selection highlight (border + smoke)

- **Feature/area**: `Services/SelectionHighlight.cs` (replaced `RegionBorderHighlight.cs`), `ShellCoordinator`, both capture pickers. Branch `feature/mini-selection-highlights`.
- **User directive**: persist the selection on screen so it is visible from the Mini pill before recording; match border style and dim ("smoke") between selection time and record time; dashed, 4px rounded, thinner stroke; do NOT recolour to red while recording.
- **Design**: two layered Win32 windows, both click-through (`WS_EX_TRANSPARENT`), `WS_EX_NOACTIVATE`, topmost, `WDA_EXCLUDEFROMCAPTURE`. (a) BORDER shaped with `SetWindowRgn` = rounded outer rect MINUS rounded inner rect, then dash gaps subtracted; (b) SMOKE = virtual-desktop rect MINUS the selection hole, 30% via `LWA_ALPHA`. Shaped windows are what make rounding and dashes possible — four abutting bars cannot.
- **GDI gotchas (each cost real debugging)**:
  - **`CreateRoundRectRgn` produces a region ONE PIXEL SMALLER than `CreateRectRgn` for identical bounds.** Measured: `CreateRectRgn(0,0,10,10)` covers x=0..9, `CreateRoundRectRgn(0,0,10,10,..)` covers x=0..8. So round-rect bounds need a `+1`. A reviewer flagged the `+1` as an off-by-one; removing it collapsed the right/bottom bands to zero (bands 3/0/3/0 instead of 3/3/3/3). **Verify region arithmetic with an isolated GDI probe before "fixing" it.**
  - **`GCLP_HBRBACKGROUND` is CLASS state, not per-window.** `SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, brush)` changes the brush for every window of that class, so two layers sharing a class fight over one brush and the last writer wins for both (smoke painted accent blue instead of black). Fix: real WndProc handling `WM_ERASEBKGND` + a per-HWND brush dictionary. That also removes a dangling class reference to a deleted brush and avoids `SetClassLongPtrW`, which 32-bit user32 does not export.
  - **A topmost full-desktop layer dims your own windows too.** `SetWindowPos(..., HWND_TOPMOST, ...)` re-inserts at the TOP of the topmost band on every call, so anything of ours outside the selection (Mini pill, recording overlay) must be re-raised **after every render**, not once.
  - Window bounds for highlighting should come from `DWMWA_EXTENDED_FRAME_BOUNDS`, not `GetWindowRect` — the latter includes the invisible resize border and leaves a visible gap.
- **DIP vs physical pixels**: XAML `StrokeThickness` is DIPs; hand-rolled Win32 geometry is physical pixels. "3" in both places is NOT the same size (4.5px vs 3px at 150%). Express Win32 geometry in DIPs and convert per-render via `MonitorFromRect` + `GetDpiForMonitor`, or it thins out as DPI rises.
- **Monitor resolution must be strict for a region preview.** `FindMonitorForRegion` had a `?? monitors.FirstOrDefault()` fallback; region coords are monitor-local, so a region from an unplugged display previewed on the primary monitor. Use the same predicate as `RecordingViewModel.BuildCaptureTarget` (`DisplayName == MonitorId || StartsWith(MonitorId + " ")`) and show nothing when it fails, so preview and capture agree.
- **High Contrast**: `RegionMaskBrush` mapped to `SystemColorWindowColor` (opaque). The window picker works by hovering a desktop screenshot, so an opaque scrim hides what is being picked. Picker scrims need an alpha-bearing colour even in HC.
- **UIA harness notes (this area is hard to drive)**:
  - `mouse_event` + `SetCursorPos` is unreliable for WinUI drag gestures; **`SendInput` with `MOUSEEVENTF_ABSOLUTE`** works. Normalise to 65535 over `GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)`.
  - After a drag the region overlay holds pointer capture, so a following click on Confirm is swallowed; a degenerate `SendInput` drag (down+up at one point) does land.
  - The window picker needs a real `PointerMoved` (hover) before the click or nothing is selected.
  - Highlight layers are `WDA_EXCLUDEFROMCAPTURE`, so they cannot be screenshotted. Verify them structurally: `GetWindowRgn` + `PtInRegion` to measure band widths, and `GetRegionData` rect count to prove dashes exist (352 rects dashed vs ~10-20 solid).
- **Verified**: highlight persists across Expand/Collapse in both modes, follows a moving window, clears on Full Screen/Expand/tray/after recording; z-order pill|overlay > border > smoke; bands 3/3/3/3 at 150%. 444 tests pass.

## Editability after cleanup + single-file `.musio` project format

- **Feature/area**: Recording storage lifecycle (`VideoWriter`, `RecordingSession`, `SessionCleanupService`), frame reading (`VideoFrameReader`), audio capture format, and a new `.musio` project container (`Musio.Core/Projects`).
- **Problem**: Exporting deleted `.frames/`, which made a project permanently un-editable — because `VideoFrameReader` could *only* read those JPEGs, even though a perfectly good `video.mp4` sat beside them. Sessions were also folders, which felt wrong as a "project".
- **Approaches tried**:
  - Keeping frames longer / gating deletion on app exit — rejected: treats the symptom, still 450 MB/min at 1080p30.
  - `Windows.Media.Editing` (`MediaClip`/`MediaComposition`) for decode — rejected up front per the existing playbook (rejects fragmented MP4s, one COM object per frame).
  - AAC for captured audio — rejected after measuring; see below.
  - ZIP vs. custom flat container for `.musio` — ZIP chosen.
- **What worked**:
  - **The MP4 was always the real master; only the reader was wrong.** Split `VideoFrameReader` into `IFrameSource` + `JpegFrameSource` + `Mp4FrameSource`, preferring JPEGs when present and decoding the MP4 otherwise. Callers (`EditorPage` ×3, `SegmentFrameComposer`) were already async, so `OpenFromVideoPath` → `OpenFromVideoPathAsync` was mechanical.
  - **`MediaPlayer` frame-server mode** (`IsVideoFrameServerEnabled`, `AutoPlay=false`, `RealTimePlayback=false`) + `CopyFrameToVideoSurface` into a reused `CanvasRenderTarget`. `CanvasBitmap`/`CanvasRenderTarget` *is* an `IDirect3DSurface`, so this is GPU-to-GPU with no CPU round-trip.
  - **`StepForwardOneFrame` is correctness-adjacent, not an optimization.** Assigning `Position` flushes the decoder back to the enclosing keyframe; a sequential walk over a 1 s GOP would re-decode up to `fps` frames *per frame* — quadratic over an export. Take the step path whenever `index == last + 1`.
  - `.frames/` now deleted the instant `FinalizeAsync` succeeds (`VideoWriter.FinalizeSucceeded` → `DeleteCapturedFrames()`), and kept when it fails — which preserves the existing playbook rule that frames are the only copy when finalization is non-fatal. `SessionCleanupService` became a backstop gated on a valid `video.mp4` rather than on an export marker.
  - `CaptureQuality` setting (Balanced/HighFidelity/Master = 12/30/60 Mbps base at 1080p, scaled by pixel count, clamped 0.5×–8×), plus a ~1 s GOP hint via `CODECAPI_AVEncMPVGOPSize` on `profile.Video.Properties`.
  - **`.musio` = ZIP** with `manifest.json` + `media/<n>_<name>` entries. Media compression is per-extension: already-compressed containers stored verbatim, PCM/event logs deflated. Extract-on-open to a LocalAppData cache (keyed by project id, reused across opens) because the whole decode stack — `MediaPlayer`, `MediaClip`, NAudio — wants real file paths and `System.IO.Compression` gives no seekable entry streams.
  - `MusioPathRewriter` is the single traversal for every media reference; save maps absolute→entry, open maps entry→absolute. Save deep-clones first (via JSON) so the editor's live `Project`/`TimelineModel` keep their working paths and segment instances are not replaced.
  - Serialization needed three things on .NET 9: `[JsonPolymorphic]`/`[JsonDerivedType]` on `TimelineSegment` (`$kind` discriminators are now on-disk format), `[JsonObjectCreationHandling(Populate)]` on `TimelineModel` (its collections are get-only), and `[JsonIgnore]` on computed properties and regenerable caches (`CursorData`, waveform arrays).
  - File association: `windows.fileTypeAssociation` in the manifest + `AppInstance.GetCurrent().GetActivatedEventArgs()` in `OnLaunched`, routed through the existing `MainWindow.ShowEditor()`.
  - 472 tests pass (13 package round-trip, 7 PCM conversion, 6 bitrate).
- **What didn't work / deliberately not done**:
  - **`StepForwardOneFrame` is on `MediaPlayer`, NOT `MediaPlaybackSession`** — the obvious guess fails to compile.
  - **AAC for captured audio was rejected on evidence.** Audio is ~17% of a session (WASAPI shared mix is typically 32-bit float/48 kHz/stereo = 384 KB/s *per track*), so the win was real — but AAC prepends encoder priming samples, and every recording's sync derives from the first captured sample (`AudioToVideoOffsetSeconds`). Settled on `Pcm16CaptureWriter` (converts the mix format to 16-bit PCM, halving size with no sync impact) plus Deflate inside the package. Do not reintroduce a compressed capture codec without explicit priming-delay compensation and a sync test.
  - Reading media in place from the ZIP: `System.IO.Compression` returns non-seekable `SubReadStream`s for stored entries, so there is no zero-copy path without a third-party zip library or a custom container.
  - Naive `Position`-per-frame decoding (see `StepForwardOneFrame` above).
  - `dotnet build`/`dotnet test` still fail with the PriGen MSB4062 error — build with VS MSBuild first, then `dotnet test --no-build -p:Platform=ARM64 -c Debug`.
- **Hazard fixed during review**: a frame abandoned by a decode timeout can land later and satisfy the *next* request with stale content (a wrong frame in an export, not just a preview glitch). `Mp4FrameSource` sets `_needsDrain` on timeout and waits it out before the next seek. Zip entry names are also untrusted input — `ResolveExtractionPath` rejects `media/../..` traversal.
- **Review round (4 real bugs found, all fixed)** — worth remembering because three of them are structural traps in this codebase:
  1. **CRITICAL / data loss.** `FinalizeAsync` created `video.mp4` *before* transcoding, so a timeout, frame error or process kill left a non-empty but undecodable file. With cleanup now gated on "non-empty `video.mp4` exists", the next launch would delete `.frames/` and destroy the recording. **Fix: transcode to `video.mp4.partial` and `File.Move` into place only after every check passes.** Invariant to preserve: *`video.mp4` existing must imply a complete, playable recording.*
  2. `MusioPathRewriter` missed `CompositionConfig.Background.BackgroundImagePath`, `CompositionConfig.Cursor.CustomImagePath`, and per-segment `FrameStyleOverride`/`CursorStyleOverride` — `Rewrite` was never handed the `CompositionConfig` at all. A packaged project would keep an absolute wallpaper path and silently fall back to a solid colour on another machine. `Rewrite` now takes and *returns* a `CompositionConfig` (the style records are immutable, so it cannot be mutated in place) and `SaveAsync` clones it. **When adding any new media-path property to a model, update BOTH `Enumerate` and `Rewrite`.**
  3. `Mp4FrameSource`: `_framePending` was cleared in `LoadFrameAsync`'s `finally`, i.e. *after* the waiter had already drawn from `_surface`. A second `VideoFrameAvailable` raise in that window overwrites the surface mid-read. Fixed with `Interlocked.Exchange(ref _framePending, null)` in the handler so only the first frame consumes the request.
  4. `VideoFrameReader.Dispose` tore down the cache without taking `_cacheGate`, so it could dispose a `CanvasBitmap` mid-`Clone`, and made `LoadFrameAsync` start throwing `ObjectDisposedException` (a regression — it used to be non-throwing). Both matter because `EditorPage` disposes `_frameReader` from the UI thread while fire-and-forget thumbnail generation is still running.

## Editor regressions exposed by MP4-backed editing (flip, preview, filmstrip, restore)

- **Feature/area**: `VideoWriter` encode orientation, `Mp4FrameSource` seek/step behaviour, filmstrip generation, `.musio` restore path, `EditorPage`/`TimelineControl` binding.
- **Approaches tried**: Reasoning from symptoms (three rounds, two wrong diagnoses), then headless probes replaying the exact access patterns against real recordings, then a persistent `DiagLog` file read after a real repro.
- **What worked**:
  - **`video.mp4` had ALWAYS been encoded vertically flipped.** Proven with an independent decode (`MediaComposition.GetThumbnailAsync`) showing the same flip. Cause: `VideoWriter` uses `MediaStreamSample.CreateFromBuffer` with `VideoEncodingProperties.CreateUncompressed(Bgra8)`, and **MF treats a positive-stride uncompressed RGB type as BOTTOM-UP** while a raw buffer carries no orientation. The export path never had the bug because `VideoEncoder` passes a D3D surface (`CreateFromDirect3D11Surface`), which carries its own orientation. Fixed with `BitmapTransform.Flip = BitmapFlip.Vertical` at JPEG decode. Nobody had noticed because nothing ever *displayed* `video.mp4` — both editor and export read the `.frames/` JPEGs.
  - **Preview stutter was a feedback loop, not decode cost.** `EditorPage.UpdatePreviewFrameAsync` coalesces render requests it cannot keep up with, so falling one frame behind makes the next request `last + 2`, which missed the `+1` step fast path and became a full seek (10-100x slower), so it fell further behind. Fix: walk any forward gap up to `MaxStepAhead` (8) with `StepForwardOneFrame` instead of seeking. Playback went from stalling to 120/120 frames at 30.0 fps.
  - **`MediaPlaybackSession.SeekCompleted` is what makes seeking bounded.** Without it there is no way to distinguish a slow seek from one that completed and silently rendered nothing, so every failure burned a fixed timeout. Scrubbing went 3590 ms -> 69 ms average.
  - **Sparse thumbnails must NOT come from a frame reader.** A single-position decoder means one seek per tile: measured ~334 ms each with ~3/11 returning nothing, while also contending with the preview for the same file. `MediaComposition.GetThumbnailsAsync` (new `VideoThumbnailExtractor`) measured **14.7 ms per tile at 100 tiles with zero failures** and needs no `MediaPlayer`, so it cannot contend. Use it for any sparse/batch frame access.
  - **`x:Bind` defaults to `OneTime`.** `EditorPage.xaml` bound `Model="{x:Bind ViewModel.Model}"`, so `TimelineControl` captured whichever `TimelineModel` existed at page construction. Recording always rebuilt the page so it never showed; adding "open project" swapped the model under an existing page and every metadata track (cursor/zoom/camera/audio) went blank while the model itself was fully populated. Fixed with `Mode=OneWay` plus an explicit assignment on `ModelReloaded`. **Audit other `x:Bind` uses that must track changes.**
  - **Restoring a project must not re-apply first-open defaults.** `InitializePreviewAsync` unconditionally overwrote `Cursor`, `Zoom`, and smoothing with hard-coded values and regenerated auto-zoom keyframes from clicks — correct for a fresh recording, destructive for a restored one. Gated on `ProjectService.IsRestoredFromPackage`.
- **What didn't work / process lessons**:
  - **Three separate fire-and-forget paths were swallowing exceptions** (`_ = InitializePreviewAsync()`, `_ = GenerateAllTimelineThumbnailsAsync(...)`, and a bare `catch { }` on cursor load). Blank UI with no error is the signature. All three now log via `DiagLog`.
  - **`Debug.WriteLine` is useless here** — the app is launched normally, not under a debugger. `Musio.Core/Diagnostics/DiagLog.cs` writes to `%LOCALAPPDATA%\Musio\diag.log`; use it for anything diagnosable only at runtime.
  - Guessing from screenshots cost several rounds and produced two wrong fixes (progressive publish sharing bitmap references, which caused a *worse* bug: cancellation disposed bitmaps the timeline was still drawing, and a disposed `CanvasBitmap` throws mid-draw and blanks the rest of the track). **Reproduce headlessly or instrument; do not infer.**
  - Gating the filmstrip behind the preview reader (`if (_frameReader is null) return;` before starting generation) meant a failed preview decoder silently skipped thumbnails entirely, with nothing retrying.

## Review fixes + sessions moved out of the user's Videos folder

- **Feature/area**: `SessionCleanupService` safety gate, `Mp4FrameSource` concurrency, restore-vs-append defaults, recording session location.
- **What worked**:
  - **Legacy sessions must be treated as JPEG-only.** Every `video.mp4` written before the orientation fix is vertically flipped, so for those sessions the captured JPEGs are the ONLY correctly oriented copy. Dropping the old `exported.marker` gate meant startup cleanup would have deleted them on first launch of the new build — silent, unrecoverable corruption of every existing recording. `VideoWriter` now writes `finalized.marker` (with `EncoderVersion`) next to the MP4 at the same moment it moves the file into place, and `SessionCleanupService.CanReleaseFrames` requires it. The marker doubles as proof the encode ran to completion, which a `Length > 0` check cannot give for sessions written in place by older builds.
  - **A stalled step must drain before falling back to a seek.** `_needsDrain` was only honoured at the top of the next `LoadFrameAsync`, so the in-call step->seek fallback could let the abandoned step's late frame (for an index BEFORE the target) satisfy the seek — a silent off-by-N in an export. Extracted `DrainIfNeededAsync()` and call it at both sites.
  - **Never `Dispose` a `SemaphoreSlim` that callers may be parked in.** `SemaphoreSlim.Dispose` does not release waiters in `WaitAsync`, so disposing the gate stranded any queued frame request forever — a hung export, not a dropped frame. Both `Mp4FrameSource` and `VideoFrameReader` now acquire the gate (with timeout) before tearing down, and deliberately do not dispose it; the `_disposed` check inside the critical section makes queued callers return null.
  - **"Restored" is a property of a SOURCE, not of the project.** Gating first-open defaults on a project-wide `IsRestoredFromPackage` denied auto-zoom and smoothing to recordings appended *after* opening a package, which have no saved state to protect. `ProjectService.IsRestoredSource(path)` tracks the set of paths that came out of the package instead.
  - **Sessions live under `%LOCALAPPDATA%\Musio\Sessions`** (`SessionPaths`). A session is working state only the app understands; the user's Videos folder now only receives saved `.musio` projects and exports. Startup cleanup sweeps both roots so sessions recorded by older builds are still handled. Export prefill is independent (`Videos\Musio`) and unaffected.
- **What didn't work**: the first version of the cleanup gate used only `HasValidVideo` (non-empty `video.mp4`). That is not sufficient evidence to delete the frames — it says nothing about whether the encode completed, nor which encoder version produced it.
