# Text Motion — Stage 1 (model, engine, presets, pickers) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give text overlays and title slides a recipe-based motion model with separate In and Out phases, nine new presets, and In/Out pickers on the preview, while every existing project renders exactly as before.

**Architecture:** A nullable `TextMotion` on each text segment switches the renderer from the legacy `TextSlideAnimation` path to a new recipe path in `AnimatedTextEngine`. All timing, curve, stagger, grouping and pose maths live in small pure static classes so they are unit-testable without a GPU; the engine's new draw method only wires them to Win2D. Timing stays on the segment and keeps flowing through `TextAnimationWindow`.

**Tech Stack:** C# / .NET 9, WinUI 3, Win2D 1.3, MSTest 3.6.

**Spec:** `docs/superpowers/specs/2026-11-23-text-motion-editor-design.md`. This plan covers the spec's delivery step 1 only. Steps 2 (motion strip) and 3 (on-canvas ghost, saved presets) get their own plans after this lands.

## Global Constraints

- **Windows only.** Build with VS MSBuild, never `dotnet build` (see `README.md`):
  `& $msbuild src\Mixtri.Tests\Mixtri.Tests.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64`
  then `dotnet test src\Mixtri.Tests\Mixtri.Tests.csproj --no-build -p:Platform=x64 -c Debug --filter "FullyQualifiedName~<TestClass>"`.
- Work on a new branch `feat/text-motion` cut from `master`, not on `fix/tier4-ux-a11y`.
- `Motion == null` means "render with the legacy `Animation` enum exactly as today". Do not edit the bodies of `DrawAnimatedText`, `DrawPerCharacter`, `ComputeCharParams`, `ComputeWholeState` or `TypewriterText`. All existing tests must pass unchanged.
- No schema bump: `MixtriPackage.CurrentSchemaVersion` stays `2`.
- Whenever `Motion` is set to a non-null value, `Animation` is set to the preset's legacy fallback in the same edit.
- Offsets, tracking and blur are in em (multiples of the font size the renderer draws with). Positive `OffsetY` is downward, positive `OffsetX` is rightward. Rotation is degrees. Scale is a factor.
- Motion types are immutable records (`init` only). `UpdateTextOverlayPropertiesOperation` snapshots with a shallow `overlay with { }`, which is only safe if nested values cannot be mutated.
- The recipe path must not allocate a render target per frame.
- Every new control has an `AutomationProperties.Name` and is reachable by Tab.
- Every model edit from the UI is exactly one undo step.

## Deviations from the spec (decided here)

- `TextMotion` has no `PresetId`. A phase's preset is found by record equality against the built-in presets; anything else displays as "Custom".
- Hover-to-loop preview on preset tiles moves to the Stage 2 plan, where it shares the loop mechanism with the strip's Loop preview. In Stage 1 choosing a tile applies it.
- Screen-reader announcements of preset changes move to Stage 2 (a page-level announcer has to be built).
- The pane's Animation dropdowns stay until Stage 2 replaces them; Task 9 keeps them consistent.

## Review Focus

1. **Hand-edited or corrupt motion values** (NaN, infinity, a bezier x of 7, a stagger of 300s): the frame still renders and never throws. Pinned in Task 2 and Task 4.
2. **Emoji and combined characters** ("Hi 👋🏽", "é" as e + accent): one visible character animates as one unit and is never split into broken halves. Pinned in Task 3.
3. **Long text** (several hundred characters, by character): the entrance still finishes inside its duration and every unit reaches rest. Pinned in Task 3.
4. **Empty or whitespace-only text**: nothing is drawn and nothing throws. Pinned in Task 3 and Task 6.
5. **Segment shorter than In + Out**: phases shrink proportionally, the text is fully at rest or fully gone at the ends, never stuck half-way. Pinned in Task 6.

## File Structure

| File | Responsibility |
|---|---|
| Create `src/Mixtri.Core/Timeline/TextMotion.cs` | `TextMotion`, `MotionPhase`, `MotionCurve`, enums, sanitising |
| Create `src/Mixtri.Core/Processing/MotionCurves.cs` | Named curves; evaluate bezier and spring |
| Create `src/Mixtri.Core/Processing/MotionStagger.cs` | Per-unit progress from phase progress |
| Create `src/Mixtri.Core/Processing/MotionUnits.cs` | Group glyphs into whole / line / word / character units |
| Create `src/Mixtri.Core/Processing/MotionPoseMath.cs` | Interpolate a unit's pose from In and Out phases |
| Create `src/Mixtri.Core/Timeline/TextMotionPresets.cs` | The nine presets; apply and identify |
| Create `src/Mixtri.Core/Processing/AnimatedTextEngine.Motion.cs` | Recipe draw path and envelope (partial class) |
| Modify `src/Mixtri.Core/Processing/AnimatedTextEngine.cs` | `partial`; `TextAnimationWindow.FromOverlay` |
| Modify `src/Mixtri.Core/Timeline/TimelineSegment.cs` | `Motion` on both segments; overlay `InDuration` / `OutDuration` |
| Modify `src/Mixtri.Core/Processing/TextOverlayRenderer.cs`, `TextSlideRenderer.cs` | Branch on `Motion`; pass overlay window |
| Modify `src/Mixtri.Core/Timeline/Operations/SegmentOperations.cs` | `SetTextSlideMotionOperation` |
| Modify `src/Mixtri.Core/Timeline/TextOverlayPresets.cs` | Style presets set recipes |
| Create `src/Mixtri.App/Pages/EditorPage.MotionBar.cs` | Floating bar behaviour |
| Modify `src/Mixtri.App/Pages/EditorPage.xaml`, `EditorPage.TextEditing.cs` | Bar markup, positioning, pane combo sync |

Tests go in `src/Mixtri.Tests/`, one class per new Core file, MSTest, `namespace Mixtri.Tests;`.

---

### Task 1: Motion model and persistence

