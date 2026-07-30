# Musio — Screen Studio Clone for Windows

## Problem Statement
Windows Snipping Tool drops frames during screen recording, and OBS Studio consumes excessive memory. Musio aims to be a lightweight, native Windows screen recording tool that replicates Screen Studio's cinematic recording experience: smooth cursor effects, auto-zoom, customizable backgrounds, timeline editing, and professional export — all with zero frame drops.

## Tech Stack
- **Framework**: WinUI 3 (Windows App SDK) — native C#, Fluent Design, minimal memory
- **Runtime**: .NET 8+
- **Screen Capture**: `Windows.Graphics.Capture` API (modern, window-level + full-screen, cursor excluded by design)
- **2D Rendering**: Win2D (`Microsoft.Graphics.Win2D`) — GPU-accelerated Direct2D for timeline, cursor effects, canvas composition
- **Video Encoding**: Media Foundation + `Windows.Media.Transcoding` for hardware-accelerated export (NVENC/QSV/AMF auto-detected)
- **Composition**: Custom rendering pipeline via Direct3D11 interop for combining screen + cursor + zoom + background layers
- **Storage**: Local SQLite (via `Microsoft.Data.Sqlite`) for project metadata, presets, region memory
- **Audio**: `Windows.Media.Capture` / WASAPI for system audio + microphone

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                     Musio WinUI 3 App                   │
├──────────┬──────────┬──────────┬────────────┬───────────┤
│  Region  │ Capture  │ Timeline │  Composer  │  Export   │
│ Selector │  Engine  │  Editor  │   Engine   │  Pipeline │
├──────────┼──────────┼──────────┼────────────┼───────────┤
│ Overlay  │ WGC API  │ Win2D    │ Direct3D   │ Media     │
│ Window   │ + Mouse  │ Canvas   │ Composition│ Foundation│
│          │ Hooks    │          │            │           │
└──────────┴──────────┴──────────┴────────────┴───────────┘
```

### Data Flow
1. **Recording**: WGC captures screen frames → raw video (no cursor). Mouse hook captures cursor position + clicks at high frequency (240Hz+). Audio captured separately.
2. **Storage**: Raw video stored as temp MP4 (HW-encoded). Cursor data stored as timestamped JSON. Click events stored with timestamps.
3. **Post-Processing**: On export — cursor path smoothed → custom cursor rendered → auto-zoom applied → background/padding/shadow composited → final video encoded.

---

## Feature Specification

### F1: Screen Region Selection
- Transparent overlay window for drawing a capture rectangle
- Resize handles + drag to reposition
- Full-screen / window / custom-region modes
- **Remembers last selected region** (persisted to local settings)
- Multi-monitor support (enumerate displays, select target)
- Visual guides: crosshair, dimension labels, snap-to-window

### F2: High-Performance Screen Capture
- `Windows.Graphics.Capture` via `GraphicsCaptureItem` + `Direct3D11CaptureFramePool`
- Configurable FPS: 30 / 60 fps
- **No frame drops**: dedicated capture thread, frame pool with 2-buffer swap
- System cursor hidden during capture (`IsCursorCaptureEnabled = false`)
- Captures to GPU texture, hardware-encoded in real-time to temp file via Media Foundation
- Session management: start / pause / resume / stop

### F3: Mouse Path & Click Recording
- Low-level mouse hook (`SetWindowsHookEx` WH_MOUSE_LL) for high-frequency position sampling (240Hz+)
- Records: `{ timestamp_ms, x, y, button, event_type }` for each sample
- Click events: button down/up with precise timestamps
- Scroll events: direction + delta
- All data stored as binary or compact JSON alongside the video

### F4: Cursor Effects (Core Differentiator)
- **Cursor hiding**: System cursor hidden during capture; custom cursor rendered in post-processing
- **Path smoothing algorithms** (user-selectable):
  - Spring Physics (damped spring model — Screen Studio's primary approach)
  - One Euro Filter (low-pass filter, good for jitter removal)
  - Catmull-Rom Spline interpolation
  - Configurable smoothing strength: None / Subtle / Medium / Smooth / Ultra Smooth
- **Custom cursor styles**:
  - macOS-style pointer (default, high-res SVG)
  - System cursor replica
  - Custom uploaded cursor image
  - Adjustable cursor size (0.5x – 3x)
- **Click animation**: On mouse-down, cursor scales down to ~80% over 100ms then springs back to 100% on mouse-up (easing: cubic-bezier) — mimics Screen Studio's "press" effect
- **Click highlight**: Optional ripple/pulse circle emanating from click point
- **Motion blur**: Optional per-frame motion blur based on cursor velocity
- **Auto-hide**: Cursor fades out after N seconds of inactivity, fades back on movement
- **Cursor loop**: For seamless looping clips, interpolate cursor from end position back to start

### F5: Auto-Zoom
- Detects mouse clicks → smoothly zooms in to click area
- Zoom interpolation: spring-based easing (same physics as cursor smoothing)
- Configurable:
  - Zoom level (1.5x – 4x, default 2x)
  - Pre-click duration (how early zoom starts before click, 0.3–1.0s)
  - Post-click hold (how long zoom stays, 0.5–2.0s)
  - Ease-out duration (zoom returns to 1x)
- Manual zoom points: user can add/edit zoom keyframes on the timeline
- Scroll-triggered zoom: optional zoom on scroll events
- Typing-triggered zoom: optional zoom when keyboard input detected in a focused area

### F6: Background & Styling
- **Background types**:
  - Solid color (hex picker)
  - Linear/radial gradient (2-color with angle/center controls)
  - Wallpaper image (upload or built-in presets)
  - Blur of captured content
- **Frame styling**:
  - Padding / inset (0–200px, uniform or per-side)
  - Corner radius (0–50px)
  - Drop shadow (offset, blur, opacity, color)
  - Border (color, width)
- **Device frame**: Optional macOS/Windows window chrome around the recording
- **Brand presets**: Save named combinations of all background/styling settings

### F7: Timeline Editor
- Custom Win2D-rendered timeline control
- **Track layers**: Video, Cursor overlay, Zoom keyframes, Audio waveform
- **Trimming**: Drag handles to trim start/end
- **Cutting**: Split at playhead, delete segments
- **Speed control**: Select segment → adjust speed (0.25x – 4x) with ramp-in/out
- **Playhead scrubbing**: Drag to preview any point
- **Frame thumbnails**: Extract key frames along the timeline
- **Zoom keyframe editing**: Add/remove/adjust zoom points with handles
- **Undo/Redo**: Full history stack

### F8: Audio
- **System audio capture**: WASAPI loopback for desktop audio
- **Microphone capture**: Selectable input device
- **Simultaneous recording**: Both system + mic as separate tracks
- **Noise reduction**: Basic noise gate / spectral subtraction (on-device)
- **Volume leveling**: Normalize audio levels
- **Audio waveform** in timeline for visual editing

### F9: Export Pipeline
- **Resolutions**: 720p, 1080p, 2K, 4K (auto-detect source resolution)
- **Frame rates**: 30fps, 60fps
- **Formats**: MP4 (H.264/H.265), GIF, WebM (VP9)
- **Aspect ratios**: Auto (source), 16:9, 9:16 (vertical/mobile), 1:1, 4:3, 3:4
  - When aspect ratio changes, auto-zoom recalibrates to keep content centered
- **Quality presets**: Draft (fast), Standard, High, Ultra
- **Hardware acceleration**: Auto-detect GPU encoder (NVENC / QSV / AMF), fallback to CPU
- **Copy to clipboard**: Export result directly to clipboard
- **Export presets**: Save named export configurations

### F10: Webcam Overlay
- Capture webcam via `MediaCapture` API
- Circular or rectangular crop with corner radius
- Draggable position (corner presets: bottom-left, bottom-right, etc.)
- Adjustable size (small / medium / large)
- Border / shadow styling to match background theme
- Toggle on/off during recording

### F11: Keyboard Shortcut Display
- Detect keypress events during recording
- Render on-screen overlay showing pressed keys (e.g., "Ctrl + S")
- Configurable position, style, fade duration
- Filter: show only modifier combos or all keys

### F12: AI Captions / Subtitles
- On-device speech-to-text using Windows Speech Recognition or Whisper.cpp
- Auto-generate SRT/VTT subtitle tracks
- Burn-in subtitles during export (customizable font, size, position, background)
- Edit text in timeline view

### F13: Settings & Preferences
- Default capture region memory
- Default FPS, resolution, format
- Hotkeys: start/stop/pause recording, take screenshot
- Theme: Light / Dark / System
- Auto-save projects
- Startup behavior (minimize to tray, etc.)
- Update checker

---

## Project Structure

```
Musio/
├── Musio.sln
├── src/
│   ├── Musio.App/                     # WinUI 3 App (entry point, App.xaml)
│   │   ├── Views/                     # XAML pages
│   │   │   ├── MainWindow.xaml        # Shell with navigation
│   │   │   ├── RecordingPage.xaml     # Pre-recording setup UI
│   │   │   ├── EditorPage.xaml        # Post-recording editor
│   │   │   ├── ExportPage.xaml        # Export settings
│   │   │   └── SettingsPage.xaml      # App settings
│   │   ├── Controls/                  # Custom WinUI controls
│   │   │   ├── TimelineControl.xaml   # Timeline editor
│   │   │   ├── RegionSelector.xaml    # Region selection overlay
│   │   │   ├── CursorPreview.xaml     # Cursor style preview
│   │   │   └── BackgroundPicker.xaml  # Background customization
│   │   ├── ViewModels/               # MVVM ViewModels
│   │   ├── Converters/               # Value converters
│   │   └── Assets/                   # Icons, cursors, wallpapers
│   │
│   ├── Musio.Core/                    # Core logic library (.NET class library)
│   │   ├── Capture/
│   │   │   ├── ScreenCaptureEngine.cs     # WGC integration
│   │   │   ├── MouseHookRecorder.cs       # Low-level mouse hook
│   │   │   ├── AudioCaptureEngine.cs      # WASAPI audio capture
│   │   │   ├── WebcamCaptureEngine.cs     # Webcam via MediaCapture
│   │   │   └── RecordingSession.cs        # Orchestrates all capture streams
│   │   ├── Processing/
│   │   │   ├── CursorSmoother.cs          # Spring physics, One Euro, Catmull-Rom
│   │   │   ├── CursorRenderer.cs          # Custom cursor drawing + click animation
│   │   │   ├── AutoZoomEngine.cs          # Click-triggered zoom with interpolation
│   │   │   ├── BackgroundCompositor.cs    # Background/padding/shadow rendering
│   │   │   └── FrameCompositor.cs         # Combines all layers into final frames
│   │   ├── Timeline/
│   │   │   ├── TimelineModel.cs           # Timeline data model
│   │   │   ├── EditOperation.cs           # Trim, cut, speed change
│   │   │   └── UndoRedoManager.cs         # Edit history
│   │   ├── Export/
│   │   │   ├── ExportEngine.cs            # Orchestrates export pipeline
│   │   │   ├── HardwareEncoderDetector.cs # Detect NVENC/QSV/AMF
│   │   │   ├── VideoEncoder.cs            # Media Foundation encoding
│   │   │   └── GifEncoder.cs              # GIF export
│   │   ├── Audio/
│   │   │   ├── NoiseReduction.cs          # Basic noise gate
│   │   │   └── AudioMixer.cs             # Mix system + mic audio
│   │   ├── AI/
│   │   │   └── SpeechToText.cs            # On-device transcription
│   │   ├── Models/
│   │   │   ├── Project.cs                 # Project file model
│   │   │   ├── CursorData.cs              # Mouse position + click data
│   │   │   ├── ExportPreset.cs            # Export configuration
│   │   │   └── BrandPreset.cs             # Background/styling preset
│   │   └── Settings/
│   │       ├── AppSettings.cs             # App-wide settings
│   │       └── RegionMemory.cs            # Remember last capture region
│   │
│   └── Musio.Tests/                   # Unit + integration tests
│       ├── CursorSmootherTests.cs
│       ├── AutoZoomEngineTests.cs
│       └── TimelineModelTests.cs
│
├── assets/
│   ├── cursors/                       # Custom cursor SVGs/PNGs
│   ├── wallpapers/                    # Built-in background images
│   └── icons/                         # App icons
│
└── docs/
    └── architecture.md                # Technical architecture doc
