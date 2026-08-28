namespace Musio.Tests;

using System.Reflection;
using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Models;
using Musio.Core.Processing;

[TestClass]
public sealed class CursorShapeTests
{
    private const double TickFrequency = 10_000_000.0;

    private static void Save(string path, MouseRecordingData data)
    {
        var saveMethod = typeof(MouseHookRecorder).GetMethod(
            "SaveDataToFile", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(saveMethod, "SaveDataToFile method should exist");
        saveMethod.Invoke(null, [path, data]);
    }

    private static string TempPath() => Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        $"test_shape_{Guid.NewGuid():N}.mcur");

    #region MCUR v2 round-trip

    [TestMethod]
    public void SaveAndLoad_PreservesCursorShape()
    {
        var samples = new List<MouseSample>
        {
            new() { TimestampTicks = 100, X = 1, Y = 1, EventKind = MouseEventKind.Move, Shape = CursorShape.Arrow },
            new() { TimestampTicks = 110, X = 2, Y = 2, EventKind = MouseEventKind.Move, Shape = CursorShape.Hand },
            new() { TimestampTicks = 120, X = 3, Y = 3, EventKind = MouseEventKind.Move, Shape = CursorShape.IBeam },
            new() { TimestampTicks = 130, X = 4, Y = 4, EventKind = MouseEventKind.Move, Shape = CursorShape.ResizeWE },
            new() { TimestampTicks = 140, X = 5, Y = 5, EventKind = MouseEventKind.Move, Shape = CursorShape.ResizeNESW },
        };
        var original = new MouseRecordingData
        {
            Samples = samples,
            Clicks = [],
            StartTimestampTicks = 100,
            EndTimestampTicks = 140,
            TickFrequency = TickFrequency,
        };

        string tempFile = TempPath();
        try
        {
            Save(tempFile, original);
            var loaded = MouseHookRecorder.LoadFromFile(tempFile);

            Assert.AreEqual(samples.Count, loaded.Samples.Count);
            for (int i = 0; i < samples.Count; i++)
                Assert.AreEqual(samples[i].Shape, loaded.Samples[i].Shape, $"Sample[{i}].Shape");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void LoadFromFile_Version1_DefaultsShapeToArrow()
    {
        // Hand-write a legacy v1 MCUR file (no shape byte per sample).
        string tempFile = TempPath();
        try
        {
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write("MCUR"u8.ToArray());
                bw.Write(1);            // version 1
                bw.Write(2);            // sample count
                bw.Write(0);            // click count
                bw.Write(0L);           // start ticks
                bw.Write(100L);         // end ticks
                bw.Write(TickFrequency);

                // Two v1 samples: no shape byte.
                foreach (var (ts, x, y) in new[] { (0L, 10, 20), (50L, 11, 21) })
                {
                    bw.Write(ts);
                    bw.Write(x);
                    bw.Write(y);
                    bw.Write((byte)MouseEventKind.Move);
                    bw.Write((byte)MouseButton.None);
                    bw.Write((short)0);
                }
            }

            var loaded = MouseHookRecorder.LoadFromFile(tempFile);

            Assert.AreEqual(2, loaded.Samples.Count);
            Assert.AreEqual(CursorShape.Arrow, loaded.Samples[0].Shape);
            Assert.AreEqual(CursorShape.Arrow, loaded.Samples[1].Shape);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    #endregion

    #region Shape propagation through SmoothPath

    [TestMethod]
    public void SmoothPath_AssignsNearestSampleShape()
    {
        // Two samples: first half Arrow, second half Hand.
        var samples = new List<MouseSample>
        {
            new() { TimestampTicks = 0, X = 0, Y = 0, EventKind = MouseEventKind.Move, Shape = CursorShape.Arrow },
            new() { TimestampTicks = (long)TickFrequency, X = 100, Y = 0, EventKind = MouseEventKind.Move, Shape = CursorShape.Hand },
        };
        var recording = new MouseRecordingData
        {
            Samples = samples,
            Clicks = [],
            StartTimestampTicks = 0,
            EndTimestampTicks = (long)TickFrequency, // 1 second
            TickFrequency = TickFrequency,
        };

        var smoother = new CursorSmoother
        {
            Algorithm = SmoothingAlgorithm.None,
            Strength = SmoothingStrength.None,
        };

        var result = smoother.SmoothPath(recording, 10);

        Assert.IsTrue(result.Count > 0);
        // Early frames are nearest the first (Arrow) sample.
        Assert.AreEqual(CursorShape.Arrow, result[0].Shape);
        // Late frames are nearest the second (Hand) sample.
        Assert.AreEqual(CursorShape.Hand, result[^1].Shape);
    }

    [TestMethod]
    public void SmoothPath_PreservesShapeThroughSmoothingAndDestutter()
    {
        // Steady left-to-right motion at 100 Hz for 1 second; first half Arrow,
        // second half Hand. Exercises the active-smoothing + de-stutter path
        // (the default in production) to ensure Shape survives sample remapping.
        const int sampleCount = 100;
        double ticksPerSample = TickFrequency / 100.0;
        var samples = new List<MouseSample>(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            samples.Add(new MouseSample
            {
                TimestampTicks = (long)(i * ticksPerSample),
                X = i * 4,
                Y = 0,
                EventKind = MouseEventKind.Move,
                Shape = i < sampleCount / 2 ? CursorShape.Arrow : CursorShape.Hand,
            });
        }

        var recording = new MouseRecordingData
        {
            Samples = samples,
            Clicks = [],
            StartTimestampTicks = 0,
            EndTimestampTicks = samples[^1].TimestampTicks,
            TickFrequency = TickFrequency,
        };

        var smoother = new CursorSmoother
        {
            Algorithm = SmoothingAlgorithm.SpringPhysics,
            Strength = SmoothingStrength.UltraSmooth,
            DestutterEnabled = true,
        };

        var result = smoother.SmoothPath(recording, 30);

        Assert.IsTrue(result.Count > 0);
        Assert.AreEqual(CursorShape.Arrow, result[0].Shape);
        Assert.AreEqual(CursorShape.Hand, result[^1].Shape);
        // Every frame must carry a defined shape (no default/garbage values).
        Assert.IsTrue(result.TrueForAll(p => p.Shape is CursorShape.Arrow or CursorShape.Hand));
    }

    [TestMethod]
    public void SmoothPath_EmptyRecording_ReturnsEmptyWithoutThrowing()
    {
        var recording = new MouseRecordingData
        {
            Samples = [],
            Clicks = [],
            StartTimestampTicks = 0,
            EndTimestampTicks = 0,
            TickFrequency = TickFrequency,
        };

        var smoother = new CursorSmoother
        {
            Algorithm = SmoothingAlgorithm.SpringPhysics,
            Strength = SmoothingStrength.UltraSmooth,
        };

        var result = smoother.SmoothPath(recording, 30);
        Assert.AreEqual(0, result.Count);
    }

    #endregion

    #region Shape resolver

    [TestMethod]
    public void CursorShapeResolver_Resolve_ReturnsDefinedShapeWithoutThrowing()
    {
        var resolver = new CursorShapeResolver();
        var shape = resolver.Resolve();
        Assert.IsTrue(Enum.IsDefined(typeof(CursorShape), shape),
            $"Resolve() returned an undefined CursorShape: {shape}");
    }

    [TestMethod]
    public void SmoothPath_DebouncesShapeFlicker_KeepsSustainedChanges()
    {
        // 100 samples at 100 Hz (1s). Mostly Arrow, with a single-sample IBeam
        // "flicker" at t=0.5s, and a sustained Hand for the final 300ms.
        const int sampleCount = 100;
        double ticksPerSample = TickFrequency / 100.0;
        var samples = new List<MouseSample>(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            CursorShape shape = CursorShape.Arrow;
            if (i == 50) shape = CursorShape.IBeam;     // 10ms flicker -> must be removed
            else if (i >= 70) shape = CursorShape.Hand; // 300ms sustained -> must remain
            samples.Add(new MouseSample
            {
                TimestampTicks = (long)(i * ticksPerSample),
                X = i, Y = 0,
                EventKind = MouseEventKind.Move,
                Shape = shape,
            });
        }

        var recording = new MouseRecordingData
        {
            Samples = samples,
            Clicks = [],
            StartTimestampTicks = 0,
            EndTimestampTicks = samples[^1].TimestampTicks,
            TickFrequency = TickFrequency,
        };

        var smoother = new CursorSmoother
        {
            Algorithm = SmoothingAlgorithm.None,
            Strength = SmoothingStrength.None,
        };

        var result = smoother.SmoothPath(recording, 60);

        Assert.IsTrue(result.Count > 0);
        // The single-sample I-beam flicker must be debounced away entirely.
        Assert.IsFalse(result.Exists(p => p.Shape == CursorShape.IBeam),
            "Transient I-beam flicker should have been removed.");
        // The sustained hand at the end must survive.
        Assert.AreEqual(CursorShape.Hand, result[^1].Shape,
            "Sustained Hand shape should be preserved.");
    }

    #endregion

    #region SVG geometry library

    [TestMethod]
    public void CursorGeometryLibrary_BuildsAllSupportedShapes()
    {
        CanvasDevice? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("Win2D CanvasDevice unavailable in this environment.");
            return;
        }

        using (device)
        {
            var glyphs = CursorGeometryLibrary.Build(device);
            try
            {
                foreach (CursorShape shape in new[]
                {
                    CursorShape.Arrow, CursorShape.Hand, CursorShape.IBeam,
                    CursorShape.ResizeWE, CursorShape.ResizeNS,
                    CursorShape.ResizeNWSE, CursorShape.ResizeNESW,
                })
                {
                    Assert.IsTrue(glyphs.ContainsKey(shape), $"Missing glyph for {shape}");
                    var bounds = glyphs[shape].Geometry.ComputeBounds();
                    Assert.IsTrue(bounds.Width > 0 && bounds.Height > 0,
                        $"{shape} geometry should have non-empty bounds.");
                }
            }
            finally
            {
                foreach (var g in glyphs.Values) g.Geometry.Dispose();
            }
        }
    }

    [TestMethod]
    public void CursorGeometryLibrary_ShapesAreConsistentlySized_WithHotspotsInBounds()
    {
        CanvasDevice? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("Win2D CanvasDevice unavailable in this environment.");
            return;
        }

        using (device)
        {
            var glyphs = CursorGeometryLibrary.Build(device);
            try
            {
                foreach (var (shape, glyph) in glyphs)
                {
                    var b = glyph.Geometry.ComputeBounds();
                    double maxDim = Math.Max(b.Width, b.Height);

                    // All cursors should share a comparable visual size so switching
                    // shapes doesn't make the cursor visibly grow/shrink.
                    Assert.IsTrue(maxDim is >= 18 and <= 34,
                        $"{shape} main dimension {maxDim:F1} is outside the consistent size band (18-34).");

                    // The hotspot must lie within (or on) the glyph bounds, with a
                    // small epsilon, so the drawn cursor aligns with the pointer point.
                    const double eps = 0.5;
                    var h = glyph.Hotspot;
                    Assert.IsTrue(h.X >= b.X - eps && h.X <= b.X + b.Width + eps,
                        $"{shape} hotspot X {h.X} is outside bounds [{b.X}, {b.X + b.Width}].");
                    Assert.IsTrue(h.Y >= b.Y - eps && h.Y <= b.Y + b.Height + eps,
                        $"{shape} hotspot Y {h.Y} is outside bounds [{b.Y}, {b.Y + b.Height}].");
                }
            }
            finally
            {
                foreach (var g in glyphs.Values) g.Geometry.Dispose();
            }
        }
    }

    [TestMethod]
    public void ParseSvgPath_AbsoluteCommands_ProduceExpectedBounds()
    {
        CanvasDevice? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("Win2D CanvasDevice unavailable in this environment.");
            return;
        }

        using (device)
        {
            // A 10x10 square authored with absolute move/lines.
            using var geo = CursorGeometryLibrary.ParseSvgPath(device, "M0 0 L10 0 L10 10 L0 10 Z");
            var bounds = geo.ComputeBounds();
            Assert.AreEqual(0, bounds.X, 0.01);
            Assert.AreEqual(0, bounds.Y, 0.01);
            Assert.AreEqual(10, bounds.Width, 0.01);
            Assert.AreEqual(10, bounds.Height, 0.01);
        }
    }

