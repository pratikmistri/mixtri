# Stability and Performance Backlog

Repository review completed 2026-07-30. This backlog covers current dead code,
freeze/crash risks, resource-lifetime issues, and performance opportunities.

Priority:

- **P0**: can leave the app corrupted or cause a process-level failure.
- **P1**: likely user-visible freeze, recording/export failure, or sustained performance loss.
- **P2**: bounded reliability/performance issue worth scheduling.
- **P3**: cleanup or optimization with lower immediate user impact.

---

## P0 - Crash and process integrity

- [x] **STAB-001: Stop swallowing every XAML unhandled exception**
  - **Evidence:** `src/Musio.App/App.xaml.cs:81-85`
  - `OnUnhandledException` logs every exception and unconditionally sets `e.Handled = true`.
    Continuing after an unknown UI exception can leave navigation, project state, or native
    resources partially mutated and cause a later failfast or data corruption.
  - **Proposed fix:** handle only explicitly classified recoverable exceptions. For unknown
    exceptions, log full context, attempt bounded project/session preservation, and terminate
    cleanly.

---

## P1 - Freeze, recording, and hot-path risks

- [x] **STAB-002: Bound webcam shutdown**
  - **Evidence:** `src/Musio.Core/Capture/RecordingSession.cs:329-330`,
    `src/Musio.Core/Capture/WebcamCaptureEngine.cs:83-92`
  - Recording stop directly awaits `MediaCapture.StopRecordAsync()` without cancellation or a
    watchdog. A stalled camera driver can leave the app indefinitely stuck in the stopping state.
  - **Proposed fix:** add a cancellation-aware timeout around webcam stop, log the timeout, dispose
    the capture object, and let the rest of session finalization continue.

- [x] **STAB-003: Move JPEG encoding and disk writes off the capture callback**
  - **Evidence:** `src/Musio.Core/Capture/ScreenCaptureEngine.cs:164-225`,
    `src/Musio.Core/Capture/RecordingSession.cs:568-583`,
    `src/Musio.Core/Capture/VideoWriter.cs:157-214`
  - Every accepted frame is synchronously converted to JPEG and written to disk under
    `_writeLock`. Capture throughput is therefore limited by GPU readback, JPEG encoding, and
    storage latency, causing dropped/duplicated frames and making 60 FPS impractical.
  - **Proposed fix:** make the callback copy/enqueue only. Use a bounded writer queue with explicit
    backpressure/drop metrics and independently owned frame surfaces. Preserve the current JPEG
    write-ahead recovery property until a checkpointed live encoder can replace it.

- [x] **STAB-004: Surface frame-write failures during recording**
  - **Evidence:** `src/Musio.Core/Capture/VideoWriter.cs:215-220`,
    `src/Musio.Core/Capture/VideoWriter.cs:608-610`,
    `src/Musio.Core/Capture/RecordingSession.cs:351-360`
  - `WriteFrame` catches and debug-logs failures after incrementing frame count. Finalization later
    fails on the missing JPEG, but that failure is also non-fatal and only debug-logged. The user
    can finish recording without knowing that the MP4 was not produced.
  - **Proposed fix:** retain the first writer failure, stop accepting frames or mark the session
    degraded, and report a recoverable error with the preserved-frame fallback location.

- [x] **STAB-005: Release the raw Direct3D interop COM pointer**
  - **Evidence:** `src/Musio.Core/Capture/Direct3DDeviceHelper.cs:41-49`
  - `CreateDirect3D11DeviceFromDXGIDevice` returns `graphicsDevice`, which is projected with
    `MarshalInterface<IDirect3DDevice>.FromAbi` but is not explicitly released. Repeated device
    creation can retain COM references and GPU resources.
  - **Proposed fix:** verify CsWinRT ownership semantics and release the ABI pointer in `finally`
    after creating the projected object, while retaining the existing HRESULT and WARP fallback.

