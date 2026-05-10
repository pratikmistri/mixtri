# Stability Pass — Todo

Comprehensive review of the Musio codebase targeting **experience stability & resilience**,
**memory leak & management**, and **performance**. Findings are grouped by focus area and
ordered by severity within each group.

---

## 1 · Experience Stability & Resilience

### Critical

- [ ] **GIF export uses disposed webcam frame** — `ExportEngine.cs ~217-223`
  GIF export sets `_webcamFrame` from a `using var`, which is disposed before
  `GifEncoder` calls `FrameCompositor.ComposeFrame`. The compositor reads a disposed bitmap.
  *Fix:* Keep the webcam frame alive through composition, or pass it directly into the
  compose call and dispose afterward.

- [ ] **SpeechToText never feeds audio file to recognizer** — `SpeechToText.cs ~101-111`
  `ContinuousRecognitionSession.StartAsync()` starts live mic recognition instead of
  consuming the requested audio file. Transcription hangs or captures wrong input.
  *Fix:* Use a file/audio-input transcription path that actually reads `audioFilePath`.

### High

- [ ] **D3D11 device creation errors silently ignored** — `Direct3DDeviceHelper.cs ~14-39`
  `D3D11CreateDevice` return code is not checked; failures surface as opaque COM errors.
  Also the raw `graphicsDevice` ABI pointer from `CreateDirect3D11DeviceFromDXGIDevice` is
  never released, leaking a COM ref per device creation.
  *Fix:* `Marshal.ThrowExceptionForHR(hr)` immediately; `Marshal.Release(graphicsDevice)` in
  `finally` after `FromAbi`.

- [ ] **Keyboard events lost on pause/resume** — `RecordingSession.cs ~405-425`, `KeyboardHookRecorder.cs ~140-143`
  Pause/resume stops and restarts the keyboard recorder, but `StartRecording()` clears
  `_events`, dropping all keys recorded before the first pause.
  *Fix:* Add real pause/resume support, or don't clear events on resume.

- [ ] **Recording shutdown relies on fixed sleeps** — `RecordingSession.cs ~280-287`, `AudioCaptureEngine.cs ~252-267`
  Fixed `Task.Delay(500)` / `Thread.Sleep(200)` guesses instead of waiting for outstanding
  writes to drain. Still racy, always pays delay.
  *Fix:* Track outstanding writes, await queue drain, and use completion tasks instead of
  time-based guesses.

- [ ] **Video export silently swallows per-frame errors** — `VideoEncoder.cs ~425-430, ~226-243`
  Per-frame exceptions are caught, `request.Sample` set to `null`, and export can end early
  with a truncated/corrupt video.
  *Fix:* Capture first frame error, cancel/abort transcode, and propagate exception back.

- [ ] **Preview rebuild races (no cancellation)** — `EditorPage.xaml.cs ~50-51, ~151, ~177-314`
  `InitializePreviewAsync` and `RebuildPreviewRendererAsync` can overlap; a stale completion
  can overwrite newer renderer/audio state or touch disposed objects.
  *Fix:* Introduce a `CancellationTokenSource`/version stamp; cancel prior work before
  starting new.

- [ ] **RegionBorderHighlight uses-after-free brush** — `RegionBorderHighlight.cs ~28-29, ~51-55, ~94-116`
  `Hide()` deletes the class background brush, but the window class stays registered. Later
  windows reference a freed brush; newly created brushes are leaked.
  *Fix:* Keep class brush alive for app lifetime, or unregister/re-register with a valid brush.

- [ ] **Blanket unhandled-exception swallowing** — `App.xaml.cs ~42-52`
  `OnUnhandledException` marks every exception as handled after logging. Can leave the app
  running in corrupt state.
  *Fix:* Only handle explicitly recoverable exceptions; otherwise log and shut down cleanly.

### Medium

- [ ] **StatsUpdated raised from Timer callback unguarded** — `RecordingSession.cs ~548-569`
  No exception isolation or UI-thread marshaling. Subscriber exception crashes the process;
  UI handlers may run on wrong thread.
  *Fix:* Wrap in `try/catch` and marshal to app dispatcher for UI subscribers.