**Files:**
- Create: `src/Mixtri.Core/Timeline/TextMotion.cs`
- Modify: `src/Mixtri.Core/Timeline/TimelineSegment.cs` (`TextSlideSegment` near line 252, `TextOverlaySegment` near line 604)
- Modify: `src/Mixtri.Core/Processing/AnimatedTextEngine.cs` (`TextAnimationWindow`)
- Test: `src/Mixtri.Tests/TextMotionModelTests.cs`

**Interfaces — Produces:**

```csharp
namespace Mixtri.Core.Timeline;
public enum MotionUnit { Whole, Line, Word, Character }
public enum StaggerOrder { Forward, Reverse, CenterOut }
public enum CurveKind { Bezier, Spring }

public sealed record MotionCurve
{
    public CurveKind Kind { get; init; } = CurveKind.Bezier;
    public double X1 { get; init; } = 0.22;  public double Y1 { get; init; } = 1;
    public double X2 { get; init; } = 0.36;  public double Y2 { get; init; } = 1;
    public double Bounce { get; init; }
    public MotionCurve Sanitized();   // X 0..1, Y -0.5..1.5, Bounce 0..0.6; non-finite -> default
}

public sealed record MotionPhase
{
    public MotionCurve Curve { get; init; } = new();
    public MotionUnit Unit { get; init; } = MotionUnit.Whole;
    public double StaggerSeconds { get; init; }
    public StaggerOrder Order { get; init; } = StaggerOrder.Forward;
    public double OffsetX { get; init; }   public double OffsetY { get; init; }
    public double Scale { get; init; } = 1;
    public double Blur { get; init; }      public double Rotation { get; init; }
    public double Tracking { get; init; }  public bool Mask { get; init; }
    public MotionPhase Sanitized();
    // Stagger 0..1, Offsets -5..5, Scale 0..4, Blur 0..1, Rotation -360..360,
    // Tracking -1..1; non-finite -> the identity value for that field
}

public sealed record TextMotion
{
    public MotionPhase In { get; init; } = new();
    public MotionPhase Out { get; init; } = new();
}
```

On `TextSlideSegment`: `public TextMotion? Motion { get; set; }`.
On `TextOverlaySegment`: `public TextMotion? Motion { get; set; }`, `public TimeSpan? InDuration { get; set; }`, `public TimeSpan? OutDuration { get; set; }`.
On `TextAnimationWindow`: `public static TextAnimationWindow FromOverlay(TextOverlaySegment overlay)`: start 0, end = duration, ramps = the overlay's durations, each falling back to `Auto(duration)`'s ramp when null, negative values treated as 0.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod] public void NewSegments_HaveNoMotion()
{
    Assert.IsNull(new TextSlideSegment().Motion);
    Assert.IsNull(new TextOverlaySegment().Motion);
}

[TestMethod] public void Motion_RoundTripsThroughProjectJson()
{
    // use the serializer options MixtriPackage defines (MixtriPackage.cs:78)
    var overlay = new TextOverlaySegment { Motion = new TextMotion {
        In = new MotionPhase { Unit = MotionUnit.Word, OffsetY = 0.35, Blur = 0.12, StaggerSeconds = 0.045 } },
        InDuration = TimeSpan.FromSeconds(0.7) };
    var back = Deserialize<TextOverlaySegment>(Serialize(overlay));
    Assert.AreEqual(overlay.Motion, back.Motion);          // record value equality
    Assert.AreEqual(overlay.InDuration, back.InDuration);
    StringAssert.Contains(Serialize(overlay), "\"Word\""); // string enums
}

[TestMethod] public void JsonWithoutMotion_LoadsAsLegacy()
{
    var back = Deserialize<TextOverlaySegment>("{\"Animation\":\"SlideUp\"}");
    Assert.IsNull(back.Motion);
    Assert.AreEqual(TextSlideAnimation.SlideUp, back.Animation);
}

[TestMethod] public void FromOverlay_WithNullDurations_EqualsAuto()
{
    var o = new TextOverlaySegment { Duration = TimeSpan.FromSeconds(4) };
    Assert.AreEqual(TextAnimationWindow.Auto(4), TextAnimationWindow.FromOverlay(o));
}

