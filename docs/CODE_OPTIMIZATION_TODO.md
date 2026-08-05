# Code Optimization / Maintainability Backlog

Repository review completed 2026-07-30, citations re-verified 2026-08-04. This backlog
covers duplication, coupling, and cleanup opportunities identified during the same audit
that produced `docs/todo.md`, but which are **not** being implemented as part of the
Wave 1-4 optimization plan (partial-class splitting, DI, extraction of controllers, etc.)
currently in progress on `refactor/wave1-file-organization`. Treat this file as the
"parking lot" for everything that plan intentionally defers — it is not a complete list
of outstanding work once Waves 1-4 land, and several items below are explicitly blocked
on that work landing first.

Priority (maintainability-focused; does not reuse the P0-P3 stability scale in `todo.md`):

- **M0**: blocks other refactoring work; unlocks multiple downstream items.
- **M1**: high duplication or context cost, well-understood fix.
- **M2**: worthwhile cleanup, bounded scope.
- **M3**: cosmetic or low-yield.

---

## M0 - UI layer decomposition (blocked on a DI seam)

- [ ] **OPT-001: Introduce an `IProjectService` seam and dependency injection**
  - **Evidence:** `src/Musio.App/Services/ProjectService.cs:17`
    (`public static ProjectService Instance => _instance ??= new();`),
    `src/Musio.Core/Settings/AppSettings.cs:12`
    (`public static AppSettings Instance => _instance.Value;`),
    `src/Musio.Core/Settings/ShellSettings.cs:12`
    (`public static ShellSettings Instance { get; } = new();`).
    `ProjectService.Instance` is referenced **68** times in
    `src/Musio.App/Pages/EditorPage.xaml.cs` alone, plus **7** in
    `src/Musio.App/ViewModels/ExportViewModel.cs`, **4** in
    `src/Musio.App/ViewModels/EditorViewModel.cs`, and **1** in
    `src/Musio.App/Pages/OpenProjectsPage.xaml.cs`. There is no DI container anywhere in
    the app.
  - This is why no UI logic is unit-testable, and it is a prerequisite for
    OPT-002/003/004 below — extracting a controller class without a seam just relocates
    untestable code from one file to another.
  - **Proposed fix:** define `IProjectService`/`IAppSettings`/`IShellSettings` interfaces,
    keep the current singletons as the default implementation, and thread the interface
    through constructors of any newly extracted controller/ViewModel so it can be faked
    in tests.

- [ ] **OPT-002: Extract `EditorPreviewController`**
  - **Evidence:** `src/Musio.App/Pages/EditorPage.xaml.cs` — the async re-entrancy guards
    `_previewInitGeneration` (declared line 190, ~11 check/increment sites including
    lines 457, 579, 678, 765, 786, 938, 966, 1094, 4939),
    `_primaryPreviewStateGeneration` (declared line 117, sites at 124, 1135, 1445, 1452,
    1468, 1605, 1652), and `_segmentPreviewGeneration` (declared line 76, sites at 153,
    581, 1502, 1510, 1519, 1939, 1945), plus adaptive-quality and graphics-device
    recovery logic in the same file.
  - As of this writing the Wave 1 partial-class split has **not yet relocated** this
    logic into a separate file — everything above is still in the monolithic
    `EditorPage.xaml.cs` (7,457 lines). The established naming convention for these
    splits (see the already-extracted `src/Musio.App/Pages/EditorPage.PropertyPanels.cs`)
    suggests the eventual file will be named `EditorPage.Preview.cs`; update this
    citation once that split lands. This is the highest-risk extraction in the repo:
    getting the generation-counter invariants wrong across an `await` boundary breaks
    the whole preview pipeline.
  - **Proposed fix:** once OPT-001 lands, move the preview generation/quality/recovery
    state and its methods into a dedicated controller class that owns the three
    generation counters, taking `EditorPage` (or a narrow interface over it) as a
    dependency rather than being a `partial class EditorPage` member.

