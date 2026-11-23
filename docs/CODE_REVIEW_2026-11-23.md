# Full-codebase review — 2026-11-23

Second pass, run against `master` @ `073b928` (after PR #116 landed the fixes from
[CODE_REVIEW_2026-11-22.md](CODE_REVIEW_2026-11-22.md)). Eight parallel read-only Sonnet
reviewers, one per area, then spot-verification of the top findings during consolidation.

**✅ = verified by reading the code during consolidation.** Everything else is the reviewer's
reading — "plausible" items need a repro before fixing. Nothing was built or run (review host is macOS).

## Status of the 2026-11-22 review

Nearly everything is **fixed**: recording-stop safety, `CaptureStopped`, shutdown/tray-exit
during recording, replace-project prompt, save revision counter, export-on-navigate, audio
engine locking, exception-safe undo, atomic recents/presets, export filename, temp cleanup, mux
timeout, HW detector vtable, render-target leaks, `_bgImage` race, seek rebase, timeline pointer
lifecycle, shortcut focus, per-keystroke undo, debouncer flush, GIF timing, dead export options,
cached shadow/blur, settings dead controls, theme persistence, tray robustness.

**Still present:**
- Editor rebuilt on every nav-rail switch (no `NavigationCacheMode`).
- Preview composites at full capture resolution regardless of adaptive tier (`PreviewRenderer.cs:105-112`).
- Webcam cache rebuilt every frame (now via `UpdateStyle`, see P3).
- Waveforms: in-memory LRU only; every launch re-decodes.
- Timeline: no per-item keyboard/UIA path; tiny segments still hard to grab.

---

## Tier 1 — Data loss & crashes

| # | Area | Finding | Where |
|---|------|---------|-------|
| 1 | Audio capture | ✅ WAV header never flushed during recording (`Pcm16CaptureWriter.Flush()` has no callers). Crash/kill/power loss mid-take → `system_*.wav` / `mic_*.wav` read as empty. Fix: flush every ~5 s under `_lock`. | `Core/Capture/Pcm16CaptureWriter.cs:167`, `AudioCaptureEngine.cs` |
| 2 | Undo | ✅ Split ops mint new GUIDs on every `Execute`, so redo changes ids. A later redo of an op targeting a half (e.g. Remove) finds nothing, sets `_removedIndex = -1` but keeps stale `_removedSegment`; `Redo()` pushes it anyway → next Ctrl+Z does `Segments.Insert(-1, …)` and throws, and the op stays on top so **all earlier history is unreachable**. Fix: generate ids once and reuse on redo; reset per-run undo state in `ExecuteCore`; `Redo()` should honour `ChangedModel`. Same shape in `RemoveZoomKeyframeOperation`, `AutomaticTypingSpeedOperation`. | `Timeline/Operations/SegmentOperations.cs:205-255, 634-781`, `UndoRedoManager.cs:95-114` |
| 3 | Shell | ✅ `OnWindowClosing` never checks `IsRecording`. In a file-activated instance (no tray) closing the window mid-recording exits without finalizing the take; with a tray, the save prompt force-shows the window into the recording. | `App/App.xaml.cs:1142-1185` |
| 4 | Capture | ✅ Static-screen tail truncated: frames are only written when WGC delivers one; `StopAcceptingFrames` flushes only dropped-frame debt, not the idle time from last frame → Stop. Narrating over a still slide yields video shorter than audio/cursor. Fix: at stop, queue `slot(now) - lastEmittedSlot - 1` owed slots. | `Core/Capture/VideoWriter.cs:749-770`, `ScreenCaptureEngine.cs:249-275` |
| 5 | Capture | ✅ Gap fill does one `File.Copy` of the previous JPEG per missed slot, uncapped, no disk check — 10 idle minutes @30fps ≈ 18 000 copies (GBs). | `VideoWriter.cs:628-664` |
| 6 | Export | ✅ Hard 2-hour transcode cap even while progressing; long 4K software exports fail at the end and delete everything. Rely on the no-progress watchdog or scale by frame count. | `Core/Export/VideoEncoder.cs:60, 1288` |
| 7 | Export | ✅ Publishing over a locked destination (open in a player) throws after the full encode, then the finished staging file is deleted. Fall back to a unique sibling name. | `ExportJobFiles.cs:103`, `ExportEngine.cs:235-238` |
| 8 | Shell | Open-in-flight not guarded against a new recording: a slow `.mixtri` open completes and overwrites the fresh, unsaved take with no prompt. | `ShellCoordinator.cs:296-322`, `ProjectService.cs:317` |
| 9 | Shell | No autosave / crash recovery: any fatal exception loses all edits since last manual save; never-saved takes become orphaned folders. | `ProjectService.cs:278`, `App.xaml.cs:205-210` |
| 10 | Shell/Model | ✅ Extracted-project cache (`LocalAppData\OpenProjects\<id>`) is never pruned — every project ever opened keeps multi-GB copies forever (plus `alt-*`, `.part`). | `ProjectService.cs:190, 315` |
| 11 | Export | Stale `.mixtri_out_*` / `.mixtri_video_*` staging files are never swept if app + worker die together. | `ExportJobFiles.cs` |
| 12 | Shell | Plain launches aren't single-instance: a second Start-menu launch gets a second tray icon, failing hotkey, and can record concurrently. | `App.xaml.cs:626, 707-710` |