```

---

## Implementation Todos

### Phase 1: Project Foundation
- `setup-solution` — Create WinUI 3 solution with Musio.App, Musio.Core, Musio.Tests projects. Add NuGet packages: Microsoft.Graphics.Win2D, Microsoft.Data.Sqlite, CommunityToolkit.Mvvm
- `app-shell` — Build MainWindow with NavigationView (Recording, Editor, Export, Settings pages). Implement MVVM infrastructure with CommunityToolkit.Mvvm
- `settings-infra` — Implement AppSettings using Windows.Storage.ApplicationData for persisting preferences, region memory, and presets

### Phase 2: Screen Capture Core
- `region-selector` — Build transparent overlay window for screen region selection with resize handles, snap-to-window, dimension labels. Persist last region to settings
- `screen-capture-engine` — Implement ScreenCaptureEngine using Windows.Graphics.Capture with Direct3D11CaptureFramePool, dedicated capture thread, configurable FPS, real-time HW encoding to temp file
- `mouse-hook-recorder` — Implement low-level mouse hook (WH_MOUSE_LL via P/Invoke) sampling at 240Hz+. Record position, button events, scroll events with precise timestamps
- `audio-capture` — Implement system audio (WASAPI loopback) and microphone capture as separate streams. Device enumeration and selection
- `recording-session` — Orchestrate all capture engines: synchronized start/stop, pause/resume, session metadata, temp file management

### Phase 3: Cursor Effects & Post-Processing
- `cursor-smoother` — Implement cursor path smoothing: Spring Physics (damped spring), One Euro Filter, Catmull-Rom spline. Configurable strength levels
- `cursor-renderer` — Render custom cursor overlay: load cursor images (SVG/PNG), position at smoothed coordinates, adjustable size. Click animation (scale down/up with cubic-bezier easing). Click highlight ripple. Motion blur. Auto-hide on inactivity
- `auto-zoom-engine` — Implement click-triggered auto-zoom: detect clicks → spring-based zoom interpolation to click area. Configurable zoom level, pre/post durations, easing. Manual zoom keyframe support
- `background-compositor` — Render backgrounds: solid color, gradient, image, blur. Apply padding, corner radius, drop shadow, border. Optional device frame chrome

### Phase 4: Composition & Export
- `frame-compositor` — Combine layers: background → screen video → cursor overlay → zoom transform → final frame. GPU-accelerated via Direct3D11/Win2D
- `export-engine` — Export pipeline: read source video → apply per-frame composition → encode via Media Foundation (HW-accelerated). Support 720p–4K, 30/60fps, H.264/H.265
- `gif-encoder` — GIF export for short clips using frame extraction + quantization
- `aspect-ratio-support` — Implement aspect ratio transforms (16:9, 9:16, 1:1, 4:3). Auto-recalibrate zoom/pan for each ratio
- `export-presets` — Save/load named export configurations (resolution, format, quality, aspect ratio)

### Phase 5: Timeline Editor
- `timeline-control` — Custom Win2D-rendered timeline control: playhead, time ruler, scrubbing, zoom in/out on timeline
- `timeline-tracks` — Render multiple tracks: video thumbnails, cursor path visualization, zoom keyframes, audio waveform
- `trim-cut-operations` — Implement trim (drag handles), cut (split at playhead), delete segment. Update composition model
- `speed-control` — Select timeline segment → adjust playback speed (0.25x–4x) with ramp transitions
- `undo-redo` — Full undo/redo stack for all timeline operations
- `preview-playback` — Real-time preview of edited video with all effects applied (lower quality for performance)

### Phase 6: Advanced Features
- `webcam-overlay` — Webcam capture via MediaCapture API, circular/rectangular crop, position/size controls, border/shadow
- `keyboard-overlay` — Detect keypresses during recording, render key combo overlay with configurable position and style
- `ai-captions` — On-device speech-to-text (Windows Speech Recognition or Whisper.cpp), SRT generation, subtitle burn-in during export
- `brand-presets` — Save/load named background+styling combinations for consistent branding
- `hotkeys` — Global hotkeys for start/stop/pause recording, take screenshot
- `system-tray` — Minimize to system tray, tray icon with quick-start recording

### Phase 7: Polish & Quality
- `ui-polish` — Fluent Design polish: animations, transitions, loading states, error handling, empty states
- `performance-optimization` — Profile and optimize: memory usage, GPU utilization, capture thread priority, frame drop monitoring
- `testing` — Unit tests for cursor smoothing algorithms, auto-zoom, timeline model. Integration tests for capture + export pipeline
- `packaging` — MSIX packaging, app icon, splash screen, installer

---

## Key Technical Decisions

1. **Cursor captured separately**: WGC captures screen WITHOUT cursor (`IsCursorCaptureEnabled = false`). Mouse position recorded via low-level hook at high frequency. This allows complete cursor replacement and smoothing in post-processing.

2. **Post-processing model**: All effects (cursor, zoom, background) applied during export, not during capture. This keeps capture lightweight (no frame drops) and allows unlimited adjustments after recording.

3. **Spring physics for smoothing**: Primary smoothing algorithm is a damped spring model (`F = -kx - bv`), matching Screen Studio's approach. Parameters: spring constant `k`, damping `b`, mass `m`. Adjustable via "smoothing strength" slider.

4. **Hardware encoding**: Real-time capture uses HW encoder for temp file. Export also uses HW encoder with higher quality settings. Auto-detect: NVENC (NVIDIA) → QSV (Intel) → AMF (AMD) → CPU fallback.

5. **Project file format**: A recording session produces a working folder under `%LOCALAPPDATA%\Musio\Sessions\session_*` (raw video MP4, cursor/keyboard data, audio tracks). A session is working state, not a deliverable, so the user's Videos folder only ever receives things they asked for: a saved `.musio` project or an exported MP4. Saving bundles the session into a single `.musio` file — a ZIP with a `manifest.json` plus stored media entries — so a project is one item in Explorer, portable, and re-openable for non-destructive editing. Compression is per entry: already-compressed media is stored verbatim, PCM audio and event logs are deflated.

6. **Captured frames are a scratch buffer, not an archive**: `VideoWriter` writes one JPEG per frame during capture as a write-ahead buffer, then encodes `video.mp4` and deletes them as soon as finalization succeeds (and only then — if it fails they are the sole copy). Finalization transcodes to `video.mp4.partial` and moves it into place only after every check passes, then writes a `finalized.marker`; cleanup requires that marker, so an interrupted or pre-orientation-fix session keeps its frames. The editor and exporter read frames through `VideoFrameReader`, which prefers those JPEGs when present and otherwise decodes the MP4 via `MediaPlayer` frame-server mode. The MP4 is the durable master, which is what keeps a project editable long after its frames are gone. Its bitrate is set by the **Capture quality** setting (12/30/60 Mbps base at 1080p, scaled by pixel count), encoded with a ~1s GOP so scrubbing never rewinds far. Filmstrip thumbnails come from `MediaComposition.GetThumbnailsAsync` instead, because sparse access through a single-position decoder is an order of magnitude slower and contends with the preview.

7. **Audio stays uncompressed**: Capture converts the WASAPI mix format down to 16-bit PCM, but no further. A/V alignment is derived from the first captured sample, and compressed codecs prepend encoder priming samples that would silently shift it; size is recovered by deflating the WAV inside the `.musio` package instead.

---

## Dependencies (NuGet)

| Package | Purpose |
|---------|---------|
| `Microsoft.WindowsAppSDK` | WinUI 3 + Windows App SDK |
| `Microsoft.Graphics.Win2D` | GPU-accelerated 2D rendering |
| `CommunityToolkit.Mvvm` | MVVM infrastructure |
| `Microsoft.Data.Sqlite` | Local database for presets/projects |
| `NAudio` | Advanced audio capture + processing |
| `SkiaSharp` | Fallback 2D rendering / image processing |
| `SharpDX.Direct3D11` | Direct3D11 interop for frame composition |

---

## Algorithms Reference

### Spring Physics Cursor Smoothing
```
position += velocity * dt
velocity += (-k * (position - target) - b * velocity) / mass * dt
```
Where: `k` = spring constant (150–400), `b` = damping (10–30), `mass` = 1.0

### One Euro Filter
```
dx = (x - prev_x) / dt
edx = lowpass(dx, alpha_d)
cutoff = min_cutoff + beta * |edx|
alpha = 1 / (1 + tau / (2 * pi * cutoff * dt))
filtered_x = alpha * x + (1 - alpha) * prev_filtered_x
```

### Click Animation Easing
```
scale(t) = 1.0 - 0.2 * cubic_bezier(0.4, 0, 0.2, 1)(t)  // press: 1.0 → 0.8
scale(t) = 0.8 + 0.2 * cubic_bezier(0.0, 0, 0.2, 1)(t)  // release: 0.8 → 1.0
```