---

## 2 · Memory Leaks & Management

### Critical

- [ ] **VideoWriter FinalizeAsync accumulates MediaClips** — `VideoWriter.cs ~229-245, ~247-303`
  One `MediaClip` per JPEG frame, none disposed. Linear memory/COM growth; can OOM or hang
  on long recordings.
  *Fix:* Dispose `MediaComposition` and all `MediaClip`s in `finally`/`using`; ideally
  replace with a streaming encoder (sink writer) that doesn't materialize the whole
  recording.

- [ ] **EditorPage leaks handlers & disposables on navigate** — `EditorPage.xaml.cs ~53-170, ~170-185`
  Many long-lived handlers (`RegisterPropertyChangedCallback`, Preview/Timeline events,
  `ExportVM.PropertyChanged`, `UndoRedoManager.StateChanged`) and disposable objects
  (`_frameReader`, `_previewRenderer`, `_audioPlayer`) are never cleaned up on Unloaded.
  *Fix:* Full teardown on Unloaded: unregister callbacks, detach handlers, dispose owned
  objects, null out references.

### High

- [ ] **MouseHookRecorder retains all samples in memory** — `MouseHookRecorder.cs ~103-118, ~184-196, ~254-261`
  All mouse samples/clicks kept in-memory for entire recording, then cloned on stop. Long
  sessions consume large RAM.
  *Fix:* Stream mouse data to disk incrementally; avoid full-list cloning on stop.

- [ ] **EditorViewModel never unsubscribes from singleton** — `EditorViewModel.cs ~22-25, ~131-145`
  Subscribes to `ProjectService.Instance.ProjectChanged` but never unsubscribes. Old VMs
  accumulate after navigation.
  *Fix:* Implement lifecycle-aware disposal; unsubscribe on page unload.

- [ ] **ExportViewModel never unsubscribes from singleton** — `ExportViewModel.cs ~20-31, ~33-55`
  Same issue as EditorViewModel with `ProjectService.Instance.ProjectChanged`.
  *Fix:* Add disposal/unsubscribe logic; dispose from EditorPage on unload.

- [ ] **VideoEncoder pendingSamples grows unbounded** — `VideoEncoder.cs ~203-247, ~273-279`
  One `Task` per frame stored; completed tasks never removed. Large task/closure graphs on
  long exports.
  *Fix:* Remove completed tasks as they finish, or use bounded active-task tracking.

- [ ] **VideoEncoder thumbnail streams never disposed** — `VideoEncoder.cs ~566-578, ~581-598`
  Frame extraction helpers create thumbnail streams/wrappers that are never disposed.
  *Fix:* Wrap in `using` or load directly from original stream.

- [ ] **ExportEngine GIF thumbnail streams never disposed** — `ExportEngine.cs ~254-266, ~272-285`
  Same undisposed thumbnail/stream issue in the GIF export hot path.
  *Fix:* Wrap in `using`.

- [ ] **CursorRenderer never disposes GPU resources** — `CursorRenderer.cs ~31-65`
  Owns `CanvasBitmap`/`CanvasGeometry` but never disposes them. Leaks GPU memory across
  preview/export sessions.
  *Fix:* Implement `IDisposable`; dispose from `FrameCompositor.Dispose()`.

- [ ] **SpeechToText leaks recognizer & stream** — `SpeechToText.cs ~55-123, ~139-145`
  Repeated calls overwrite `_recognizer` without disposing; handlers never unsubscribed;
  error paths skip `StopAsync`/stream disposal.
  *Fix:* Use local recognizer in `try/finally`; always stop session and dispose stream.

- [ ] **TimelineControl leaks CanvasGeometry per draw** — `TimelineControl.xaml.cs ~490-496, ~942-956, ~1048-1054`
  Transient `CanvasGeometry` objects never disposed during frequent redraws.
  *Fix:* Dispose with `using`, or cache/reuse until data changes.