- [x] **PERF-001: Stop redrawing every timeline track on each playhead tick**
  - **Evidence:** `src/Musio.App/Controls/TimelineControl.xaml.cs:515-523`,
    `src/Musio.App/Controls/TimelineControl.xaml.cs:337-346`
  - A playhead change updates the visual and then calls `InvalidateAll`, repainting the ruler,
    thumbnails, waveforms, zooms, camera track, and cursor paths at playback frequency.
  - **Proposed fix:** isolate the playhead in its own overlay/composition visual. Keep track layers
    static until model, zoom, scroll, selection, or theme state changes.

- [x] **PERF-002: Preload wallpaper images outside frame composition**
  - **Evidence:** `src/Musio.Core/Processing/BackgroundCompositor.cs:158-181`
  - A wallpaper cache miss performs synchronous file I/O and GPU decode with
    `GetAwaiter().GetResult()` inside the render path, which can stall preview or export.
  - **Proposed fix:** asynchronously preload on style/path/device changes and publish a ready
    bitmap to the compositor. Render should use only cached resources or a fallback color.

- [ ] **PERF-003: Add a benchmarked capture-mode ladder**
  - **Evidence:** `learnings.md` section "Settings audit: recording FPS selector is not wired";
    `src/Musio.App/ViewModels/RecordingViewModel.cs:62`,
    `src/Musio.Core/Capture/VideoWriter.cs:157-214`
  - The current synchronous JPEG path is the dominant recording bottleneck. The learnings identify
    live H.264 as feasible but not a flag flip because it requires owned surfaces, a bounded queue,
    CFR/drop policy, checkpointing, and adapter-specific hardware validation.
  - **Proposed fix:** benchmark `Auto` modes in this order: validated live hardware H.264 60,
    validated live hardware H.264 30, then current JPEG/30 recovery mode. Never silently claim
    60 FPS when the unique-frame rate cannot sustain it.

---

## P2 - Bounded stability and resource-lifetime issues

- [ ] **STAB-006: Make hook-thread teardown observable and recoverable**
  - **Evidence:** `src/Musio.Core/Capture/MouseHookRecorder.cs:208-225`,
    `src/Musio.Core/Capture/KeyboardHookRecorder.cs:169-180`
  - Both recorders post `WM_QUIT`, wait two seconds, then discard the thread reference without
    checking whether the join succeeded. A stuck callback can leave a global hook/thread alive.
  - **Proposed fix:** check join results, explicitly unhook on timeout, retain diagnostic state,
    and surface teardown failure instead of silently continuing.

- [ ] **STAB-007: Replace fixed audio-stop sleep with callback drain completion**
  - **Evidence:** `src/Musio.Core/Capture/AudioCaptureEngine.cs:271-292`
  - Every audio stop sleeps the caller thread for 200 ms regardless of whether callbacks have
    drained, adding guaranteed latency while still being race-prone on slow devices.
  - **Proposed fix:** signal completion from `RecordingStopped`/last data callback and use a bounded
    wait tied to actual capture shutdown.

- [ ] **STAB-008: Guard stats subscribers on the timer thread**
  - **Evidence:** `src/Musio.Core/Capture/RecordingSession.cs:598-619`
  - `StatsUpdated` is invoked directly from `System.Threading.Timer`. A subscriber exception can
    escape a thread-pool callback and terminate the process.
  - **Proposed fix:** isolate subscriber failures and define the dispatch contract. Keep UI
    marshaling in the app layer, but do not allow one observer to crash recording.

- [ ] **STAB-009: Give `PreviewCanvas` deterministic unload cleanup**
  - **Evidence:** `src/Musio.App/Controls/PreviewCanvas.xaml.cs:13-19`,
    `src/Musio.App/Controls/PreviewCanvas.xaml.cs:112-149`
  - The control owns a render target, dispatcher timer, timer handler, and stopwatch but has no
    unload/dispose path. `EditorPage` pauses playback but never clears the final frame.
  - **Proposed fix:** on unload, stop and detach the timer, clear/dispose the current frame, stop
    the clock, and detach theme handlers using named delegates.

