# Full-codebase review — 2026-11-22

Eight parallel read-only reviewers (Sonnet), one per area, followed by spot-verification of the
top findings against the code. **✅ = verified by reading the code during consolidation.**
Everything else is the reviewer's reading; "plausible" items need a repro before fixing.

## Tier 1 — Data loss & crashes (fix first)

| # | Area | Finding | Where |
|---|------|---------|-------|
| 1 | Capture | ✅ Unguarded cursor/keyboard side-file save (or `StopCapture` COMException) aborts MP4 finalization → recording lost, JPEGs orphaned | `Core/Capture/RecordingSession.cs:438-452` |
| 2 | Capture | ✅ `CaptureStopped` never subscribed — closing the target window / unplugging a monitor leaves a silent dead recording | `ScreenCaptureEngine.cs:251-275`, `RecordingSession.cs:283` |
| 3 | Capture | Zero-frame recording builds a 0×0 project pointing at a non-existent MP4, no fault | `RecordingSession.cs:451, 500-512` |
| 4 | Capture | Failed stop leaves stale `LastProject` → shell opens / re-appends the *previous* take | `RecordingViewModel.cs:198, 303-309` |
| 5 | Capture | Timer-thread NRE race in `UpdateElapsedDisplay` (`_session` read twice) can crash process | `RecordingViewModel.cs:426-455` |
| 6 | Shell | ✅ `WM_QUERYENDSESSION` starts quiesce immediately and skips unsaved-work prompt; a vetoed shutdown still kills the app | `App/MainWindow.xaml.cs:78-82` |
| 7 | Shell | Tray Exit / OS shutdown during recording: no stop/finalize, take lost | `App.xaml.cs:810-836, 910-922` |
| 8 | Shell | ✅ New recording replaces an edited project with no unsaved-work prompt (only Gallery/Editor/App call the guard) | `RecordingViewModel.SetProject`, `ShellCoordinator.StartRecordingAsync` |
| 9 | Shell | Save marks project clean even if user edited during the save; `IsSaveInFlight` bool breaks on overlapping saves | `ProjectService.SavePackageAsync` |
| 10 | Editor | Navigating away from the editor silently cancels an in-flight export (`Unloaded` → `ExportVM.Cleanup()` → Cancel) | `EditorPage.xaml.cs:538`, `ExportViewModel.cs:53-57` |
| 11 | Audio | ✅ `Load`/`LoadPlacements`/`Dispose` tear down readers without `_transportLock` while the scrub worker indexes them; no try/catch in `ProcessScrubQueue` → pool-thread crash | `Core/Audio/AudioPlaybackEngine.cs:174-310, 590-702` |
| 12 | Model | ✅ `UndoRedoManager` not exception-safe: throw mid-Execute leaves half-applied un-undoable edit; throw in Undo drops op from both stacks | `Core/Timeline/UndoRedoManager.cs:24-42` |
| 13 | Model | ✅ Recents index & presets written non-atomically; a crash mid-write + swallowed parse error wipes up to 60 recents | `RecentProjectsStore.cs:234`, `PresetManager.cs:62,81` |
| 14 | Model | Save refuses entirely if any optional sidecar (`.mcur`, `.mkey`, webcam, audio) is missing | `MixtriPackageService.cs:89-101` |
| 15 | Export | ✅ Save-picker filename ignored — only the folder is passed; engine writes `{ProjectName}.mp4` and leaves the user's 0-byte file | `ExportViewModel.cs:505`, `ExportEngine.cs:115-117` |
| 16 | Export | Cancel/failure leaves truncated output and multi-GB `.mixtri_video_*` temp files | `VideoEncoder.cs:~278-300`, `GifEncoder.cs` |
| 17 | Export | Audio mux has a fixed 5-min timeout after a full encode — long/4K exports fail at 99% and lose all work | `VideoEncoder.cs:~1010-1090` |
| 18 | Export | ✅ `HardwareEncoderDetector` uses wrong `IMFAttributes` vtable slots (33/20 vs 13/10) — calls `ActivateObject` on every HW MFT each export | `HardwareEncoderDetector.cs:169, ~194` |
| 19 | Render | `FrameCompositor` leaks full-res output render target when any draw step throws (retried every tick) | `FrameCompositor.cs:1152-1191, 1245-1281` |
| 20 | Render | `TextSlideRenderer._bgImage` swapped/disposed off-thread while render thread draws it | `TextSlideRenderer.cs:293-346` |

## Tier 2 — Visible glitches & broken interactions

