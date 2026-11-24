# Text motion editor — design

Date: 2026-11-23. Status: awaiting approval.

## Intent

Text animations on text overlays and title slides should look like a crafted
product reel, and the user should be able to shape the motion by hand rather
than only pick from a list. Today the motion reads as slide-deck transitions
and only its timing on title slides is editable.

Editing happens in the preview area, not the properties pane: the user wants
to manipulate motion while watching the result, and wants direct manipulation
on the canvas wherever that is the better fit.

## What is wrong today (the review this design answers)

- Every effect's exit is its entrance mirrored. Overlays, and slides left on
  automatic timing, use the same hardcoded 0.6s for both.
- Travel is large: 12% of the frame for slides, 60% start scale for Scale Pop,
  160% for Zoom Blur, 90 degree spins, a literal bounce.
- Easing is five stock formulas (cubic in, out and in-out, back, bounce)
  hardcoded in `AnimatedTextEngine`; per-character opacity is linear; there
  are no springs.
- Stagger is per character only, left to right, fixed spread. No word or line
  units, no masked line reveal, no per-unit blur.
- One enum (`TextSlideAnimation`) drives both In and Out. Slides have four
  0–30s timing sliders; overlays have no timing controls.
- The 17-item dropdown is duplicated in both text panes with no preview.

## Goals

1. A motion model where In and Out are separate, each with its own curve,
   pose and stagger.
2. A small set of new presets built on that model that hold up next to
   professional motion work.
3. A docked motion strip under the preview with a curve editor, an
   in/hold/out timing bar and compact value fields.
4. Direct manipulation of travel on the canvas.
5. Preview and export render the same motion from the same code path.

## Non-goals

- Per-property curves (separate curves for position, scale, opacity, blur).
  The strip layout leaves room for them; they are not built now.
- Keyframes.
- Curve editing for transitions, zoom or camera moves (they may reuse the
  strip later).
- The wider properties-pane cleanup. Only the shared pieces this editor needs
  are built here.
- Scramble, random-order, 3D or path-based text effects.

## Anti-goals

- An existing project that renders differently after this change.
- An editor that covers the text being edited.
- A preset list that grows back into a long flat menu.
- Motion that looks different in export than in preview.

## Constraints

- WinUI 3 + Win2D. All drawing goes through `AnimatedTextEngine`, which both
  `TextSlideRenderer` and `TextOverlayRenderer` call (the overlay renderer
  calls it several times per frame for outline and shadow passes).
- Animation enum values are part of the `.mixtri` on-disk format (string
  enums, schema version 2).
- Every edit must be one undo step per gesture. Overlays already do this via
  `UpdateTextOverlayPropertiesOperation`; slide edits use
  `UpdateTextSlideOperation` / `SetTextSlideTextWindowOperation`.
- The current branch carries accessibility work; new controls must be
  keyboard-operable and named for screen readers.
- The app cannot be built or run on macOS. Pure logic is unit tested in
  `Mixtri.Tests`; anything visual must be verified on Windows.

## Approaches

**Depth of control**