- [ ] **OPT-003: Extract `EditorStyleController`**
  - **Evidence:** `src/Musio.App/Pages/EditorPage.xaml.cs` — `SyncStyleControlsToConfig`
    is declared at line 5518 and called from lines 2760, 5389, and 5720. The method and
    its callers are deeply coupled to XAML named elements (`BgTypeCombo`,
    `WallpaperGrid`, `BgColorText`, and related controls declared in
    `EditorPage.xaml`), which are code-generated fields — extracting this needs either
    `x:FieldModifier="public"` on those elements or a ViewModel-first rework so the
    controller does not need direct XAML element references. The async wallpaper load
    (around line 5433) has ordering constraints against the synchronous
    `SyncStyleControlsToConfig` that must be preserved.
  - **Proposed fix:** same extraction approach as OPT-002 — defer until OPT-001 lands,
    then move style-sync logic into its own controller with an explicit dependency on
    the named XAML elements or a thin view-model wrapper around them.

- [ ] **OPT-004: Extract `EditorTextEditController`**
  - **Evidence:** `src/Musio.App/Pages/EditorPage.xaml.cs` — the `SlideTextEditTarget`
    (declared line 8314) and `OverlayTextEditTarget` (declared line 8399) adapters
    implement `ITextEditTarget` and hold a back-reference to the enclosing
    `EditorPage page` (constructors at lines 8319 and 8418); they call back into the
    page (e.g. `RefreshSlidePreview()`/`ScheduleOverlayPreviewRefresh()`-style methods
    referenced in the surrounding comments at lines 7528-7593, 7999-8004, 8172-8256).
    Converting `page` to an injected interface instead of the concrete `EditorPage` is
    the main work item here.
  - **Proposed fix:** extract an `IEditorTextEditHost` interface covering the handful of
    members `SlideTextEditTarget`/`OverlayTextEditTarget` call back into, implement it on
    `EditorPage`, and move the two adapter classes plus their driving logic into a
    standalone controller.

---

## M2 - XAML component extraction

- [ ] **OPT-101: `ColorPickerRow` UserControl**
  - **Evidence:** the `DropDownButton` + swatch + flyout `ColorPicker` block is repeated
    **9x** (~14 lines each) across
    `src/Musio.App/Controls/PropertyPanes/ScenePropertiesView.xaml:439,465`,
    `src/Musio.App/Controls/PropertyPanes/TextOverlayPropertiesView.xaml:199,400,501,536`,
    `src/Musio.App/Controls/PropertyPanes/TextSlidePropertiesView.xaml:103,175,214`.
    (Note: these views live under `Controls/PropertyPanes/`, not `Views/` as an earlier
    draft of this backlog assumed; line numbers have also shifted by a few lines from
    the original audit and are corrected above.)
  - Deferred because `EditorPage` code-behind binds directly to the inner `x:Name`s
    (each `DropDownButton`/`ColorPicker` pair has field-modifier-public named elements
    wired to handlers in `EditorPage.PropertyPanels.cs`), so this needs dependency
    properties plus rewiring all 9 handler sets — it is not the trivial change it
    appears to be.
  - **Proposed fix:** design a `ColorPickerRow` UserControl with `Label`, `Color`, and
    `ColorChanged`-style dependency properties, then migrate the 9 call sites one pane
    at a time, verifying each pane's color-change handlers still fire.

- [ ] **OPT-102: `TextFormattingToolbar` UserControl**
  - **Evidence:** the Bold/Italic/alignment toggle row (`<!-- Formatting: bold / italic /
    alignment -->`) is duplicated at
    `src/Musio.App/Controls/PropertyPanes/TextOverlayPropertiesView.xaml:222-259`
    (`OverlayBoldToggle`, `OverlayItalicToggle`, `OverlayAlignSegmented`) and
    `src/Musio.App/Controls/PropertyPanes/TextSlidePropertiesView.xaml:126-158`
    (`SlideBoldToggle`, `SlideItalicToggle`, `SlideAlignSegmented`) — ~35-38 lines each.
    Line numbers corrected from the original audit (previously cited as 224-260/128-160).
  - Same code-behind-binding problem as OPT-101: each toggle/segmented control is
    `x:FieldModifier="public"` and wired individually in code-behind.
  - **Proposed fix:** same approach as OPT-101 — a `TextFormattingToolbar` UserControl
    exposing `IsBold`/`IsItalic`/`Alignment` dependency properties, migrated one pane at
    a time.