- ✅ **Seek/scrub during playback is undone by the next tick** — `OnPlaybackTick` uses `_playbackStartPosition + clock`, never rebased on external playhead changes. `App/Controls/PreviewCanvas.xaml.cs:329-345, 377`
- ✅ **Ruler / System-audio / Mic lanes leave `_dragMode=Playhead` stuck** (Pressed only, no Moved/Released/CaptureLost) → playhead follows hover. `TimelineControl.xaml:125-184`, `.cs:6371`
- **Camera/Text/Voice/Music lanes and EditorPage zoom-region canvas lack `PointerCaptureLost`/`Canceled`** → stuck drags. `TimelineControl.xaml.cs` 7588/8021/4807; `EditorPage.xaml:121-126`
- Any mouse button starts timeline drags (no `IsLeftButtonPressed` check); right-click on Camera/Text block deletes immediately.
- Drag commit handlers not in try/finally; model changes / undo don't prune stale selections or abort in-flight drags.
- ✅ **ZoomBlur transition dims mid-way** (both layers partial-opacity over black — same bug CrossFade already fixed). `TransitionRenderer.cs:~454, ~480`
- ✅ **GIFs play ~10% fast** (`1000/fps/10` truncates 3.33cs→3cs). `GifEncoder.cs:106-107`
- Plain `Load` path lacks `SilencePaddedSampleProvider` → shorter track (mic) drops out permanently after reaching EOF.
- Pause/seek/scrub hard-stop `WaveOutEvent` → clicks; resampler not reset on seek → stale audio.
- WASAPI loopback writes nothing during silence → system audio drifts earlier than video after quiet stretches.
- Keyboard shortcuts dead after clicking any toolbar button (any `ButtonBase` counts as interactive); Space re-triggers the focused destructive button. `EditorPage.Shortcuts.cs:283-306`
- Overlay text box: one undo entry + full undo-state refresh per keystroke. `EditorPage.TextEditing.cs:632-643`
- Debounced style edits dropped on unload/reload (`Stop()` not `Flush()`); suppress flags not in try/finally; Escape commits instead of cancels text edit.
- 60fps and WebM offered in export but silently become 30fps / MP4; aspect-ratio option ignored.
- Ctrl+wheel zoom unanchored, unbounded on touchpads; zoomed timeline never follows playhead.
- Zoom-segment resize clamps to `model.Duration` → negative `PostDuration`. `ZoomOperations.cs:230-241`
- ✅ No-op split pushes an undo entry (`return false` instead of `NothingToDo()`). `SegmentOperations.cs:780`
- Index-based undo in Trim/Speed/AudioMode ops; transition ops use full-list index instead of base-segment index.

## Tier 3 — Performance

- Preview composites at full capture resolution regardless of adaptive tier (4K/5K = 33–60 MB render-target churn per frame). `PreviewRenderer.cs:105-112`
- Timeline drags call `InvalidateAll()` (8 canvases + `UpdateTrackVisibility`) on every pointer sample; zoom bands/scrim/anchors recomputed per redraw; playhead moved via `Margin` (re-measure per tick).
- Background shadow geometry + blur rebuilt every frame; full-canvas σ=30 blur backgrounds; OrganicWave slide gradient runs full-res turbulence per frame; webcam cache rebuilt per frame during fullscreen animation.
- Frame reader: two render-target allocs + blits per decoded frame, cache never hits during playback. `Mp4FrameSource.cs:293-312`, `VideoFrameReader.cs:182-211`
- Mouse recording re-read/parsed on UI thread on every style slider tick. `EditorPage.Style.cs:1062-1067`
- Webcam thumbnail re-extracted on every rendered frame. `EditorPage.Preview.cs:2123-2195`
- Waveforms decode entire files on every open, no sidecar cache.
- Recording stop = full software re-encode with no progress, 30-min cap.
- Region/window picker screenshot (~100 MB for 3×4K) built synchronously on UI thread.
- Undo stack unbounded; every save re-zips all media with `CompressionLevel.Optimal`.
- Per-frame `CanvasTextFormat`/`CanvasTextLayout` allocations (timeline, keyboard overlay — leaked, subtitle burner, per-char text animation).

## Tier 4 — UX & accessibility

- Mid-recording faults go to hidden windows; overlay has no error/low-disk state; no free-space check.
- Audio device failures (mic privacy denied, unplug) silently swallowed.
- Keyboard hook records every keystroke (incl. passwords) to `keyboard.mkbd` unconditionally — privacy.
- Webcam start failure blocks the whole recording.
- Record hides the app before validating the target; Full Screen picks first enumerated monitor, not primary.
- Export cancel has no "Cancelling…" state; ETA inaccurate; mux shows "99%, 2s" for minutes; super-res setup has no progress.
- Settings page: Default FPS, capture mode, audio devices are dead controls; Theme not persisted.
- Min window size 1024×768 DIP exceeds 1080p@150% work area.
- Gallery open: no progress, no re-entry guard; cards have no accessible name.
- Tray icon not restored after Explorer restart; 16px icon blurry at high DPI; tray WndProc has no try/catch.
- Back button desyncs nav rail; editor torn down on every rail switch (no `NavigationCacheMode`).
- Timeline has no keyboard path and minimal UIA names; small segments (<16px) can't be moved.
- `SpeechToText` is unused but broken (listens to live mic, wall-clock timestamps) — delete or rewrite.

## Suggested fix order

1. **Recording safety** (Tier 1 #1–8): guard each stop step, subscribe `CaptureStopped`, guard recording against exit/shutdown/unsaved project, fix `WM_QUERYENDSESSION`.
2. **Edit safety** (#9–14): save revision counter, export-on-navigate guard, audio-engine locking, exception-safe undo, atomic writes.
3. **Export correctness** (#15–18 + GIF timing, dead options).
4. **Interaction glitches**: playback seek rebase, timeline pointer lifecycle (one shared reset for all canvases), shortcut focus guard.
5. **Performance**: tier-scaled preview composition, drag-time invalidation, cached shadow/blur, frame-reader copy.
