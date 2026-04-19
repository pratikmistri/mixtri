namespace Musio.Tests;

using System.Reflection;
using Musio.Core.Capture;
using Musio.Core.Models;

[TestClass]
public sealed class MouseHookRecorderTests
{
    #region MouseSample

    [TestMethod]
    public void MouseSample_DefaultValues_AreZero()
    {
        var sample = new MouseSample();

        Assert.AreEqual(0L, sample.TimestampTicks);
        Assert.AreEqual(0, sample.X);
        Assert.AreEqual(0, sample.Y);
        Assert.AreEqual(MouseEventKind.Move, sample.EventKind);
        Assert.AreEqual(MouseButton.None, sample.Button);
        Assert.AreEqual((short)0, sample.ScrollDelta);
    }

    [TestMethod]
    public void MouseSample_FieldAssignment_RetainsValues()
    {
        var sample = new MouseSample
        {
            TimestampTicks = 123456789L,
            X = 1920,
            Y = 1080,
            EventKind = MouseEventKind.ButtonDown,
            Button = MouseButton.Left,
            ScrollDelta = 120,
        };

        Assert.AreEqual(123456789L, sample.TimestampTicks);
        Assert.AreEqual(1920, sample.X);
        Assert.AreEqual(1080, sample.Y);
        Assert.AreEqual(MouseEventKind.ButtonDown, sample.EventKind);
        Assert.AreEqual(MouseButton.Left, sample.Button);
        Assert.AreEqual((short)120, sample.ScrollDelta);
    }

    [TestMethod]
    public void MouseSample_NegativeCoordinates_ArePreserved()
    {
        var sample = new MouseSample { X = -100, Y = -200, ScrollDelta = -120 };

        Assert.AreEqual(-100, sample.X);
        Assert.AreEqual(-200, sample.Y);
        Assert.AreEqual((short)-120, sample.ScrollDelta);
    }

    #endregion

    #region ClickEvent

    [TestMethod]
    public void ClickEvent_RecordCreation_HasCorrectValues()
    {
        var click = new ClickEvent(
            TimestampTicks: 9999L,
            X: 500,
            Y: 300,
            Button: MouseButton.Right,
            IsDown: true);

        Assert.AreEqual(9999L, click.TimestampTicks);
        Assert.AreEqual(500, click.X);
        Assert.AreEqual(300, click.Y);
        Assert.AreEqual(MouseButton.Right, click.Button);
        Assert.IsTrue(click.IsDown);
    }