- [ ] **OPT-103: `LabeledSection` implicit style**
  - **Evidence:** the `<StackPanel Spacing="6"><TextBlock
    Style="{StaticResource PropertyLabelStyle}"/>` wrapper appears **38x** across the 6
    property panes in `src/Musio.App/Controls/PropertyPanes/`
    (`CursorPropertiesView.xaml` x2, `ScenePropertiesView.xaml` x7,
    `TextOverlayPropertiesView.xaml` x15, `TextSlidePropertiesView.xaml` x11,
    `TransitionPropertiesView.xaml` x2, `VideoPropertiesView.xaml` x1). The original
    audit estimated "~44x"; the verified count from a repo-wide search is 38 — corrected
    here.
  - `src/Musio.App/Themes/PropertyPaneStyles.xaml:52` already defines a
    `PropertyHeaderTemplate` (`<DataTemplate x:Key="PropertyHeaderTemplate">`) that
    solves this for controls with a built-in `Header`; this item covers the remaining
    hand-written `TextBlock` + content wrapper cases.
  - **Proposed fix:** add an implicit `Style` (or a small `LabeledSection` UserControl)
    that reproduces the `PropertyHeaderTemplate` look for the hand-written cases, then
    replace the 38 wrapper blocks.

- [ ] **OPT-104: Delete `Converters/CheckedToBrushConverter.cs`**
  - **Evidence:** `src/Musio.App/Converters/CheckedToBrushConverter.cs` (52 lines,
    confirmed) is the app's only `IValueConverter` implementation and is referenced only
    from `src/Musio.App/Controls/PropertyPanes/ScenePropertiesView.xaml`. CommunityToolkit
    is already referenced there (`xmlns:ctk="using:CommunityToolkit.WinUI.Controls"` at
    line 7 of that file, used for `ctk:Segmented`/`ctk:SegmentedItem` at lines 303-321),
    so `BoolToObjectConverter` from `CommunityToolkit.WinUI.Converters` may replace it —
    confirm that specific converter is available in the referenced toolkit package
    version before acting, since the `ctk` namespace currently in use
    (`CommunityToolkit.WinUI.Controls`) is not the same namespace that hosts converters.
  - **Proposed fix:** if `BoolToObjectConverter` (or equivalent) is available, swap it in
    for the three binding sites (`Bg`/`Border`/`Glyph` parameters) in
    `ScenePropertiesView.xaml` and delete the custom converter; otherwise leave as-is.

---

## M1 - Filmstrip

- [ ] **OPT-107: Primary filmstrip never generates when the opened project has no `VideoFilePath`**
  - **Evidence:** `diag.log` reports
    `[Filmstrip] no thumbnails for segment '...\OpenProjects\<id>\video.mp4' (primary '')`
    on opening a saved project — the empty `primary ''` is
    `TimelineControl._primaryThumbnailFilePath`, which is only set by
    `TimelineControl.SetThumbnails(...)`, which is only called from
    `EditorPage.Timeline.cs` on the `isPrimary: true` path. That path is gated behind
    `if (!string.IsNullOrEmpty(primaryPath))` in `GenerateAllTimelineThumbnailsAsync`,
    where `primaryPath` comes from `ProjectService.Instance.CurrentProject.VideoFilePath`.
  - **This is a long-standing bug, not fallout from the optimization work** — confirmed by
    A/B: a build of `master` logs the identical line for the same project. Recorded here so
    it is not misattributed to the refactor.
  - The segments themselves carry a correct `VideoFilePath` (the log prints it), so only the
    `Project.VideoFilePath` field is empty; the likely gap is the package/session restore
    path (`ProjectService.OpenPackageAsync` → `CurrentProject = result.Project`) not
    repopulating it, while `MusioPathRewriter` does rewrite it when present.
  - Diagnostics for this are in place: `GenerateAllTimelineThumbnailsAsync` logs
    `primary pass skipped: the project has no VideoFilePath`, and
    `GenerateTimelineThumbnailsAsync` distinguishes "extraction returned nothing" from
    "discarded — superseded by a newer generation". Reproduce with a project open and the log
    will state which of the three it is.
  - **Proposed fix:** repopulate `Project.VideoFilePath` on the restore path from the
    timeline's primary video segment when it is missing, rather than gating the filmstrip on
    it. Falling back to `TimelineModel.PrimaryVideoFilePath`, or to the first primary
    `VideoSegment.VideoFilePath`, would make the filmstrip independent of a field that is
    only incidentally populated.

