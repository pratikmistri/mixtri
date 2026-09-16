# Mixtri

**Professional screen recording with cinematic cursor effects for Windows.**

Mixtri is a screen recorder and editor optimized to run natively on Windows. Record your screen, add beautiful effects, and export polished videos — all from a native Windows app.

> **Formerly Musio.** The app was renamed to Mixtri; it is the same project, and the Store listing
> updates in place. Projects saved as `.musio` still open — Mixtri saves new ones as `.mixtri`.

![Mixtri Screenshot](docs/screenshot.png)

## Features

- **Screen capture** — Record full screen, a single window, or a custom region at 30 or 60 fps
- **Cinematic cursor effects** — Automatic zoom-to-click, smooth cursor trails, and highlight animations
- **Timeline editor** — Trim, split, and adjust speed with a visual timeline and instant preview
- **Automatic typing pace** — Sustained text-entry bursts are accelerated to 1.5×, muted, and framed with a subtle caret-focused zoom (or a broad click-position fallback for custom text controls)
- **Segment transitions** — Configurable dissolve, wipe, slide, push, and stylized effects at any timeline cut
- **Beautiful backgrounds** — Gradient, image, and wallpaper backgrounds behind your recording
- **Webcam overlay** — Picture-in-picture webcam feed with shape and position controls
- **Flexible export** — Export to MP4, GIF, or WebM in resolutions up to 4K with preset management

## Prerequisites

| Tool | Version |
|------|---------|
| .NET SDK | 9.0+ |
| Windows 10 SDK | 10.0.26100.0+ |
| Visual Studio | 2022 17.8+ with **.NET desktop development** and **Windows App SDK** workloads |

## Build & Run

The simplest route is to open `Mixtri.sln` in Visual Studio and press **F5**.

From the command line, build with **Visual Studio MSBuild**:

```powershell
# Clone the repo
git clone https://github.com/pratikmistri/mixtri.git
cd mixtri

# Locate VS MSBuild
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1

# Build (always pass /restore; a bare /t:Build fails to restore after a clean)
& $msbuild src\Mixtri.App\Mixtri.App.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64
```

> **`dotnet build` does not work on this repo.** It fails in the WinUI resource (PriGen) tooling
> with `MSB4062`, as does `dotnet test` when it tries to build. Use VS MSBuild as above. The app
> is packaged (MSIX), so `dotnet run` is not the launch path either — run it from Visual Studio,
> or register the build output with
> `Add-AppxPackage -Register <bin>\AppxManifest.xml`.

## Tests

Build the test project with MSBuild first, then run it without rebuilding:

```powershell
& $msbuild src\Mixtri.Tests\Mixtri.Tests.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64
dotnet test src\Mixtri.Tests\Mixtri.Tests.csproj --no-build -p:Platform=x64 -c Debug
```

If the host lacks the exact .NET 9 runtime, set `DOTNET_ROLL_FORWARD=Major` before `dotnet test`.

## Resource use

The default is one process, avoiding the extra in-use footprint of a second WinUI runtime.
Settings > General > **Release editor memory on close** enables an optional process-split
trial after restarting Mixtri. In that mode, a resident Mini/recorder process owns the tray icon and Win+Shift+X hotkey.
The full app runs in another process: Record, Editor, Gallery, and Settings are tabs in
the same window, not separate process launches. Expanding Mini restores that full window
or opens it on Record at its last position and size. A new full window is revealed only
after its initial XAML frames are ready.

Closing the full window retains the save/discard prompt and exits its process, releasing
its native rendering caches. Save, open, import, and export work block closing until finished.
Recording remains in the resident process; completed takes are handed over as metadata and
file paths, not raw frames. Record More targets the original project explicitly. A completed
take stays in a durable pending handoff until an editor accepts it; opening the full app
retries pending handoffs. If the original editor explicitly rejects a take, it opens separately
without overwriting existing edits. The new destination is saved before delivery; a lost reply
is retried with the same request ID in that editor, not by opening another copy.
Application receipts are stored with the recording, so a failed final acknowledgement does
not apply the same request again. An interrupted application is reported as uncertain rather
than blindly repeated or redirected. An unreachable recorder keeps the editor hidden until
its exit or an acknowledged recording's end is confirmed; an editor that fails a readiness
check is left running, never automatically killed.

This is an **idle-memory tradeoff**, not an overall memory reduction: the resident WinUI
process still costs roughly launch-time RAM while the full app is open, and a new full
window incurs process-startup latency. Turning the setting off restores single-process behavior
after restarting Mixtri.

Single-process tray RAM does not return to launch levels on the tested ARM64 host: a
diagnostic empty-editor close settles at about 120 MiB private working set and 143 MiB
private commit, against 48 MiB and 70 MiB at launch. Four repeated open/close cycles show
no per-cycle growth, and managed memory returns to under 1 MiB. About 64 MiB of the excess
is in eight fixed-size private allocations outside the enumerated process heaps. Their
count is unchanged at the two tested window sizes, and they survive window destruction
and the tested device-trimming and heap-optimization paths. A headless Win2D harness can
release similar allocations when its devices are destroyed, supporting further graphics-
lifetime investigation, but the exact native allocator and retaining owner are not proven.
No supported reclamation fix has been demonstrated. A plateau does not rule out a one-time
leak; these measurements are neither a universal memory ceiling nor proof of a driver defect.