[TestMethod] public void Sanitized_ClampsAndReplacesNonFinite()
{
    var p = new MotionPhase { Scale = double.NaN, Blur = 9, StaggerSeconds = 300, OffsetX = double.PositiveInfinity }.Sanitized();
    Assert.AreEqual(1, p.Scale); Assert.AreEqual(1, p.Blur);
    Assert.AreEqual(1, p.StaggerSeconds); Assert.AreEqual(0, p.OffsetX);
    var c = new MotionCurve { X1 = 7, Y1 = -3, Bounce = 2 }.Sanitized();
    Assert.AreEqual(1, c.X1); Assert.AreEqual(-0.5, c.Y1); Assert.AreEqual(0.6, c.Bounce);
}
```

- [ ] **Step 2: Build and run `--filter "FullyQualifiedName~TextMotionModelTests"`.** Expected: build fails, types not defined.
- [ ] **Step 3: Implement the types and properties above.**
- [ ] **Step 4: Run the filter again, then the whole suite.** Expected: all pass, including every pre-existing test.
- [ ] **Step 5: Commit** `feat(core): text motion model and persistence`.

---

### Task 2: Curve evaluation

**Files:**
- Create: `src/Mixtri.Core/Processing/MotionCurves.cs`
- Test: `src/Mixtri.Tests/MotionCurvesTests.cs`

**Interfaces — Consumes:** `MotionCurve`, `CubicBezierEasing.Evaluate(float t, float x1, float y1, float x2, float y2)`.
**Produces:**

```csharp
namespace Mixtri.Core.Processing;
public static class MotionCurves
{
    public static double Evaluate(MotionCurve curve, double t);  // sanitises; t clamped 0..1
    public static MotionCurve Smooth { get; }  // bezier 0.22, 1, 0.36, 1
    public static MotionCurve Snappy { get; }  // bezier 0.16, 1, 0.30, 1
    public static MotionCurve Gentle { get; }  // bezier 0.40, 0, 0.20, 1
    public static MotionCurve Exit   { get; }  // bezier 0.40, 0, 1.00, 1
    public static MotionCurve Linear { get; }  // bezier 0, 0, 1, 1
    public static MotionCurve Spring(double bounce = 0.25);
}
```

Spring: an under- or critically-damped spring over normalised time whose damping falls as `Bounce` rises; the formula is the implementer's choice within the tests below. The result is forced to exactly `1` at `t == 1`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod] public void Endpoints_AreExact()   // for Smooth, Snappy, Gentle, Exit, Linear, Spring(0), Spring(0.6)
{ Assert.AreEqual(0, Evaluate(c, 0)); Assert.AreEqual(1, Evaluate(c, 1)); }

[TestMethod] public void Linear_IsIdentity() => Assert.AreEqual(0.37, Evaluate(Linear, 0.37), 1e-3);

[TestMethod] public void Smooth_IsFrontLoaded() => Assert.IsTrue(Evaluate(Smooth, 0.3) > 0.7);

[TestMethod] public void SpringWithoutBounce_NeverOvershoots()
{ for (double t = 0; t <= 1; t += 0.01) Assert.IsTrue(Evaluate(Spring(0), t) <= 1 + 1e-6); }

[TestMethod] public void SpringWithBounce_OvershootsModestly()
{ double max = Samples(Spring(0.25)).Max(); Assert.IsTrue(max > 1.01 && max < 1.3); }

[TestMethod] public void SpringSettles_BeforeTheEnd()
{ Assert.AreEqual(1, Evaluate(Spring(0.25), 0.95), 0.02); }

[TestMethod] public void OvershootBezier_IsAllowedAboveOne()
{ Assert.IsTrue(Samples(new MotionCurve { X1 = 0.3, Y1 = 1.5, X2 = 0.5, Y2 = 1 }).Max() > 1.0); }

[TestMethod] public void CorruptCurve_DoesNotThrowAndStaysFinite()
{ var v = Evaluate(new MotionCurve { X1 = double.NaN, Y2 = double.NegativeInfinity, Bounce = 99 }, 0.5);
  Assert.IsTrue(double.IsFinite(v)); }

[TestMethod] public void OutOfRangeT_IsClamped()
{ Assert.AreEqual(0, Evaluate(Smooth, -4)); Assert.AreEqual(1, Evaluate(Smooth, 9)); }
```

- [ ] **Step 2: Run `~MotionCurvesTests`.** Expected: build fails.
- [ ] **Step 3: Implement `MotionCurves`.**
- [ ] **Step 4: Run again.** Expected: pass.
- [ ] **Step 5: Commit** `feat(core): motion curve evaluation with springs`.

---

### Task 3: Stagger timing and unit grouping

**Files:**
- Create: `src/Mixtri.Core/Processing/MotionStagger.cs`, `src/Mixtri.Core/Processing/MotionUnits.cs`
- Test: `src/Mixtri.Tests/MotionStaggerTests.cs`, `src/Mixtri.Tests/MotionUnitsTests.cs`

**Interfaces — Produces:**

```csharp
public static class MotionStagger
{
    public const double MinUnitFraction = 0.4;
    /// Progress (0..1) of one unit, given the whole phase's progress (0..1).
    public static double UnitProgress(double phaseProgress, double totalSeconds,
        double staggerSeconds, int unitCount, int unitIndex, StaggerOrder order);
}

public static class MotionUnits
{
    /// One entry per drawable glyph, in drawing order.
    public readonly record struct GlyphRef(int TextIndex, int Length, int Line);
    /// Splits text into drawable text elements (never splitting a surrogate pair or
    /// a base + combining sequence), skipping whitespace. lineOfTextIndex maps a
    /// char index to its laid-out line.
    public static IReadOnlyList<GlyphRef> Elements(string text, Func<int, int> lineOfTextIndex);
    /// Unit index per glyph, and the unit count.
    public static (int[] UnitOfGlyph, int UnitCount) Group(
        string text, IReadOnlyList<GlyphRef> glyphs, MotionUnit unit);
}
```

Stagger rule (from the spec): `unitSeconds = max(total − stagger × (n − 1), total × MinUnitFraction)`; the effective stagger is `(total − unitSeconds) / (n − 1)`; unit rank `r` starts at `r × effectiveStagger`. Rank is `i` for `Forward`, `n − 1 − i` for `Reverse`, and for `CenterOut` the units are ranked by distance from the middle index, nearest first, ties sharing a rank. With `n <= 1` or `totalSeconds <= 0` the result is the clamped `phaseProgress`.

Word units: a run of non-whitespace glyphs is one word. Line units: glyphs sharing a `Line`. Use `System.Globalization.StringInfo` for text elements.

- [ ] **Step 1: Write the failing tests**

