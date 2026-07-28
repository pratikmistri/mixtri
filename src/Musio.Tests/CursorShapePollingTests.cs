namespace Musio.Tests;

using System.Reflection;
using System.Runtime.InteropServices;
using Musio.Core.Capture;
using Musio.Core.Models;

/// <summary>
/// Covers the continuous cursor-shape sampling path: the poll-interval contract and the
/// monotonic sample append shared by the hook thread and the shape poller thread.
/// </summary>
[TestClass]
public sealed class CursorShapePollingTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static List<MouseSample> GetSamples(MouseHookRecorder recorder)
    {
        var field = typeof(MouseHookRecorder).GetField(
            "_samples", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field, "_samples field should exist");
        return (List<MouseSample>)field.GetValue(recorder)!;
    }

    private static void AddSample(MouseHookRecorder recorder, MouseSample sample)
    {
        var method = typeof(MouseHookRecorder).GetMethod(
            "AddSampleLocked", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method, "AddSampleLocked method should exist");
        method.Invoke(recorder, [sample]);
    }

    [TestMethod]
    public void ShapeSampleInterval_DefaultsToContinuousPolling()
    {
        using var recorder = new MouseHookRecorder();

        Assert.AreEqual(MouseHookRecorder.DefaultShapeSampleIntervalMs, recorder.ShapeSampleIntervalMs);
        Assert.IsTrue(recorder.ShapeSampleIntervalMs <= 33,
            "Shape polling must be at least ~30Hz so hover-driven shape changes feel accurate.");
    }

    [TestMethod]
    public void ShapeSampleInterval_IsClampedToSupportedRange()
    {
        using var recorder = new MouseHookRecorder();

        recorder.ShapeSampleIntervalMs = 0;
        Assert.AreEqual(MouseHookRecorder.MinShapeSampleIntervalMs, recorder.ShapeSampleIntervalMs);

        recorder.ShapeSampleIntervalMs = int.MaxValue;
        Assert.AreEqual(MouseHookRecorder.MaxShapeSampleIntervalMs, recorder.ShapeSampleIntervalMs);

        recorder.ShapeSampleIntervalMs = 50;
        Assert.AreEqual(50, recorder.ShapeSampleIntervalMs);
    }

    [TestMethod]
    public void AddSample_OutOfOrderTimestamp_IsClampedToKeepSamplesMonotonic()
    {
        using var recorder = new MouseHookRecorder();

        AddSample(recorder, new MouseSample { TimestampTicks = 1000, Shape = CursorShape.Arrow });
        // Simulates the shape poller racing the hook thread: an older timestamp arrives late.
        AddSample(recorder, new MouseSample { TimestampTicks = 900, Shape = CursorShape.Hand });
        AddSample(recorder, new MouseSample { TimestampTicks = 1200, Shape = CursorShape.IBeam });

        var samples = GetSamples(recorder);

        Assert.AreEqual(3, samples.Count);
        Assert.AreEqual(1000, samples[1].TimestampTicks, "Late sample should be clamped, not reordered.");
        Assert.AreEqual(CursorShape.Hand, samples[1].Shape, "Clamping must not change the recorded shape.");

        for (int i = 1; i < samples.Count; i++)
        {
            Assert.IsTrue(samples[i].TimestampTicks >= samples[i - 1].TimestampTicks,
                $"Samples must be non-decreasing in time at index {i}.");
        }
    }

    [TestMethod]
    public void CursorShapeResolver_ResolveWithPosition_ReportsLiveCursorPosition()
    {
        var resolver = new CursorShapeResolver();

        var shape = resolver.Resolve(out int x, out int y, out bool positionValid);

        Assert.IsTrue(Enum.IsDefined(shape), $"Unexpected shape value: {shape}");

        if (!positionValid)
            return; // No visible cursor on this session — nothing further to assert.

        Assert.IsTrue(GetCursorPos(out POINT expected), "GetCursorPos should succeed.");
        Assert.IsTrue(Math.Abs(expected.X - x) <= 64 && Math.Abs(expected.Y - y) <= 64,
            $"Resolved position ({x},{y}) should track the live cursor ({expected.X},{expected.Y}).");
    }
}