- [ ] **RegionSelectorOverlay leaks GDI handles** — `RegionSelectorOverlay.xaml.cs ~143-176, ~180-183`
  GDI handles (`HDC`, `HBITMAP`) and `SoftwareBitmap` cleanup not in `finally`; bitmap
  never disposed after `SetBitmapAsync`.
  *Fix:* Wrap cleanup in `try/finally`; dispose `SoftwareBitmap` after assigning.

### Medium

- [ ] **VideoWriter CanvasDevice never disposed** — `VideoWriter.cs ~80-97, ~309-323`
  `CanvasDevice` created from D3D11 device is never disposed; leaks GPU/device resources
  across recordings.
  *Fix:* Track ownership; dispose in `Dispose()` when this class created it.

- [ ] **PreviewCanvas missing unload cleanup** — `PreviewCanvas.xaml.cs ~13-19, ~104-109, ~139-147`
  `_previewFrame` and `_playbackTimer` not cleaned up on unload. Timer keeps ticking; last
  render target stays retained.
  *Fix:* Stop timer, detach `Tick`, clear frame, dispose `CanvasRenderTarget` on unload.

- [ ] **PerformanceMonitor leaks Process handles** — `PerformanceMonitor.cs ~82-95, ~111-140`
  `Process.GetCurrentProcess()` returns disposable `Process`; handles never disposed during
  repeated metric polling.
  *Fix:* Wrap in `using`.

---

## 3 · Performance

### High

- [ ] **VideoWriter synchronous frame encoding on capture thread** — `VideoWriter.cs ~103-147`
  Each frame is JPEG-encoded and written to disk synchronously (`SaveAsync.GetAwaiter().GetResult()`
  under `_writeLock`). Capture throughput depends on disk/encoder latency; causes dropped
  frames under load.
  *Fix:* Offload encoding/writes to a dedicated background writer with bounded
  queue/backpressure; keep capture callback to fast copy/enqueue.

- [ ] **TimelineControl redraws everything on playhead move** — `TimelineControl.xaml.cs ~224-232, ~267-275, ~287-332, ~354-543`
  Every playhead tick invalidates all canvases—ruler, clips, zoom, waveforms, cursor path.
  Expensive at 30 FPS on long recordings.
  *Fix:* Separate static track rendering from playhead overlay; only redraw playhead during
  playback; invalidate full tracks only on zoom/scroll/model changes.

- [ ] **BackgroundCompositor sync-loads wallpaper in render path** — `BackgroundCompositor.cs ~148-160`
  `CanvasBitmap.LoadAsync(...).GetAwaiter().GetResult()` inside the frame composition path.
  Can stall preview/export threads on I/O.
  *Fix:* Preload background images asynchronously on config changes; use only cached
  resources during composition.

### Medium

- [ ] **BackgroundCompositor recreates GPU resources every frame** — `BackgroundCompositor.cs ~199-241, ~255-257`
  Blur/shadow/rounded-clip resources (`CanvasCommandList`, `ShadowEffect`, geometries,
  layers) are rebuilt per frame. Avoidable GPU resource churn in the hottest path.
  *Fix:* Cache static geometry/effect resources per output size/style, or pre-render stable
  content into reusable buffers.

- [ ] **CursorSmoother linear scan per frame** — `CursorSmoother.cs ~299-340`
  `EvaluateCatmullRom` linearly scans key points for every output frame. Scales poorly on
  long recordings.
  *Fix:* Use a moving segment index or binary search for O(1)/O(log n) per frame.

- [ ] **KeyboardOverlayRenderer rebuilds text per frame** — `KeyboardOverlayRenderer.cs ~67-121, ~178-215`
  Each frame rescans recent key events and rebuilds text format/layout/geometry even when the
  displayed combo hasn't changed.
  *Fix:* Precompute display intervals, cache active combo index, reuse `CanvasTextFormat` /
  layouts while text is unchanged.

- [ ] **SubtitleBurner rebuilds layout per frame** — `SubtitleBurner.cs ~67-105`
  Linear segment search and fresh `CanvasTextFormat`/`CanvasTextLayout` every frame while the
  same subtitle is active.
  *Fix:* Track current segment by index/time; cache layout per active subtitle text/style.