    [TestMethod]
    public void ClickEvent_Equality_WorksForRecords()
    {
        var a = new ClickEvent(100L, 10, 20, MouseButton.Left, true);
        var b = new ClickEvent(100L, 10, 20, MouseButton.Left, true);
        var c = new ClickEvent(100L, 10, 20, MouseButton.Left, false);

        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, c);
    }

    #endregion

    #region MouseRecordingData

    [TestMethod]
    public void MouseRecordingData_Duration_CalculatedCorrectly()
    {
        long freq = 10_000_000; // 1 tick = 100 ns
        long start = 0;
        long end = 5 * freq; // 5 seconds worth of ticks

        var data = new MouseRecordingData
        {
            StartTimestampTicks = start,
            EndTimestampTicks = end,
            TickFrequency = freq,
        };

        Assert.AreEqual(TimeSpan.FromSeconds(5), data.Duration);
    }

    [TestMethod]
    public void MouseRecordingData_Duration_FractionalSeconds()
    {
        long freq = 10_000_000;
        long start = 1_000_000;
        long end = start + (long)(2.5 * freq); // 2.5 seconds

        var data = new MouseRecordingData
        {
            StartTimestampTicks = start,
            EndTimestampTicks = end,
            TickFrequency = freq,
        };

        Assert.AreEqual(2.5, data.Duration.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void MouseRecordingData_DefaultCollections_AreEmpty()
    {
        var data = new MouseRecordingData();

        Assert.AreEqual(0, data.Samples.Count);
        Assert.AreEqual(0, data.Clicks.Count);
    }

    #endregion

    #region Binary Serialization

    [TestMethod]
    public void SaveAndLoad_RoundTrip_PreservesData()
    {
        var samples = new List<MouseSample>
        {
            new() { TimestampTicks = 100, X = 10, Y = 20, EventKind = MouseEventKind.Move, Button = MouseButton.None, ScrollDelta = 0 },
            new() { TimestampTicks = 200, X = 30, Y = 40, EventKind = MouseEventKind.ButtonDown, Button = MouseButton.Left, ScrollDelta = 0 },
            new() { TimestampTicks = 300, X = 50, Y = 60, EventKind = MouseEventKind.Scroll, Button = MouseButton.None, ScrollDelta = 120 },
        };

        var clicks = new List<ClickEvent>
        {
            new(200, 30, 40, MouseButton.Left, true),
            new(250, 30, 40, MouseButton.Left, false),
        };

        var original = new MouseRecordingData
        {
            Samples = samples,
            Clicks = clicks,
            StartTimestampTicks = 100,
            EndTimestampTicks = 300,
            TickFrequency = 10_000_000.0,
        };

        string tempFile = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            $"test_mouse_{Guid.NewGuid():N}.mcur");

        try
        {
            // Use reflection to call private static SaveDataToFile
            var saveMethod = typeof(MouseHookRecorder).GetMethod(
                "SaveDataToFile",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(saveMethod, "SaveDataToFile method should exist");
            saveMethod.Invoke(null, [tempFile, original]);

            // Load back using public API
            var loaded = MouseHookRecorder.LoadFromFile(tempFile);

            // Verify header fields
            Assert.AreEqual(original.StartTimestampTicks, loaded.StartTimestampTicks);
            Assert.AreEqual(original.EndTimestampTicks, loaded.EndTimestampTicks);
            Assert.AreEqual(original.TickFrequency, loaded.TickFrequency, 0.001);

            // Verify samples
            Assert.AreEqual(original.Samples.Count, loaded.Samples.Count);
            for (int i = 0; i < original.Samples.Count; i++)
            {
                Assert.AreEqual(original.Samples[i].TimestampTicks, loaded.Samples[i].TimestampTicks, $"Sample[{i}].TimestampTicks");
                Assert.AreEqual(original.Samples[i].X, loaded.Samples[i].X, $"Sample[{i}].X");
                Assert.AreEqual(original.Samples[i].Y, loaded.Samples[i].Y, $"Sample[{i}].Y");
                Assert.AreEqual(original.Samples[i].EventKind, loaded.Samples[i].EventKind, $"Sample[{i}].EventKind");
                Assert.AreEqual(original.Samples[i].Button, loaded.Samples[i].Button, $"Sample[{i}].Button");
                Assert.AreEqual(original.Samples[i].ScrollDelta, loaded.Samples[i].ScrollDelta, $"Sample[{i}].ScrollDelta");
            }

            // Verify clicks
            Assert.AreEqual(original.Clicks.Count, loaded.Clicks.Count);
            for (int i = 0; i < original.Clicks.Count; i++)
            {
                Assert.AreEqual(original.Clicks[i], loaded.Clicks[i], $"Click[{i}]");
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void LoadFromFile_InvalidMagic_ThrowsInvalidDataException()
    {
        string tempFile = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            $"test_bad_magic_{Guid.NewGuid():N}.mcur");

        try
        {
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]);

            Assert.ThrowsException<InvalidDataException>(() =>
                MouseHookRecorder.LoadFromFile(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void SaveAndLoad_EmptyRecording_RoundTrips()
    {
        var original = new MouseRecordingData
        {
            Samples = [],
            Clicks = [],
            StartTimestampTicks = 0,
            EndTimestampTicks = 1000,
            TickFrequency = 10_000_000.0,
        };

        string tempFile = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            $"test_empty_{Guid.NewGuid():N}.mcur");

        try
        {
            var saveMethod = typeof(MouseHookRecorder).GetMethod(
                "SaveDataToFile",
                BindingFlags.NonPublic | BindingFlags.Static);
            saveMethod!.Invoke(null, [tempFile, original]);

            var loaded = MouseHookRecorder.LoadFromFile(tempFile);

            Assert.AreEqual(0, loaded.Samples.Count);
            Assert.AreEqual(0, loaded.Clicks.Count);
            Assert.AreEqual(original.StartTimestampTicks, loaded.StartTimestampTicks);
            Assert.AreEqual(original.EndTimestampTicks, loaded.EndTimestampTicks);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    #endregion
}
