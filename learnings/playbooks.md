> **This is the always-read file.** Every agent reads this at the start of every task -
> it is kept deliberately small. A rule is promoted here only after its area has caused
> repeated churn (multiple rounds of rework, reversals, or regressions); do not add
> speculative rules. For the full historical record of why each rule exists, and for
> entries that never rose to playbook status, see the archive files listed in
> ../learnings.md.

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