```csharp
// MotionStaggerTests — total 0.7s, stagger 0.045s, 5 units: unitSeconds 0.52
[TestMethod] public void FirstUnit_FinishesAfterItsOwnDuration()
    => Assert.AreEqual(1, UnitProgress(0.52 / 0.7, 0.7, 0.045, 5, 0, Forward), 1e-9);
[TestMethod] public void LastUnit_HasNotStartedAtItsDelay()
    => Assert.AreEqual(0, UnitProgress(0.18 / 0.7, 0.7, 0.045, 5, 4, Forward), 1e-9);
[TestMethod] public void EveryUnit_IsAtRestWhenPhaseEnds()
{ for (int i = 0; i < 5; i++) Assert.AreEqual(1, UnitProgress(1, 0.7, 0.045, 5, i, Forward), 1e-9); }
[TestMethod] public void LongText_HitsFloorAndStillFinishes()   // 0.7s, 0.1s stagger, 400 units
{ Assert.AreEqual(1, UnitProgress(1, 0.7, 0.1, 400, 399, Forward), 1e-9);
  Assert.AreEqual(0, UnitProgress(0.42 / 0.7, 0.7, 0.1, 400, 399, Forward), 1e-9); }
[TestMethod] public void Reverse_SwapsFirstAndLast()
    => Assert.AreEqual(UnitProgress(0.3, 0.7, 0.045, 5, 0, Forward), UnitProgress(0.3, 0.7, 0.045, 5, 4, Reverse), 1e-9);
[TestMethod] public void CenterOut_MiddleLeadsAndEndsMatch()
{ Assert.IsTrue(UnitProgress(0.2, 0.7, 0.045, 5, 2, CenterOut) > UnitProgress(0.2, 0.7, 0.045, 5, 0, CenterOut));
  Assert.AreEqual(UnitProgress(0.2, 0.7, 0.045, 5, 0, CenterOut), UnitProgress(0.2, 0.7, 0.045, 5, 4, CenterOut), 1e-9); }
[TestMethod] public void DegenerateInputs_FallBackToPhaseProgress()
{ Assert.AreEqual(0.3, UnitProgress(0.3, 0.7, 0.045, 1, 0, Forward), 1e-9);
  Assert.AreEqual(0.3, UnitProgress(0.3, 0, 0.045, 5, 2, Forward), 1e-9);
  Assert.AreEqual(1, UnitProgress(7, 0.7, 0.045, 5, 2, Forward), 1e-9); }

// MotionUnitsTests — single line unless stated
[TestMethod] public void MakeItYours_Counts()
{ var g = Elements("Make it yours", _ => 0);
  Assert.AreEqual(11, g.Count);
  Assert.AreEqual(1,  Group("Make it yours", g, MotionUnit.Whole).UnitCount);
  Assert.AreEqual(3,  Group("Make it yours", g, MotionUnit.Word).UnitCount);
  Assert.AreEqual(11, Group("Make it yours", g, MotionUnit.Character).UnitCount); }
[TestMethod] public void Lines_FollowTheLayout()
{ var g = Elements("Make it\nyours", i => i < 8 ? 0 : 1);
  Assert.AreEqual(2, Group("Make it\nyours", g, MotionUnit.Line).UnitCount); }
[TestMethod] public void Emoji_IsOneElement()
{ var g = Elements("Hi 👋🏽", _ => 0); Assert.AreEqual(3, g.Count); Assert.IsTrue(g[2].Length > 1); }
[TestMethod] public void CombiningAccent_StaysWithItsBase()
    => Assert.AreEqual(1, Elements("é", _ => 0).Count);
[TestMethod] public void EmptyAndWhitespace_GiveNoUnits()
{ foreach (var s in new[] { "", "   ", "\n\t" })
  { var g = Elements(s, _ => 0); Assert.AreEqual(0, g.Count); Assert.AreEqual(0, Group(s, g, MotionUnit.Word).UnitCount); } }
```

- [ ] **Step 2: Run `~MotionStaggerTests|FullyQualifiedName~MotionUnitsTests`.** Expected: build fails.
- [ ] **Step 3: Implement both classes.**
- [ ] **Step 4: Run again.** Expected: pass.
- [ ] **Step 5: Commit** `feat(core): motion stagger timing and unit grouping`.

---

### Task 4: Pose interpolation

**Files:**
- Create: `src/Mixtri.Core/Processing/MotionPoseMath.cs`
- Test: `src/Mixtri.Tests/MotionPoseMathTests.cs`

**Interfaces — Consumes:** `MotionPhase`, `MotionCurves.Evaluate`.
**Produces:**

```csharp
public readonly record struct MotionPose(
    float Dx, float Dy, float Scale, float RotationRadians,
    float BlurPixels, float TrackingPixels, double Opacity);

public static class MotionPoseMath
{
    public const double OpacityLead = 0.6;
    /// inP / outP are this unit's own progress through each phase (0..1).
    public static MotionPose Evaluate(MotionPhase inPhase, MotionPhase outPhase,
        double inP, double outP, float fontSize);
}
```

Rule: with `a = 1 − curve_in(inP)` and `b = curve_out(outP)`, each additive field is `in.Field × a + out.Field × b` (offsets, blur and tracking multiplied by `fontSize`; rotation converted to radians). Scale is `lerp(1, in.Scale, a) × lerp(1, out.Scale, b)`. Blur is clamped at `>= 0` after combining. Opacity is `smoothstep(clamp(inP / OpacityLead)) × (1 − clamp(outP))`. Both phases are sanitised first.

- [ ] **Step 1: Write the failing tests**

```csharp
static readonly MotionPhase Rest = new();
static MotionPhase Lin(MotionPhase p) => p with { Curve = MotionCurves.Linear };

[TestMethod] public void AtRest_IsIdentity()
{ var p = Evaluate(Lin(new() { OffsetY = 0.35, Scale = 0.9, Blur = 0.12 }), Rest, 1, 0, 100);
  Assert.AreEqual(new MotionPose(0, 0, 1, 0, 0, 0, 1.0), p); }
[TestMethod] public void HalfWayIn_IsHalfTheOffset()
    => Assert.AreEqual(17.5f, Evaluate(Lin(new() { OffsetY = 0.35 }), Rest, 0.5, 0, 100).Dy, 1e-3f);
[TestMethod] public void StartOfIn_IsTheFarPoseAndInvisible()
{ var p = Evaluate(Lin(new() { OffsetX = -0.6, Scale = 0.92, Blur = 0.12, Rotation = 90, Tracking = 0.12 }), Rest, 0, 0, 100);
  Assert.AreEqual(-60f, p.Dx, 1e-3f); Assert.AreEqual(0.92f, p.Scale, 1e-4f);
  Assert.AreEqual(12f, p.BlurPixels, 1e-3f); Assert.AreEqual(MathF.PI / 2, p.RotationRadians, 1e-4f);
  Assert.AreEqual(12f, p.TrackingPixels, 1e-3f); Assert.AreEqual(0, p.Opacity); }
[TestMethod] public void Opacity_IsFullByTheLead()
    => Assert.AreEqual(1, Evaluate(Rest, Rest, 0.6, 0, 100).Opacity, 1e-9);
[TestMethod] public void EndOfOut_IsTheOutPoseAndInvisible()
{ var p = Evaluate(Rest, Lin(new() { OffsetX = 0.3, Scale = 0.97 }), 1, 1, 100);
  Assert.AreEqual(30f, p.Dx, 1e-3f); Assert.AreEqual(0.97f, p.Scale, 1e-4f); Assert.AreEqual(0, p.Opacity); }
[TestMethod] public void CorruptPhase_GivesFinitePose()
{ var p = Evaluate(new() { Scale = double.NaN, OffsetY = double.PositiveInfinity }, Rest, 0.3, 0, 100);
  Assert.IsTrue(float.IsFinite(p.Dy) && float.IsFinite(p.Scale)); }
[TestMethod] public void Blur_NeverGoesNegativeOnOvershoot()
{ var c = new MotionCurve { X1 = 0.3, Y1 = 1.5, X2 = 0.5, Y2 = 1 };
  for (double t = 0; t <= 1; t += 0.05)
      Assert.IsTrue(Evaluate(new() { Blur = 0.2, Curve = c }, Rest, t, 0, 100).BlurPixels >= 0); }
```