- [ ] **STAB-010: Do not reserve global shortcuts that perform no action**
  - **Evidence:** `src/Musio.App/App.xaml.cs:574-586`,
    `src/Musio.App/App.xaml.cs:726-740`
  - Ctrl+Shift+R/P/S are registered globally, but every handler is a TODO/no-op. Musio can consume
    shortcuts system-wide while giving no feedback.
  - **Proposed fix:** either wire each command through `ShellCoordinator`/`RecordingViewModel`, or
    do not register it until the feature is implemented.

- [ ] **STAB-011: Fix or quarantine the speech-to-text prototype**
  - **Evidence:** `src/Musio.Core/AI/SpeechToText.cs:99-125`; no production call sites
  - The file stream is opened but never supplied to the recognizer. `StartAsync()` starts normal
    continuous recognition instead of transcribing the requested WAV, so the method can listen to
    the wrong source and wait until its ten-minute timeout. The class is currently unwired.
  - **Proposed fix:** remove/quarantine the prototype until a real file-input transcription path
    exists, or replace it with an API that accepts the audio stream and has deterministic
    cancellation and event unsubscription.

- [ ] **PERF-004: Parallelize Gallery card metadata and poster loading with a bound**
  - **Evidence:** `src/Musio.App/Pages/OpenProjectsPage.xaml.cs:68-92`
  - Project cards are loaded one at a time: one worker task and one bitmap decode per project,
    awaited serially. Large libraries have unnecessarily long Gallery load times.
  - **Proposed fix:** read manifests/posters with bounded concurrency, preserve deterministic
    sorting, then create UI-bound bitmap objects on the dispatcher.

- [ ] **PERF-005: Cache stable background-composition resources**
  - **Evidence:** `src/Musio.Core/Processing/BackgroundCompositor.cs:199-268`
  - Blur transforms, shadow command lists/effects/geometries, and rounded clips are reconstructed
    for every frame even when output size and style are unchanged.
  - **Proposed fix:** cache size/style/device-dependent geometry and stable effects, invalidating
    only on style, dimensions, or device loss. Benchmark before caching objects whose Win2D
    lifetime rules make reuse unsafe.

- [ ] **PERF-006: Pre-index and cache keyboard overlay display data**
  - **Evidence:** `src/Musio.Core/Processing/KeyboardOverlayRenderer.cs:62-119`,
    `src/Musio.Core/Processing/KeyboardOverlayRenderer.cs:172-221`
  - Every frame reverse-scans events, rebuilds the combo string, creates text format/layout/
    geometry objects, and creates a second undisposed `CanvasTextFormat` for `DrawText`.
  - **Proposed fix:** precompute display intervals and labels, binary-search or advance an index by
    time, reuse format/layout while the label is unchanged, and dispose every Win2D resource.

- [ ] **PERF-007: Cache subtitle lookup and layout**
  - **Evidence:** `src/Musio.Core/AI/SubtitleBurner.cs:59-104`
  - Every composed frame linearly searches subtitle segments and recreates text format/layout even
    when the same subtitle remains active for seconds.
  - **Proposed fix:** keep an active segment index or binary-search sorted segments, and cache
    layout by subtitle/style/output size/device.

- [ ] **PERF-008: Avoid repeated camera-track scans per frame**
  - **Evidence:** `src/Musio.Core/Timeline/TimelineModel.cs:44-72`,
    `src/Musio.Core/Export/ExportEngine.cs:332-338`,
    `src/Musio.App/Pages/EditorPage.xaml.cs:1406-1415`
  - Camera lookup is linear, then opacity performs two more `Any` scans. Preview and export repeat
    this for every frame.
  - **Proposed fix:** normalize/sort camera segments after edits and use a moving index or interval
    lookup with precomputed predecessor/successor fade flags.

---

## P3 - Lower-cost optimizations and dead-code cleanup

- [ ] **PERF-009: Reduce legacy JPEG fallback file-system probes**
  - **Evidence:** `src/Musio.Core/Processing/JpegFrameSource.cs:43-66`
  - Opening eagerly enumerates and sorts the full frame directory, and every frame load performs a
    separate `File.Exists` probe before opening the file.
  - **Proposed fix:** keep the cached path list, remove the redundant existence probe, and consider
    lazy enumeration/index validation for very large failed-finalization sessions.