---

## M2 - Core pipeline

- [ ] **OPT-201: `TrackOperation<TSegment>` generic base**
  - **Evidence:** `src/Musio.Core/Timeline/Operations/CameraOperations.cs` (5 classes:
    `AddCameraSegmentOperation:5`, `MoveCameraSegmentOperation:42`,
    `TrimCameraSegmentOperation:75`, `RemoveCameraSegmentOperation:135`,
    `UpdateCameraSegmentPropertiesOperation:163`) and
    `src/Musio.Core/Timeline/Operations/TextOverlayOperations.cs` (5 structurally
    parallel classes: `AddTextOverlayOperation:5`, `MoveTextOverlayOperation:42`,
    `TrimTextOverlayOperation:75`, `RemoveTextOverlayOperation:135`,
    `UpdateTextOverlayPropertiesOperation:170`). Both operation sets re-sort their track
    by `Start` after every mutation, ~85 lines of net duplication between the two files.
  - Deferred because a generic-over-track-type design needs care, and the camera and
    text-overlay tracks may diverge further as features land.
  - **Proposed fix:** design a `TrackOperation<TSegment>` (or `TrackOperation<TSegment,
    TTrack>`) base handling the common add/move/trim/remove/update-and-resort pattern,
    with virtual hooks for the few divergent behaviors (e.g. overlay-specific property
    validation).

- [ ] **OPT-202: `BlurEffectGraph` wrapper**
  - **Evidence:** Win2D blur effect graphs are built three different ways: (1)
    `src/Musio.Core/Processing/TextOverlayRenderer.cs:400-428`
    (`EnsureBlurEffectGraph`) caches a `BorderEffect -> GaussianBlurEffect` chain in
    fields `_blurBorderEffect`/`_blurGaussianEffect` (correct — reused across frames);
    (2) `src/Musio.Core/Processing/BackgroundCompositor.cs:627-651`
    (`DrawBlurBackground`, called once per `Compose` at line 181) allocates a fresh
    `Transform2DEffect -> GaussianBlurEffect` chain **every draw** — a per-frame
    allocation on the hot path; (3)
    `src/Musio.Core/Processing/TransitionRenderer.cs:431-464` builds an inline
    `Transform2DEffect -> GaussianBlurEffect` chain in `using` blocks per transition
    frame. Correction from the original audit: only case (1) actually uses
    `BorderEffect`; cases (2) and (3) chain off `Transform2DEffect`, not `BorderEffect`.
    Note the `BackgroundCompositor` case is arguably a performance bug (per-frame
    allocation), not just duplication — see also `docs/todo.md` PERF-005, which already
    covers caching background-composition resources generally.
  - **Proposed fix:** extract a small `BlurEffectGraph` helper (owns a
    `Transform2DEffect`/`BorderEffect` + `GaussianBlurEffect` pair, exposes an `Update`
    method that only touches the cheap properties when the source is unchanged) and
    adopt it in all three call sites, prioritizing `BackgroundCompositor` for the
    performance win.

- [ ] **OPT-203: `DisposableBase` or a Roslyn analyzer for the disposal-guard pattern**
  - **Evidence:** the `private bool _disposed;` field appears in 22 files under
    `src/Musio.Core/` (e.g. `Capture/AudioCaptureEngine.cs`,
    `Processing/BackgroundCompositor.cs`, `Processing/FrameCompositor.cs`,
    `Export/SegmentFrameComposer.cs`, `Processing/PreviewRenderer.cs`, and 17 more), and
    `ObjectDisposedException.ThrowIf(...)` appears at ~29 call sites across
    `src/Musio.Core/` (several files have more than one guard site, e.g.
    `PreviewRenderer.cs` has 6, `RecordingSession.cs` has 4). The original audit
    estimated "~25 classes / ~30 guard sites"; the verified counts (22 / 29) are close
    and are corrected here.
  - The codebase prefers composition over inheritance elsewhere, so a Roslyn analyzer
    enforcing the pattern may fit better than introducing a shared base class.
  - **Proposed fix:** prototype both a minimal `DisposableBase` and an analyzer/code-fix
    that flags `IDisposable` classes missing the guard, and pick whichever needs less
    disruptive change to the 22 existing classes.