- [ ] **Step 2: Run `~MotionPoseMathTests`.** Expected: build fails.
- [ ] **Step 3: Implement `MotionPoseMath`.**
- [ ] **Step 4: Run again.** Expected: pass.
- [ ] **Step 5: Commit** `feat(core): motion pose interpolation`.

---

### Task 5: Presets

**Files:**
- Create: `src/Mixtri.Core/Timeline/TextMotionPresets.cs`
- Modify: `src/Mixtri.Core/Timeline/TextOverlayPresets.cs`
- Test: `src/Mixtri.Tests/TextMotionPresetTests.cs`

**Interfaces — Produces:**

```csharp
public sealed record TextMotionPreset(string Id, string Name, MotionPhase In, MotionPhase Out,
    double InSeconds, double OutSeconds, TextSlideAnimation LegacyFallback)
{
    public TextMotion Motion => new() { In = In, Out = Out };
    public void Apply(TextOverlaySegment overlay);  // Motion, InDuration, OutDuration, Animation
    public void Apply(TextSlideSegment slide);      // Motion, TextInDuration, TextOutDuration, Animation
}

public static class TextMotionPresets
{
    public static IReadOnlyList<TextMotionPreset> All { get; }
    public static TextMotionPreset ById(string id);            // throws on unknown id
    public static TextMotionPreset DefaultForOverlays { get; } // blur-rise
    public static TextMotionPreset DefaultForSlides { get; }   // line-reveal
    public static TextMotionPreset? MatchIn(MotionPhase phase);   // record equality, else null
    public static TextMotionPreset? MatchOut(MotionPhase phase);
    /// Replace one phase. When current is null the other phase comes from the same preset.
    public static TextMotion WithIn(TextMotion? current, TextMotionPreset preset);
    public static TextMotion WithOut(TextMotion? current, TextMotionPreset preset);
}
```

Preset values. Unlisted fields are identity. Curves are `MotionCurves` names. In / Out seconds and legacy fallback last.

| Id / Name | In | Out | In s | Out s | Fallback |
|---|---|---|---|---|---|
| `soft-fade` Soft Fade | Whole, Smooth | Whole, Exit | 0.5 | 0.3 | FadeIn |
| `blur-rise` Blur Rise | Word, OffsetY 0.35, Blur 0.12, Stagger 0.045, Smooth | Whole, Blur 0.06, Exit | 0.7 | 0.35 | CascadeFadeUp |
| `line-reveal` Line Reveal | Line, Mask, OffsetY 1.2, Stagger 0.09, Snappy | Line, Mask, OffsetY −1.2, Stagger 0.05, Exit | 0.8 | 0.45 | Reveal |
| `focus-pull` Focus Pull | Whole, Blur 0.20, Scale 1.04, Smooth | Whole, Blur 0.10, Exit | 0.8 | 0.4 | ZoomBlurIn |
| `tracking-settle` Tracking Settle | Whole, Tracking 0.12, Smooth | Whole, Exit | 0.9 | 0.35 | TrackingIn |
| `spring-pop` Spring Pop | Word, Scale 0.92, Stagger 0.04, Spring(0.25) | Whole, Scale 0.97, Exit | 0.6 | 0.25 | ScalePop |
| `glide` Glide | Whole, OffsetX −0.6, Snappy | Whole, OffsetX 0.3, Exit | 0.7 | 0.35 | SlideRight |
| `character-cascade` Character Cascade | Character, OffsetY 0.25, Stagger 0.018, Smooth | Whole, Exit | 0.7 | 0.3 | CascadeFadeUp |
| `settle` Settle | Whole, Scale 1.08, Smooth | Whole, Scale 0.98, Exit | 0.9 | 0.4 | ZoomBlurIn |

`TextOverlayPresets`: every style preset replaces its `overlay.Animation = X;` line with a recipe `Apply`, by this mapping of the value it sets today: `SlideUp` → Glide with its In changed to `OffsetX 0, OffsetY 0.6` and Out to `OffsetX 0, OffsetY −0.3`; `FadeIn` → Soft Fade; `ScalePop` → Spring Pop; `ZoomBlurIn` → Focus Pull; `TrackingIn` → Tracking Settle; anything else → Soft Fade.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod] public void NinePresets_WithUniqueIdsAndNames()
{ Assert.AreEqual(9, All.Count);
  Assert.AreEqual(9, All.Select(p => p.Id).Distinct().Count());
  Assert.AreEqual(9, All.Select(p => p.Name).Distinct().Count()); }
[TestMethod] public void EveryExit_IsShorterThanItsEntrance()
{ foreach (var p in All) Assert.IsTrue(p.OutSeconds < p.InSeconds, p.Id); }
[TestMethod] public void EveryPreset_SurvivesSanitisingUnchanged()
{ foreach (var p in All) { Assert.AreEqual(p.In, p.In.Sanitized(), p.Id); Assert.AreEqual(p.Out, p.Out.Sanitized(), p.Id); } }
[TestMethod] public void ApplyToOverlay_SetsMotionTimingAndFallback()
{ var o = new TextOverlaySegment(); ById("blur-rise").Apply(o);
  Assert.AreEqual(ById("blur-rise").Motion, o.Motion);
  Assert.AreEqual(TimeSpan.FromSeconds(0.7), o.InDuration);
  Assert.AreEqual(TimeSpan.FromSeconds(0.35), o.OutDuration);
  Assert.AreEqual(TextSlideAnimation.CascadeFadeUp, o.Animation); }