- A. Curve only on a fixed effect list. Cheapest; cannot fix travel or stagger.
- B. Recipes: separate In and Out, each with curve, pose and stagger. **Chosen
  (builder's recommendation, user did not object).** Covers every finding in
  the review and fits a compact editor.
- C. Per-property curves. Most powerful; needs a larger editor. Deferred.

**Placement of the editor**

- A. Docked strip between the preview and the timeline toolbar. **Chosen by
  the user.** Never covers the text, wide enough for curve, timing and values
  in one row, room to grow. Costs preview height while open.
- B. Floating card over the preview. Closest to the object but covers part of
  a small frame and is too narrow.
- C. Lane inside the timeline. True-scale timing, but a 0.6s entrance is a few
  pixels wide and the curve still needs a popup.

## The motion model

New types in `Mixtri.Core/Timeline`:

```
TextMotion        { MotionPhase In; MotionPhase Out; string? PresetId }
MotionPhase       { MotionCurve Curve; MotionUnit Unit; double StaggerSeconds;
                    StaggerOrder Order; double OffsetX; double OffsetY;
                    double Scale; double Blur; double Rotation;
                    double Tracking; bool Mask }
MotionCurve       { CurveKind Kind; double X1, Y1, X2, Y2; double Bounce }
MotionUnit        Whole | Line | Word | Character
StaggerOrder      Forward | Reverse | CenterOut
CurveKind         Bezier | Spring
```

- `TextSlideSegment` and `TextOverlaySegment` each gain a nullable
  `TextMotion? Motion`. **Null means "render with the legacy `Animation` enum
  exactly as today."** The legacy drawing code is not modified, and its
  existing tests keep passing unchanged.
- A phase describes the *far* pose: for In, where the text comes from; for
  Out, where it goes. The resting pose is always identity. Opacity always
  runs 0↔1.
- Units: `OffsetX/OffsetY/Tracking/Blur` are in em (multiples of the font
  size the renderer actually draws with), `Rotation` is degrees, `Scale` is a
  factor. Em units keep motion proportional on both renderers: overlays scale
  their font by `height / 1080`, slides do not, and neither needs a special
  case. Blur values in the preset table below are quoted in pixels for a
  100px font (so "blur 12" is 0.12em).
- `Mask` clips each unit to its own line box, so a rising line appears from
  behind an invisible edge instead of fading through space.
- Bezier: x clamped to 0–1, y allowed −0.5 to 1.5 so overshoot is drawable.
  Evaluated with the existing `CubicBezierEasing.Evaluate`, which is
  float-typed and short-circuits any curve with `x1 == y1 && x2 == y2` to
  linear; both are acceptable here.
- Spring: a damped spring over normalised time, controlled by one `Bounce`
  value (0–0.6), pinned to exactly 1 at the end of the phase.

**Timing stays on the segment, not in the recipe.** Slides keep
`TextInStart / TextInDuration / TextOutEnd / TextOutDuration`. Overlays gain
nullable `InDuration` and `OutDuration` with the same automatic default. A
preset carries a suggested In and Out duration that is written to those
fields when the preset is applied. The engine keeps using
`TextAnimationWindow`. The overlay durations also apply to Classic
animations: `TextOverlayRenderer` passes a window to the engine's existing
`window` parameter, and null durations produce today's automatic window, so
untouched overlays render as before.

The timeline's existing drag handles for a slide's text window stay. They and
the strip's timing bar edit the same fields.

**Stagger timing.** The phase duration on the segment is the *total* visible
length of the entrance or exit, so the timing bar is truthful. With `n` units,
each unit animates for `total − stagger × (n − 1)`, floored at 40% of the
total; if the floor is hit, the stagger is compressed to fit.

**Overlay background box.** For `Whole` recipes the box moves, scales and
fades with the text as it does today. For staggered recipes the box only
fades, on the phase's overall progress.

**Compatibility with older builds.** No schema bump. When `Motion` is set,
`Animation` is also set to the nearest legacy value, so an older build opens
the project and shows an approximate animation instead of refusing the file.
(Builder's call; say so if a hard version gate is preferred.)

## Presets

Nine built-in recipes, each a deliberate In/Out pair. Values are starting
points to be tuned by eye on Windows.

| Preset | In | Out |
|---|---|---|
| Soft Fade | opacity only, 0.5s, strong ease-out | fade, 0.3s |
| Blur Rise | by word, rise 0.35em, blur 12, stagger 45ms, 0.7s | whole, blur 6 + fade, 0.35s |
| Line Reveal | by line, masked, rise one line height, stagger 90ms, 0.8s | masked, exits upward, 0.45s |
| Focus Pull | whole, blur 20, scale 1.04, no travel, 0.8s | blur 10 + fade, 0.4s |
| Tracking Settle | whole, tracking +0.12em, fade, 0.9s | fade, 0.35s |
| Spring Pop | by word, scale 0.92, spring bounce 0.25, 0.6s | scale 0.97 + fade, 0.25s |
| Glide | whole, from 0.6em left, 0.7s | continues 0.3em right + fade, 0.35s |
| Character Cascade | by character, rise 0.25em, stagger 18ms, 0.7s | whole fade, 0.3s |
| Settle | whole, scale 1.08, fade, 0.9s | scale 0.98 + fade, 0.4s |

Craft rules the presets follow: exits are shorter and simpler than entrances;
exits continue the direction of travel rather than reversing it; travel is
under half an em except for masked reveals; opacity completes in roughly the
first 60% of a unit's motion.

The 17 legacy animations remain selectable under a **Classic** group
(Typewriter, Typewriter + Caret and Wave are mechanisms the recipe model does
not express, so Classic is their permanent home). New overlays default to
Blur Rise; new title slides default to Line Reveal. (Builder's call.)

## The editor

**Floating bar.** When a text overlay or slide is selected, a small bar sits
just above its on-canvas text box (below it if there is no room): an In
picker, an Out picker and a Motion toggle. A picker opens a grid of preset
tiles grouped as Presets, Yours and Classic; hovering or focusing a tile
loops that preset on the real text in the preview.

In and Out are picked independently among recipes. Classic is all-or-nothing,
because one legacy value drives both phases:

- Choosing a Classic item in either picker sets `Motion` to null and both
  pickers show that Classic name.
- Choosing a recipe for one phase of a Classic segment creates a `Motion`
  whose other phase is that preset's own paired phase.
- A recipe In with a Classic Out (or the reverse) cannot be expressed.

For a Classic segment the Motion toggle still opens the strip, in a
timing-only state: the timing bar and loop preview work, and the curve, value
and stagger controls are disabled with a one-line explanation.

**Motion strip.** Toggling Motion opens a strip about 96px tall at the bottom
of the preview column (inside editor row 1, so the properties pane keeps its
full height). Left to right:

1. In / Out phase switch.
2. Curve graph with two draggable handles (one Bounce handle in Spring mode),
   plus chips for Smooth, Snappy, Gentle, Spring, Linear.
3. In/hold/out bar spanning the segment's duration, with draggable edges for
   the In and Out lengths (and, on slides, the window start and end) and
   numeric readouts.
4. Value fields for the selected phase: X, Y, Scale, Blur, Rotate, Tracking,
   Mask.
5. Unit switch (All / Line / Word / Char), Stagger in ms, Order.
6. Loop preview, Save as preset, Close.

Loop preview plays the selected phase plus a short hold, repeatedly, in the
preview, and puts the playhead back when stopped.

**Scrub field.** A compact numeric control used for every value in the strip:
label and value on one line, drag horizontally to scrub, click to type,
double-click to reset to the preset's value. This is the shared piece the
later panel cleanup will reuse.

**On-canvas ghost.** While the strip is open, an outline of the far pose is
drawn on the canvas (start pose on the In tab, end pose on the Out tab).
Dragging it sets `OffsetX/OffsetY`; Shift constrains to one axis.

**Properties panes.** The Animation dropdown is removed from both text panes
and the four Animation Window sliders from the slide pane. In their place is
one summary row, for example "Motion: Blur Rise → Fade", with an Edit button
that opens the strip.

**Overlay style presets.** The six one-click overlay styles in
`TextOverlayPresets` currently set a legacy `Animation`. Each is changed to
set a recipe instead, so applying a style still fully replaces the look,
motion included: Lower Third → Glide (rising rather than sideways), Caption
Bar → Soft Fade, Callout Pill → Spring Pop, and the remaining three to the
closest recipe by the builder's judgement. (Builder's call.)

**Your presets.** Save as preset stores the current In/Out recipe and
durations in a per-user file alongside the app's settings. Saved presets
appear under Yours and can be applied and deleted. They are not stored in the
project; applying one copies its values into the segment.

**Undo.** Each drag, scrub, typed value, preset choice or ghost move is one
undo step, for slides as well as overlays.

**Keyboard and screen readers.** Everything in the strip is reachable by Tab.
Curve handles and the ghost move with arrow keys (fine step; Shift for a
coarse step). The curve also has four numeric fields as a non-pointer
alternative. Every control has an automation name. Phase and preset changes
are announced to screen readers; the only live region today is private to
`TimelineControl`, so a page-level announcer has to be exposed or added.

## Rendering

`AnimatedTextEngine` gains a second path used when `Motion` is non-null. It
groups the measured glyph layout into the chosen units, computes each unit's
progress from the stagger rule above, evaluates the phase curve, and draws
each unit with its interpolated pose. `ComputeEnvelope` gains the matching
case so the overlay box follows the rule above. The same call serves preview
and export.

## Delivery order

1. Model, curves, engine path, presets and persistence, selectable from the
   floating bar's pickers. Usable on its own.
2. Motion strip: curve graph, timing bar, scrub fields, loop preview; pane
   controls replaced by the summary row.
3. On-canvas ghost and Your presets.

## Left to the builder

- Whether the new path lives in `AnimatedTextEngine` or a sibling class.
- How per-unit blur is drawn within the frame budget (quantised blur levels,
  per-unit scratch targets, or grouping units that share a blur level). It
  must not reintroduce per-frame render-target allocation.
- The exact spring formula and the Bounce-to-damping mapping.
- The opacity lead (the "first 60%" rule) as an engine constant.
- Strip visuals within the existing theme resources, and the exact floating
  bar placement logic.
- File name and format of the user preset file.
- Test structure. At minimum: curve and spring evaluation, stagger timing
  including the 40% floor, unit grouping, JSON round-trip with and without
  `Motion`, legacy fallback when `Motion` is null, and preset application as
  a single undoable change.
