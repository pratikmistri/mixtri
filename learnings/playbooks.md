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
  `DOTNET_ROLL_FORWARD=Major` to roll forward to the installed runtime. `dotnet test` also
  hits the same PriGen MSB4062 failure when it tries to BUILD, so build the test project
  with MSBuild first and then run
  `dotnet test src\Musio.Tests\Musio.Tests.csproj --no-build -p:Platform=x64 -c Debug`.
  The suite must stay green (currently ~1280 tests, 3 skipped).
- **MSIX (unsigned, for Store):** `msbuild Musio.App.csproj /restore /t:Build /p:Configuration=Release /p:Platform=<x64|ARM64> /p:GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never`.
- **ALWAYS check the LIVE Store version before packaging, and bump past it.** The Store rejects a
  submission whose version equals one already published, and the in-repo manifest normally still
  holds the version that was last shipped — so packaging straight from a clean checkout produces
  packages that cannot be submitted. This has now wasted a full build cycle twice (1.3.0, 1.4.0).
  A bump touches TWO files that must stay in step: `Package.appxmanifest` (`Version="x.y.z.0"` —
  the 4th part must be 0 for the Store) and `SettingsPage.xaml`'s `Text="Version x.y.z"`.
- **Verify the produced `.msix` rather than trusting the path**: open it as a zip and read the
  embedded `AppxManifest.xml` (`Identity` Name/Version/ProcessorArchitecture) and the PE machine
  type of `Musio.App.exe` — the per-architecture output directories are easy to mix up, and a
  wrong-arch payload is only caught by the Store days later. `AppxSignature.p7x` must be ABSENT.
  The `mspdbcmf.exe` warning (no symbols package) is expected on this host and does not block
  submission.
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

## Playbook: Transient UI revealed by a drag gesture (timeline drop-hint lane)

Three rounds of rework, all on the same lane. The rules generalise to any affordance a drag
reveals:

- **Latch it for the duration of the gesture.** Reveal it when the drag first reaches for it,
  then hold it until the pointer is released — never re-evaluate its existence per pointer
  sample. Anything that changes layout under a live pointer gets ONE chance to grow and one to
  shrink (`TimelineControl._hintLaneLatched` / `HintLaneRequested`). Keep the "would the drop
  land here right now" state separate, driving only the highlight (`ShowOverlayDropHint`).
- **Hysteresis is for noise, not for intent.** A dead band can absorb the jitter of a hand
  holding still (~0.75 of a lane); it can never be widened enough to absorb the drift of a hand
  sweeping sideways to position something without also making the target unreachable on purpose.
  If you find yourself widening a dead band to fix churn, latch instead.
- **A gesture that changes the layout must not measure itself against that layout.** Resolve the
  target from how far the pointer has TRAVELLED (`_segmentDragStartY - y`), never from where it
  now sits — revealing the lane shifts every row under a stationary cursor.
- **Animate the SIZE, don't toggle existence**, and scale the row's padding by the same reveal
  fraction — an unscaled pad exceeds a part-open band, yields a negative clip height, and makes
  the dragged block vanish for the first frames. Draw the lane's contents in a layer clipped to
  the band and scaled to its opacity. Skip the fold-away when the drop actually created the
  lane: `UndoRedoManager.Execute` is synchronous, so the real row already occupies that height.
- **During a segment drag invalidate ONLY `VideoTrackCanvas`**, not `InvalidateAll()` — ten
  canvases with filmstrips and waveforms repainted per pointer sample is a visible stutter.



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
  **Anchor BOTH edges to the SINGLE owning segment occurrence** (`OwningSegmentForKeyframe`:
  file match + Timestamp containment), never arbitrary per-edge first-match (which collapsed appended
  segments to a thin line) and never the primary-only `XToSourceTime` mapping for appended keyframes.
  An overflowing edge may traverse only directly adjacent pieces that are contiguous in source,
  output, track, and list order — required for the 1x/1.5x slices created by automatic typing speed.
- On any edit (move/resize/property change), promote the keyframe to `IsManual = true` (save the
  previous value for undo). `ZoomKeyframe.MinSegmentDuration = 200ms`. Cut/split overlap logic must
  use the full `Start`/`End` span, not just `Timestamp`.

## Playbook: Crash / freeze hardening invariants

- **Every `CanvasControl` MUST subscribe `CreateResources` and rebuild on
  `CanvasCreateResourcesReason.NewDevice`.** A `CanvasControl` recovers from GPU device loss
  *internally and silently* — it builds a replacement device and raises `CreateResources`;
  it does NOT necessarily raise `CanvasDevice.DeviceLost` on the shared device. Any bitmap or
  render target the app cached externally (preview frame, filmstrip/waveform bitmaps) then
  belongs to a dead device and every surface draws nothing, permanently, with **no exception,
  no crash.log entry and no diag.log line**. Route these into
  `EditorGraphicsDeviceManager.RequestRecovery(reason)`. Note the enum member is `NewDevice`
  (`FirstTime` is the normal startup pass); there is no `...Reason.DeviceLost`.
- **A flag that suppresses rendering for the duration of an operation needs an explicit flush
  AFTER that operation** — never a repaint issued from inside it. `IsRecoveryInProgress` stays
  true for the whole of `RecoverGraphicsDeviceAsync`, so a repaint called from within it is
  parked, and `_pendingRenderPosition` is drained only by an already-running render loop. That
  is why the manager takes a separate `flushPendingRenderAsync` delegate.
- **Never tear down the preview pipeline before the guard that can abort the rebuild.** Resolve
  `CurrentProject` (and anything else the rebuild needs) FIRST; disposing `_frameReader` and
  clearing track visuals and only then discovering there is nothing to rebuild with leaves a
  permanently blank editor that no later render can repair. Log every such bail-out.
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