[TestMethod] public void ApplyToSlide_WritesTheExistingWindowFields()
{ var s = new TextSlideSegment { Duration = TimeSpan.FromSeconds(5) }; ById("line-reveal").Apply(s);
  Assert.AreEqual(TimeSpan.FromSeconds(0.8), s.TextInDuration);
  Assert.AreEqual(TimeSpan.FromSeconds(0.45), s.TextOutDuration);
  Assert.AreEqual(TextSlideAnimation.Reveal, s.Animation); }
[TestMethod] public void WithIn_OnClassicSegment_TakesBothPhasesFromThePreset()
    => Assert.AreEqual(ById("glide").Motion, WithIn(null, ById("glide")));
[TestMethod] public void WithOut_KeepsTheExistingIn()
{ var m = WithOut(ById("glide").Motion, ById("soft-fade"));
  Assert.AreEqual(ById("glide").In, m.In); Assert.AreEqual(ById("soft-fade").Out, m.Out); }
[TestMethod] public void Match_FindsPresetOrNull()
{ Assert.AreEqual("glide", MatchIn(ById("glide").In)!.Id);
  Assert.IsNull(MatchIn(ById("glide").In with { OffsetX = -0.61 })); }
[TestMethod] public void EveryOverlayStylePreset_SetsARecipe()
{ foreach (var preset in /* every TextOverlayPreset the class exposes */)
  { var o = new TextOverlaySegment(); preset.Apply(o);
    Assert.IsNotNull(o.Motion, preset.Name); Assert.IsNotNull(o.InDuration, preset.Name); } }
[TestMethod] public void LowerThird_RisesInsteadOfSliding()
{ var o = new TextOverlaySegment(); TextOverlayPresets.LowerThird.Apply(o);
  Assert.AreEqual(0, o.Motion!.In.OffsetX); Assert.AreEqual(0.6, o.Motion.In.OffsetY); }
```

- [ ] **Step 2: Run `~TextMotionPresetTests`.** Expected: build fails.
- [ ] **Step 3: Implement the presets and update `TextOverlayPresets`.** Update the class doc comment there: presets now set motion, not `Animation` alone. If an existing test asserts a style preset's `Animation` value, change it to assert the recipe instead and say so in the commit body.
- [ ] **Step 4: Run again, then the whole suite.** Expected: pass.
- [ ] **Step 5: Commit** `feat(core): nine text motion presets; style presets use recipes`.

---

### Task 6: Engine recipe path and renderer wiring

**Files:**
- Create: `src/Mixtri.Core/Processing/AnimatedTextEngine.Motion.cs`
- Modify: `src/Mixtri.Core/Processing/AnimatedTextEngine.cs` (class becomes `partial`; nothing else)
- Modify: `src/Mixtri.Core/Processing/TextOverlayRenderer.cs` (lines ~201, ~364, ~607, ~621)
- Modify: `src/Mixtri.Core/Processing/TextSlideRenderer.cs` (line ~77)
- Test: `src/Mixtri.Tests/TextMotionEnvelopeTests.cs`

**Interfaces — Consumes:** everything from Tasks 1–4; `ComputeInOutProgress(progress, durationSeconds, window)`.
**Produces:**

```csharp
public partial class AnimatedTextEngine
{
    public void DrawMotionText(CanvasDrawingSession ds, string text, CanvasTextFormat format,
        Rect rect, Color baseColor, TextMotion motion, double progress,
        int canvasWidth, int canvasHeight, float fontSize, double durationSeconds,
        TextAnimationWindow window);

    /// Transform and opacity for an overlay's background box.
    public static (float Scale, float Tx, float Ty, double Opacity) ComputeMotionEnvelope(
        TextMotion motion, double progress, double durationSeconds,
        TextAnimationWindow window, float fontSize);
}
```

Behaviour:
- `ComputeMotionEnvelope`: if both phases are `Whole`, return the single unit's pose (scale, Dx, Dy, opacity). Otherwise return identity transform with opacity `smoothstep(inP) × (1 − outP)` on the phases' overall progress.
- `DrawMotionText`: measure once per (text, format, rect) using the existing layout-cache pattern; build glyphs with `MotionUnits.Elements` (line from the layout's line metrics); group for In and for Out separately (their `Unit` may differ); per glyph compute `inP`/`outP` with `MotionStagger.UnitProgress` and the pose with `MotionPoseMath.Evaluate`; skip glyphs with opacity `<= 0.003`.
- Transform order per unit: rotation and scale about the **unit's** centre (word, line or whole box, not the glyph), then translation.
- Tracking: a glyph's extra x is `(k − (m − 1) / 2) × TrackingPixels`, where `k` is its index among the `m` glyphs on its line.
- `Mask`: draw the unit inside a layer clipped to its line's box (full text-rect width, that line's top to bottom).
- Outside the window (`progress` before `InStart` or at/after `OutEnd`) draw nothing.
- Blur: implementer's choice of technique, bound by Global Constraints (no per-frame render-target allocation; reuse `EnsureBlurScratch`). When every drawn glyph shares one blur value, blur the whole text in one pass.
- Renderers: when `Motion` is non-null call the new methods; otherwise call the legacy ones as now. The overlay renderer passes `TextAnimationWindow.FromOverlay(overlay)` to **every** engine call, legacy included, replacing today's omitted window argument.

- [ ] **Step 1: Write the failing tests**

```csharp
static readonly TextAnimationWindow W = TextAnimationWindow.Auto(4); // 0.6s ramps on a 4s segment

[TestMethod] public void WholeRecipe_MovesTheBoxWithTheText()
{ var m = TextMotionPresets.ById("glide").Motion;
  var e = ComputeMotionEnvelope(m, 0, 4, W, 100);
  Assert.AreEqual(-60f, e.Tx, 1e-2f); Assert.AreEqual(0, e.Opacity); }