## Tier 2 — Visible glitches & broken interactions

**Playback / render correctness**
- `VideoFrameReader.GetFrameIndex` truncates `time.TotalSeconds * fps`, and export builds positions via `TimeSpan.FromSeconds(n/fps)` (tick-truncated) → many indices land one low (`0,0,1,3,3,4…`), i.e. duplicated/skipped frames on export and frame-step. Fix: floor with a small epsilon or round. `VideoFrameReader.cs:177`, `SegmentFrameComposer.cs:183, 706`. *Code path ✅; magnitude needs a Windows frame-hash test before fixing.* Same pattern in `FrameTimeConverter.TimeToFrameFloor`.
- Preview audio trails video by ~one device latency (reader position ≠ speaker position; `OutputLatencyMs = 100`). `AudioPlaybackEngine.cs:129, 762`, `EditorPage.Timeline.cs:1866`.
- One inserted track's sync correction ramps/seeks the **whole** mix → audible dip at every voice-over boundary. `AudioPlaybackEngine.cs:591-606`.
- Per-segment mute/volume changes are instant gain steps → clicks. `AudioPlaybackEngine.cs:655`.
- Speed edits: stretched audio engine disposed before re-render, render uncancellable → all placed audio silent for seconds. `EditorPage.Timeline.cs:2105, 2149`.
- `BackgroundCompositor` compares devices with `ReferenceEquals` (10 sites) while sibling caches use `Win2DUtils.SameDevice` → possible wallpaper re-decode per frame. *Plausible.*
- Blur background and OrganicWave slides use `BorderMode.Hard` → dim rims / moving edge bands. *Plausible.*
- Shadow opacity likely applied twice (mask alpha × `ShadowColor` alpha). *Plausible.*

**Editor UX**
- ✅ Every text-overlay slider / colour-picker tick executes its own undo op (no merging) and triggers a full resync → one drag = dozens of Ctrl+Z, sluggish drag. `EditorPage.TextEditing.cs:866-1075`.
- ✅ Any scene-style edit resets shadow/border to hard-coded values (blur 24, opacity 0.5, `#000000`, border 1 `#333333`), wiping preset shadows. `EditorPage.Style.cs:889-894`.
- Import / append wipe the entire undo history (`OnProjectChanged` always builds a new `UndoRedoManager`). `EditorViewModel.cs:117-135`.
- Webcam overlay drag lacks `PointerCaptureLost`/`Canceled` → stuck drag, position not saved. `EditorPage.xaml:111-116`.
- Empty-state reset leaves a draggable webcam rect and edit modes/selection ids alive. `EditorPage.Timeline.cs:1008-1036`, `Preview.cs:310-362`.
- ✅ Imported-video audio gets no waveform (lookup by `system_`/`mic_` prefix; import writes `audio.wav`). Use `RecordedAudio.IsMic`. `EditorPage.Timeline.cs:1631-1634`.
- Slide animation-window debouncer not flushed on selection change → edit lost / no-op undo on the other slide.
- Overlay trim/speed can overlap lane-mates; `SplitAndInsertTextSlide` lacks `MinHalf` / overlay guards.