- [ ] **OPT-204: Investigate whether the zoom-sync sequence is duplicated instead of
      reusing `SegmentFrameComposer`'s private helper**
  - **Evidence:** `src/Musio.App/Pages/EditorPage.xaml.cs` calls
    `renderer.UpdateZoomKeyframes(...)` followed by
    `renderer.UpdateSuppressedClickTicks(...)` (and, at one call site, also
    `UpdateTextOverlays(...)`) at four separate places: lines 1636-1637, 2043-2044,
    2590-2591 (plus `UpdateTextOverlays` at 2609), and 2640-2641. Each of
    `PreviewRenderer.UpdateZoomKeyframes`/`UpdateTextOverlays`/`UpdateSuppressedClickTicks`
    (`src/Musio.Core/Processing/PreviewRenderer.cs:95-119`) is a thin wrapper that calls
    a single `FrameCompositor.Sync*` method. `src/Musio.Core/Export/SegmentFrameComposer.cs:1003`
    has a **private static** `SyncZoomState(FrameCompositor, TimelineModel?, string)`
    that combines the same three sync calls plus a null/empty-collection guard.
    Correction from the original audit: `SyncZoomState` is `private`, not `public` —
    `PreviewRenderer` could not call it today without a visibility change, and the
    duplication (if any) is really in `EditorPage`'s four call sites, not inside
    `PreviewRenderer` itself.
  - This is an **investigation item, not a confirmed defect** — ~20-30 lines if the
    four `EditorPage` call sites can be safely collapsed to call one shared helper.
  - **Proposed fix:** if confirmed, make `SyncZoomState` internal or public, adjust it to
    take the values `EditorPage` already has on hand (or the same `TimelineModel`/path
    selection it already uses via `SegmentFrameComposer.SelectTextOverlays`), and call it
    from `EditorPage` instead of the three-call sequence repeated four times.

---

## M3 - Tests

- [ ] **OPT-301: Assertion helpers for repeated count-pair asserts**
  - **Evidence:** `src/Musio.Tests/VideoWriterQueueTests.cs` has the
    `Assert.AreEqual(N, writer.FrameCount, ...)` +
    `Assert.AreEqual(N, CountFrameFiles(writer))` pair **5** times (lines 105/107,
    129/131, 147/148, 166/167, 192/194); `src/Musio.Tests/MouseHookRecorderTests.cs` has
    `Samples.Count`/`Clicks.Count` assertions scattered across ~13 lines (e.g. 131-132,
    186, 198, 257-258, 308, 314, 346, 376, 378, 403, 404, 431), not all of which are
    adjacent pairs. Correction from the original audit: it cited "11" and "8"
    occurrences respectively; the verified counts are lower and less uniformly paired
    than described, though the duplication is real.
  - Low value until the Wave 2 builders land (per this backlog's scope note, Wave 2
    test-builder work is being tracked separately).
  - **Proposed fix:** add a small `AssertFrameCounts(VideoWriter writer, int expected)`
    helper for `VideoWriterQueueTests`, and consider a similar
    `AssertSamplesAndClicks(MouseRecordingData data, int samples, int clicks)` helper for
    `MouseHookRecorderTests`, once the exact duplicated shape is confirmed at the sites
    above.

---

## M3 - Tooling

- [ ] **OPT-401: Exclude `AppPackages/` from code-search tooling**
  - **Evidence:** `AppPackages/` at the repo root contains 38 files totaling ~188 MB
    (verified). It is listed in `.gitignore:20` (`AppPackages/`) so it is correctly
    excluded from source control, but it is not excluded from this environment's
    file-search tooling, so it is still traversed and costs agent time on every
    broad search.
  - **Proposed fix:** add `AppPackages/` (and any other large gitignored build-output
    directories, e.g. per-configuration `bin/`/`obj/` trees) to whatever ignore-list
    mechanism the code-search tooling supports, separate from `.gitignore`.