[TestMethod] public void StaggeredRecipe_OnlyFadesTheBox()
{ var m = TextMotionPresets.ById("blur-rise").Motion;
  var e = ComputeMotionEnvelope(m, 0.075, 4, W, 100);   // half-way through the In ramp
  Assert.AreEqual(1f, e.Scale); Assert.AreEqual(0f, e.Tx); Assert.AreEqual(0f, e.Ty);
  Assert.IsTrue(e.Opacity > 0 && e.Opacity < 1); }
[TestMethod] public void MidHold_IsIdentityAndOpaque()
{ foreach (var p in TextMotionPresets.All)
  { var e = ComputeMotionEnvelope(p.Motion, 0.5, 4, W, 100);
    Assert.AreEqual((1f, 0f, 0f, 1.0), e, p.Id); } }
[TestMethod] public void SegmentShorterThanInPlusOut_StartsHiddenRestsAndEndsHidden()
{ var o = new TextOverlaySegment { Duration = TimeSpan.FromSeconds(0.5) };
  TextMotionPresets.ById("settle").Apply(o);              // asks for 0.9s in + 0.4s out
  var w = TextAnimationWindow.FromOverlay(o);
  Assert.AreEqual(0, ComputeMotionEnvelope(o.Motion!, 0, 0.5, w, 100).Opacity);
  Assert.AreEqual(0, ComputeMotionEnvelope(o.Motion!, 1, 0.5, w, 100).Opacity, 1e-6);
  // each ramp is first capped at the 0.5s window (0.5 + 0.4), then both shrink by 0.5 / 0.9,
  // so the entrance ends at 0.5 * (0.5 / 0.9) seconds, i.e. progress 0.5 / 0.9
  var (inP, outP) = ComputeInOutProgress(0.5 / 0.9, 0.5, w);
  Assert.AreEqual(1, inP, 1e-6); Assert.AreEqual(0, outP, 1e-6); }
[TestMethod] public void LegacyEnvelope_IsUntouchedByOverlayWindowDefaults()
{ var o = new TextOverlaySegment { Duration = TimeSpan.FromSeconds(4) };
  Assert.AreEqual(
      ComputeEnvelope(TextSlideAnimation.SlideUp, 0.05, 4, 1920, 1080),
      ComputeEnvelope(TextSlideAnimation.SlideUp, 0.05, 4, 1920, 1080, TextAnimationWindow.FromOverlay(o))); }
```

- [ ] **Step 2: Run `~TextMotionEnvelopeTests`.** Expected: build fails.
- [ ] **Step 3: Implement the partial class and wire both renderers.**
- [ ] **Step 4: Run the filter, then the whole suite.** Expected: pass, including every pre-existing animation test.
- [ ] **Step 5: Verify by eye on Windows.** Build and run `Mixtri.App`. Open an existing project: text overlays and title slides look and time exactly as before. Then temporarily set a recipe in the debugger or via a scratch project file and confirm, for each of the nine presets, at 1080p preview and in an exported MP4: text is sharp at rest; empty text draws nothing; "Hi 👋🏽" animates the emoji as one piece; an overlay with the Outline + Shadow background animates outline, shadow and fill together; Line Reveal's lines appear from behind an edge rather than fading through space. Record what was checked in the commit body.
- [ ] **Step 6: Commit** `feat(core): recipe draw path in AnimatedTextEngine`.

---

### Task 7: Undoable slide motion edit

**Files:**
- Modify: `src/Mixtri.Core/Timeline/Operations/SegmentOperations.cs`
- Test: `src/Mixtri.Tests/SetTextSlideMotionOperationTests.cs`

**Interfaces — Produces:**

```csharp
/// Sets a slide's motion, legacy animation and ramp durations as one undo step.
public class SetTextSlideMotionOperation : SegmentEditOperationBase
{
    public SetTextSlideMotionOperation(string segmentId, TextMotion? motion,
        TextSlideAnimation animation, TimeSpan? textInDuration, TimeSpan? textOutDuration);
    public static SetTextSlideMotionOperation ForPreset(string segmentId, TextMotionPreset preset);
    public static SetTextSlideMotionOperation ForClassic(TextSlideSegment slide, TextSlideAnimation animation);
    // ForClassic: motion null, keeps the slide's current ramp durations
}
```

Follow `SetTextSlideTextWindowOperation` in the same file for base-class usage and the `ChangedModel` convention. Description: `"Change Text Motion"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod] public void Execute_AppliesPreset_AndUndoRestoresEverything()
{ var slide = new TextSlideSegment { Duration = TimeSpan.FromSeconds(5), Animation = TextSlideAnimation.BounceIn,
                                     TextInDuration = TimeSpan.FromSeconds(1.5) };
  var model = ModelWith(slide);
  var op = SetTextSlideMotionOperation.ForPreset(slide.Id, TextMotionPresets.ById("line-reveal"));
  op.Execute(model);
  Assert.AreEqual(TextMotionPresets.ById("line-reveal").Motion, slide.Motion);
  Assert.AreEqual(TextSlideAnimation.Reveal, slide.Animation);
  Assert.AreEqual(TimeSpan.FromSeconds(0.8), slide.TextInDuration);
  op.Undo(model);
  Assert.IsNull(slide.Motion);
  Assert.AreEqual(TextSlideAnimation.BounceIn, slide.Animation);
  Assert.AreEqual(TimeSpan.FromSeconds(1.5), slide.TextInDuration);
  Assert.IsNull(slide.TextOutDuration); }
[TestMethod] public void ForClassic_ClearsMotionAndKeepsTiming()
{ /* start from line-reveal applied; ForClassic(slide, Wave).Execute -> Motion null,
     Animation Wave, TextInDuration still 0.8s */ }