**Timeline control**
- ✅ Wheel/zoom not blocked during a drag → pixel↔time mapping changes mid-gesture, drop lands wrong. `TimelineControl.xaml.cs:8907`.
- No edge auto-scroll while dragging when zoomed; no Escape-to-cancel; second pointer overwrites drag state.
- No Draw handler has try/catch — one bad frame can take down the app.
- Inserted-audio lanes: click moves playhead but doesn't scrub.

**Capture**
- Minimized window target records nothing, no warning until Stop.
- Webcam unplug mid-recording silent (no `MediaCapture.Failed`).
- Mic/output default-device switch mid-recording → silent track, no warning; no recovery after `DeviceFailed`.
- Picker launch handlers are `async void` with no catch. `RecordToolbarControl.xaml.cs:290-294`.
- Window capture: cursor/click offset frozen at first frame; moving the window misaligns overlays.

**Export**
- Cancel at the very end can overwrite the destination yet report "cancelled".
- `ForceSoftwareRendering` never set → GPU-less/RDP machines fail instead of using WARP.
- Legacy audio path ignores `AudioToVideoOffsetSeconds`.
- GIFs likely don't loop (no NETSCAPE extension). Subtitle burn-in path is dead (`SubtitleStyle` never set).
- Video import: audio extraction failure/timeout silently produces a mute video.

**Shell**
- Redirected open can swap the project under a running export.
- ContentDialogs ignore the chosen app theme.
- Settings webcam toggle swallowed during device enumeration.
- `SavePackageAsync` renames the project before the save succeeds.

## Tier 3 — Performance

- ✅ Scrub/click release calls full `InvalidateAll()` (bumps model revision, 8 canvases) even when only the playhead moved. `TimelineControl.xaml.cs:7317-7326`. Wheel events do the same even when nothing changed.
- ✅ Per-draw `CanvasTextFormat` / `CanvasStrokeStyle` in timeline loops (`:2119`, `:3702`, `:5350`, `:8519`, …).
- `WebcamCompositor.UpdateStyle` invalidates caches unconditionally and is called every preview tick (`WebcamCompositor.cs:82-86`, `EditorPage.Preview.cs:2200`) — early-return when the style record is equal.
- `Mp4FrameSource` allocates + blits one full-res render target per decoded frame (`:340-354`). Pool them.
- Preview still composites at full capture resolution (see "still present").
- Cursor logs parsed synchronously on the UI thread on project init and for appended clips; every style commit disposes appended decoders and re-parses. `EditorPage.Preview.cs:537, 2007`, `Timeline.cs:67`.
- Recorded-audio engine load/rebuild on the UI thread. `EditorPage.Timeline.cs:1721, 2797`.
- Every save re-copies/re-hashes all media into a new zip, even for a text edit.
- Audio capture does disk I/O under a single lock shared by both WASAPI callbacks.
- Gallery: unbounded discovery, serial per-card zip reads, no incremental display.
- Wallpaper tiles decoded eagerly; slide colour pickers repaint every timeline canvas per tick; webcam/text-edit layout rewritten from the Draw callback every frame.

## Tier 4 — UX & accessibility

- Browser/WinUI password fields still recorded to `keyboard.mkbd` (redaction is Win32-edit only); add a per-recording "record keystrokes" toggle.
- Timeline items have no keyboard path or UIA peers.
- Slow clock drift on continuous audio streams never corrected (~0.1–0.4 s/hr).
- `.ogg` offered in audio import but likely unsupported.
- Hook-start timeout leaves an orphan low-level hook thread.

## Suggested fix order

1. **Data safety**: WAV flush (#1), undo redo-id corruption (#2), close-during-recording (#3), static-tail + gap-fill (#4, #5), export cap & locked publish (#6, #7), open-vs-record guard (#8).
2. **Disk hygiene**: prune `OpenProjects` cache and stale export staging (#10, #11).
3. **Visible glitches**: frame-index rounding (verify first), slider undo coalescing, style-reset of shadow/border, wheel-during-drag, audio latency/dip/clicks.
4. **Performance**: scrub-release invalidation, per-draw allocations, webcam `UpdateStyle`, frame-source RT pool, UI-thread cursor/audio loads.
5. **Robustness/UX**: single-instance, autosave/recovery, device-change handling, minimized-window warning, keystroke privacy toggle.