    [TestMethod]
    public void ParseSvgPath_RelativeCommands_ProduceExpectedBounds()
    {
        CanvasDevice? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("Win2D CanvasDevice unavailable in this environment.");
            return;
        }

        using (device)
        {
            // Same 10x10 square but offset to (5,5) using relative move + lines
            // (including a negative coordinate) to exercise the relative path.
            using var geo = CursorGeometryLibrary.ParseSvgPath(device, "m5 5 l10 0 l0 10 l-10 0 z");
            var bounds = geo.ComputeBounds();
            Assert.AreEqual(5, bounds.X, 0.01);
            Assert.AreEqual(5, bounds.Y, 0.01);
            Assert.AreEqual(10, bounds.Width, 0.01);
            Assert.AreEqual(10, bounds.Height, 0.01);
        }
    }

    [TestMethod]
    public void ParseSvgPath_MalformedInput_ThrowsInsteadOfHanging()
    {
        CanvasDevice? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("Win2D CanvasDevice unavailable in this environment.");
            return;
        }

        using (device)
        {
            // Unsupported command.
            Assert.ThrowsException<FormatException>(() =>
                CursorGeometryLibrary.ParseSvgPath(device, "K1 2 3"));
            // Command missing its operands.
            Assert.ThrowsException<FormatException>(() =>
                CursorGeometryLibrary.ParseSvgPath(device, "M5"));
            // Leading number with no command.
            Assert.ThrowsException<FormatException>(() =>
                CursorGeometryLibrary.ParseSvgPath(device, "10 10"));
        }
    }

    [TestMethod]
    public void ParseSvgPath_NullOrEmpty_Throws()
    {
        CanvasDevice? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("Win2D CanvasDevice unavailable in this environment.");
            return;
        }

        using (device)
        {
            Assert.ThrowsException<ArgumentException>(() =>
                CursorGeometryLibrary.ParseSvgPath(device, ""));
        }
    }

    /// <summary>
    /// The reposition affordance frames the DRAWN cursor, so it needs each glyph's box in the
    /// only origin the rest of the pipeline speaks: the hotspot. These assertions bite because
    /// the shapes genuinely sit differently around it — the arrow hangs entirely down-right of
    /// its tip, while the I-beam and resize arrows straddle their centre.
    /// </summary>
    [TestMethod]
    public void CursorGeometryLibrary_ReportsBoundsRelativeToTheHotspot()
    {
        CanvasDevice? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("Win2D CanvasDevice unavailable in this environment.");
            return;
        }

        using (device)
        {
            var glyphs = CursorGeometryLibrary.Build(device);
            try
            {
                foreach (var (shape, glyph) in glyphs)
                {
                    var raw = glyph.Geometry.ComputeBounds();
                    Assert.AreEqual(raw.Width, glyph.Bounds.Width, 0.01,
                        $"{shape} bounds width should match the geometry's.");
                    Assert.AreEqual(raw.Height, glyph.Bounds.Height, 0.01,
                        $"{shape} bounds height should match the geometry's.");
                    Assert.AreEqual(raw.X - glyph.Hotspot.X, glyph.Bounds.X, 0.01,
                        $"{shape} bounds X should be measured from the hotspot.");
                    Assert.AreEqual(raw.Y - glyph.Hotspot.Y, glyph.Bounds.Y, 0.01,
                        $"{shape} bounds Y should be measured from the hotspot.");
                }

                // The arrow's hotspot is its tip, so the glyph occupies only the down-right
                // quadrant: a box centred on the hotspot would miss it entirely.
                var arrow = glyphs[CursorShape.Arrow].Bounds;
                Assert.IsTrue(arrow.X >= -0.5 && arrow.Y >= -0.5,
                    $"Arrow should hang down-right of its tip, got ({arrow.X:F1}, {arrow.Y:F1}).");

                // The I-beam is centred on its hotspot, so its box must straddle it.
                var ibeam = glyphs[CursorShape.IBeam].Bounds;
                Assert.IsTrue(ibeam.X < 0 && ibeam.Y < 0,
                    $"I-beam should straddle its hotspot, got ({ibeam.X:F1}, {ibeam.Y:F1}).");
                Assert.IsTrue(ibeam.X + ibeam.Width > 0 && ibeam.Y + ibeam.Height > 0,
                    "I-beam box should extend past its hotspot on both axes.");
            }
            finally
            {
                foreach (var g in glyphs.Values) g.Geometry.Dispose();
            }
        }
    }

    /// <summary>
    /// The drawn box has to scale with <see cref="CursorStyle.Scale"/> — the cursor is drawn in
    /// output pixels at that multiplier — and must be empty for a cursor that draws nothing, so
    /// the editor falls back to a fixed target rather than framing a zero-size glyph.
    /// </summary>
    [TestMethod]
    public void GetDrawnCursorBounds_ScalesWithStyle_AndIsEmptyWhenHidden()
    {
        using var single = new CursorRenderer(new CursorStyle { Type = CursorType.Default, Scale = 1.0f });
        using var quad = new CursorRenderer(new CursorStyle { Type = CursorType.Default, Scale = 4.0f });
        using var hidden = new CursorRenderer(new CursorStyle { Type = CursorType.Hidden, Scale = 4.0f });

        var one = single.GetDrawnCursorBounds(CursorShape.Arrow);
        var four = quad.GetDrawnCursorBounds(CursorShape.Arrow);

        Assert.IsTrue(one.Width > 0 && one.Height > 0, "A drawn cursor must report a box.");
        Assert.AreEqual(one.Width * 4, four.Width, 0.01, "Box width should scale with CursorStyle.Scale.");
        Assert.AreEqual(one.Height * 4, four.Height, 0.01, "Box height should scale with CursorStyle.Scale.");

        var none = hidden.GetDrawnCursorBounds(CursorShape.Arrow);
        Assert.AreEqual(0, none.Width, "A hidden cursor draws nothing, so it has no box.");
        Assert.AreEqual(0, none.Height, "A hidden cursor draws nothing, so it has no box.");
    }

    private static CanvasDevice? TryCreateDevice()
    {
        try
        {
            return new CanvasDevice(forceSoftwareRenderer: true);
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