Minimizing or collapsing an editor does not end its session. Hidden editors pause playback
and release derived preview/audio/thumbnail resources; restoring rebuilds them at the same
playhead without changing edits or undo history. An open project's last presented frame
stays available while its decoder warms. No process is restarted automatically to mask
memory use, and no working-set trim is used to page out retained allocations.

The preview's Win2D surface is created only when there is a frame to display. Inactive
property panes are constructed and wired on demand; the visible Scene panel, empty-state
actions, timeline controls, and editing behavior remain available as before.

Timeline repaints reuse a fixed, lazily created set of native text formats and stroke styles
instead of recreating them for every ruler tick, label, and drag frame. These resources are
released when the editor is hidden or unloaded; font sizes, wrapping, and drawing output
are unchanged.

Wallpaper-picker thumbnails load only in Image background mode, use bounded thumbnail
decoding, and are released when the picker is no longer needed or the editor is hidden.
The selected background remains full resolution for preview and export.
Gallery cards load posters on demand as their image controls are realized, without lowering
source resolution. Recycling, hiding, or unloading cancels stale loads and releases images;
restoring the gallery reloads the currently realized cards.

Filmstrips first extract the source samples needed by the visible timeline, with a small
whole-video overview as a fallback, then refine the remaining thumbnails in background
batches. A GPU-only crossfade reveals arrivals over 350 ms (honoring Windows' animation
preference); no color tint or gradient is drawn over the thumbnails. Final thumbnail
sampling, resolution, and export output are unchanged.

Waveform generation uses bounded, channel-aligned read buffers and vectorized peak scans
where supported. It preserves the original waveform buckets and stops once the requested
peaks are complete, rather than reading and discarding additional buckets.
Pending requests for the same inserted-audio file are coalesced. Preview restarts, hiding,
project close, and editor unload cancel waveform work; restore regenerates missing peaks,
and stale results cannot overwrite the current timeline.
Audio scrub feedback reuses one 80 ms stop timer. New scrub requests and normal transport
commands invalidate stale callbacks and queued seeks, so old scrub work cannot interrupt
resumed playback.

Rendering caches reuse unchanged geometry, text layout, and webcam decoration; transition
resolution avoids per-frame temporary configuration objects.
Text and cursor color parsing avoids temporary trimmed/expanded strings while preserving
the existing accepted formats and fallback rules. Export uses
small full-resolution frame caches with reference-counted read-only leases, and releases
source/style contexts after their last use. Encoder-owned output surfaces remain independent
until encoding completes. Preview also leases decoded source frames across composition instead
of making a second full-frame GPU copy; presented frames remain independently owned.
Cursor rendering reuses value-type buffers for transformed clicks and touch-animation chains,
avoiding per-frame event clones without modifying recorded input or animation timing.
Burned-in subtitles reuse one current text layout rather than measuring unchanged text on
every frame. Text, output-size, and device changes rebuild it; inactive cues release the
layout, and compositor teardown releases the remaining font resources.
Text-overlay cache cleanup scans live IDs once rather than once per cached entry. Removing
the last overlay releases its text and blur resources; adding it back recreates them lazily.
Animated text reuses its blur effect alongside the existing scratch surface, including
multi-pass outlines; resizing replaces the pair and teardown releases both.
These optimizations do not lower recording/export quality, frame
rate, resolution, or effect settings.

Screen capture is prepared before the recording overlay appears, but frame delivery starts
only after its gate opens so a static window's first frame is not discarded. At stop, the
last frame is held through the active recording duration using the existing CFR gap-fill
path, rather than waking the GPU repeatedly to capture an unchanged window.
Held and dropped-frame duplicates share immutable JPEG bytes through hard links where the
filesystem supports them. Unsupported filesystems use byte-identical copies, and long holds
roll over filesystem link limits automatically. Real frame files are published by replacement,
so no linked frame can be changed by a later write.
Finalization reuses one decoded source image across linked holds, while allocating a
separate input buffer for each encoder sample. Ordinary unlinked frames retain independent
decoding, and the cache is released only after outstanding sample preparation has stopped.

GIF exports reuse one CPU readback array per export, refilling it only after WIC commits
the previous frame. Dimension or pixel-format changes resize the buffer. Palette
quantization, frame delays, resolution, and encoded output remain unchanged.

## Architecture

```
mixtri/
├── src/
│   ├── Mixtri.App/         # WinUI 3 front-end (XAML pages, view models, controls)
│   │   ├── Pages/          # RecordingPage, EditorPage, ExportPage, SettingsPage
│   │   ├── Controls/       # PreviewCanvas, TimelineControl, RegionSelector
│   │   ├── ViewModels/     # MVVM view models (CommunityToolkit.Mvvm)
│   │   ├── Helpers/        # DialogHelper and UI utilities
│   │   └── Services/       # App-level services
│   ├── Mixtri.Core/        # Platform-independent capture & editing engine
│   └── Mixtri.Tests/       # MSTest suite covering Mixtri.Core
├── docs/                   # Documentation and assets
├── learnings/              # Settled playbooks and the historical decision archive
└── Mixtri.sln              # Solution file
```

## License

This project is licensed under the Apache License, Version 2.0 — see the [LICENSE](LICENSE) file
for details.

If you redistribute Mixtri or a derivative work, the license requires you to retain copyright and
attribution notices and to include the attribution from the [NOTICE](NOTICE) file in your
redistribution (e.g., in a NOTICE file, documentation, or a credits screen). Please keep the
credit visible in your product's about/credits screen or documentation.

Releases published before this change remain available under the MIT License.