[TestMethod] public void MissingSegment_ChangesNothingAndReportsNoChange()
{ /* Execute against a model without the id: no throw, ChangedModel false */ }
```

- [ ] **Step 2: Run `~SetTextSlideMotionOperationTests`.** Expected: build fails.
- [ ] **Step 3: Implement the operation.**
- [ ] **Step 4: Run again.** Expected: pass.
- [ ] **Step 5: Commit** `feat(core): undoable text slide motion operation`.

---

### Task 8: Defaults for new text

**Files:**
- Modify: every site that constructs a **new user-created** `TextOverlaySegment` or `TextSlideSegment` (find with `grep -rn "new TextOverlaySegment\|new TextSlideSegment" src/Mixtri.App src/Mixtri.Core --include=*.cs`, excluding tests, deserialisation, and clone/duplicate paths)
- Test: add to `src/Mixtri.Tests/TextMotionPresetTests.cs` if the creation path is in `Mixtri.Core`; otherwise verify by eye

Apply `TextMotionPresets.DefaultForOverlays.Apply(overlay)` / `DefaultForSlides.Apply(slide)` at creation. **Do not** change the property initialisers on the records: a segment loaded from an old file must keep `Motion == null`. Subtitle overlays generated by `SubtitleGenerator` / `SubtitleBurner` are not user-created text: leave them as they are.

- [ ] **Step 1: List the creation sites and write a test for each one reachable from Core** asserting the created segment's `Motion` equals the default preset's `Motion`.
- [ ] **Step 2: Run it.** Expected: fail (`Motion` is null).
- [ ] **Step 3: Apply the defaults.**
- [ ] **Step 4: Run the whole suite.** Expected: pass; `NewSegments_HaveNoMotion` from Task 1 still passes.
- [ ] **Step 5: Commit** `feat: new text starts on Blur Rise / Line Reveal`.

---

### Task 9: In / Out pickers on the preview

**Files:**
- Modify: `src/Mixtri.App/Pages/EditorPage.xaml` (inside `TextEditCanvas`, near line 263)
- Create: `src/Mixtri.App/Pages/EditorPage.MotionBar.cs` (partial `EditorPage`)
- Modify: `src/Mixtri.App/Pages/EditorPage.TextEditing.cs` (`PositionTextEditControls` near line 1913; `OverlayAnimationCombo_SelectionChanged` near 735; `SlideAnimationCombo_SelectionChanged` near 1203; the two combo-sync blocks near 109 and 1374)

**Interfaces — Consumes:** `TextMotionPresets`, `SetTextSlideMotionOperation`, `UpdateTextOverlayPropertiesOperation`, the existing `ITextEditTarget`.

Markup: a `StackPanel x:Name="MotionBar"` (horizontal, spacing 4, `CornerRadius` `SmallCornerRadius`, card background and stroke from the theme) containing two `DropDownButton`s, `MotionInButton` and `MotionOutButton`, with automation names "Entrance motion" and "Exit motion". Button content is `In: <name>` / `Out: <name>`. Each flyout holds two labelled groups, "Presets" and "Classic", each a `GridView` of text tiles (single selection, three columns in a 300px-wide flyout). Presets lists the nine names in table order; Classic lists the 17 legacy names with the labels the pane combo uses today.

Behaviour:
- Placement: horizontally aligned to the text box's left edge, 8px above it; if that would leave the preview's top, 8px below it; clamped inside the preview horizontally. Hidden while the text is being edited in place or dragged, and whenever `TextEditCanvas` is hidden.
- Button label: the matching preset's name from `MatchIn` / `MatchOut`; "Custom" when `Motion` is non-null and nothing matches; the Classic label on **both** buttons when `Motion` is null.
- Choosing a preset tile in In: `Motion = WithIn(current, preset)`, In duration = `preset.InSeconds`, `Animation = preset.LegacyFallback`; if the segment was Classic, also set the Out duration from the preset. Out mirrors this with `WithOut` / `OutSeconds`, leaving `Animation` alone unless the segment was Classic.
- Choosing a Classic tile in either picker: `Motion = null`, `Animation = <chosen>`, durations unchanged.
- Overlays commit through one `UpdateTextOverlayPropertiesOperation` (description "Change Text Motion", no merge key). Slides commit through `SetTextSlideMotionOperation`. Then refresh the preview and the timeline the way the existing animation handlers do, and mark the project dirty where those handlers do.
- After a choice, move the playhead to the segment's start for an In choice, or to `end − out duration` for an Out choice, and start playback, so the result is seen immediately.
- Pane combos: when `Motion` is non-null, set `SelectedIndex = -1` and `PlaceholderText = "Custom motion"` under the existing suppress flags. Picking from a combo behaves like choosing a Classic tile, and the slide combo now goes through `SetTextSlideMotionOperation.ForClassic` instead of mutating the slide directly.
- Keyboard: both buttons are in the tab order after the text box handles; Escape closes a flyout and returns focus to its button.

- [ ] **Step 1: Add the markup and the partial class; wire placement into `PositionTextEditControls`.**
- [ ] **Step 2: Implement choice handling and label sync as specified.**
- [ ] **Step 3: Build `Mixtri.App` and run the whole test suite.** Expected: build succeeds, all tests pass.
- [ ] **Step 4: Verify by eye on Windows.** For an overlay and for a title slide: the bar appears above the selected text and flips below near the top edge; In and Out can be chosen independently; each choice is one Ctrl+Z; choosing Typewriter in either picker makes both buttons read "Typewriter"; then choosing Glide for In makes Out read Glide's exit; the pane dropdown shows "Custom motion" for a recipe and still switches to a Classic animation; save, close, reopen and the choice is kept; the bar is reachable and operable by keyboard alone, and Narrator reads both button names. Record what was checked in the commit body.
- [ ] **Step 5: Commit** `feat(app): In/Out motion pickers on the preview`.

---

## Done when

- The whole test suite passes on Windows, with no pre-existing test modified except any in Task 5 that asserted a style preset's legacy `Animation`.
- A project saved before this branch opens and renders identically.
- A new overlay and a new title slide use Blur Rise and Line Reveal, and both can be changed per phase from the preview.
- The two by-eye checklists (Task 6 Step 5, Task 9 Step 4) are recorded in their commits.