- [ ] **PERF-010: Defer or incrementally schedule startup cleanup**
  - **Evidence:** `src/Musio.App/App.xaml.cs:500-510`,
    `src/Musio.Core/Services/SessionCleanupService.cs:156-244`
  - Cleanup is off the UI thread, but full directory walks/deletes can still compete with project
    opening and media decode for disk bandwidth immediately after launch.
  - **Proposed fix:** start after first render/idle, process a bounded number of sessions per pass,
    and resume later.

- [ ] **PERF-011: Bound long-recording cursor memory**
  - **Evidence:** `src/Musio.Core/Capture/MouseHookRecorder.cs:119,255-263,528-541`
  - Throttling limits growth, but the complete cursor sample/click history is retained in memory,
    cloned for save, and only then released.
  - **Proposed fix:** for very long sessions, stream chunked cursor data or write periodic
    checkpoints while preserving the established MCUR compatibility rules.

- [ ] **DEAD-001: Remove the unused DPI heuristic**
  - **Evidence:** `src/Musio.Core/Processing/FrameCompositor.cs:256`; definition-only search result
  - `GetSystemDpiScale` has no call sites and contradicts the settled DPI playbook, which explicitly
    says dimension-ratio/system-DPI heuristics are unreliable for region capture.
  - **Proposed fix:** delete the method and retain the playbook note to prevent reintroduction.

- [ ] **DEAD-002: Decide whether `PerformanceMonitor` is product code or test-only code**
  - **Evidence:** `src/Musio.Core/Diagnostics/PerformanceMonitor.cs`; referenced only by
    `src/Musio.Tests/PerformanceMonitorTests.cs`
  - The monitor is not wired into capture/export. Its memory methods also create undisposed
    `Process` wrappers on every report.
  - **Proposed fix:** either integrate it into diagnostics/benchmark telemetry and dispose process
    wrappers, or remove the unused production class and its isolated tests.

- [ ] **DEAD-003: Remove the placeholder empty test**
  - **Evidence:** `src/Musio.Tests/Test1.cs`
  - `TestMethod1` has no assertions or behavior and adds noise to test counts.
  - **Proposed fix:** delete the placeholder test file.

- [ ] **DEAD-004: Wire the Default FPS setting or remove it from Settings**
  - **Evidence:** `src/Musio.App/Pages/SettingsPage.xaml:86-92`,
    `src/Musio.Core/Settings/AppSettings.cs:36-39`,
    `src/Musio.App/ViewModels/RecordingViewModel.cs:62`
  - The setting is displayed, but the page never reads/writes it and recording defaults to a
    hard-coded 30 FPS.
  - **Proposed fix:** preferably replace it with the benchmarked `Auto` capture-mode selection from
    PERF-003. Until then, remove the nonfunctional selector or wire the supported values honestly.

---

## Settled findings not to re-add without new evidence

These were investigated or fixed previously and are intentionally excluded from the backlog:

- Do not reuse one per-frame `VideoEncoder` output surface; the encoder consumes surfaces
  asynchronously and requires independently owned buffers.
- Do not dispose `Mp4FrameSource`/`VideoFrameReader` semaphores while callers may be waiting.
  Decoder disposal is deliberately bounded and app UI call sites defer it off the dispatcher.
- The bounded app-instance activation redirect is an intentional, device-verified tradeoff to
  prevent two windows from editing the same project. Do not replace it with a naive UI-thread
  `await` or local-open-on-timeout path.
- Text-slide blur and compositor zoom render targets were measured and exonerated as the cause of
  the prior preview freeze. The actual runaway renderer rebuild and crossfade duplication were
  fixed.
- Timeline Win2D geometries previously reported as leaks are now wrapped in `using`.
- Export per-frame task growth, frame-error propagation, cursor-renderer disposal, editor preview
  generation races, and decoder disposal on the UI thread have already been fixed.
