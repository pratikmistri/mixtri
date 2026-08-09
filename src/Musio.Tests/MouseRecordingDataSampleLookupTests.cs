using Musio.Core.Models;

namespace Musio.Tests;

/// <summary>
/// Covers <see cref="MouseRecordingData.FindSampleNearest"/>, the binary search that replaced two
/// copies of a linear scan over the recorded cursor path (zoom-segment creation and the zoom
/// region editor), both of which run on the UI thread while the user is interacting.
/// </summary>
[TestClass]
public sealed class MouseRecordingDataSampleLookupTests
{
    private const double TickFrequency = 10_000_000.0;
    private const long StartTicks = 5_000_000_000;

    private static MouseRecordingData Recording(params double[] sampleSeconds)
    {
        var data = new MouseRecordingData
        {
            StartTimestampTicks = StartTicks,
            TickFrequency = TickFrequency,
        };

        for (int i = 0; i < sampleSeconds.Length; i++)
        {
            data.Samples.Add(new MouseSample
            {
                TimestampTicks = StartTicks + (long)(sampleSeconds[i] * TickFrequency),
                X = i,
                Y = i * 10,
            });
        }

        return data;
    }

    /// <summary>The exhaustive linear scan this replaced, kept as the reference implementation.</summary>
    private static MouseSample? LinearNearest(MouseRecordingData data, double seconds)
    {
        if (data.Samples.Count == 0) return null;

        MouseSample best = data.Samples[0];
        double bestDist = double.MaxValue;
        foreach (var s in data.Samples)
        {
            double dist = Math.Abs(((s.TimestampTicks - data.StartTimestampTicks) / data.TickFrequency) - seconds);
            if (dist < bestDist) { bestDist = dist; best = s; }
        }
        return best;
    }

    [TestMethod]
    public void NoSamples_ReturnsNull()
        => Assert.IsNull(Recording().FindSampleNearest(1.0));

    [TestMethod]
    public void ExactHit_ReturnsThatSample()
    {
        var data = Recording(0.0, 1.0, 2.0, 3.0);
        Assert.AreEqual(2, data.FindSampleNearest(2.0)!.Value.X);
    }

    [TestMethod]
    public void BetweenSamples_ReturnsTheNearer()
    {
        var data = Recording(0.0, 1.0, 2.0);

        Assert.AreEqual(1, data.FindSampleNearest(1.2)!.Value.X, "1.2s is nearer 1.0s.");
        Assert.AreEqual(2, data.FindSampleNearest(1.8)!.Value.X, "1.8s is nearer 2.0s.");
    }

    [TestMethod]
    public void OutsideTheRecording_ClampsToTheNearestEnd()
    {
        var data = Recording(1.0, 2.0, 3.0);

        Assert.AreEqual(0, data.FindSampleNearest(-50)!.Value.X);
        Assert.AreEqual(2, data.FindSampleNearest(500)!.Value.X);
    }

    [TestMethod]
    public void NonFiniteOrUnusableFrequency_DoesNotThrow()
    {
        var data = Recording(0.0, 1.0);
        Assert.IsNotNull(data.FindSampleNearest(double.NaN));
        Assert.IsNotNull(data.FindSampleNearest(double.PositiveInfinity));

        var noFrequency = new MouseRecordingData { StartTimestampTicks = StartTicks, TickFrequency = 0 };
        noFrequency.Samples.Add(new MouseSample { TimestampTicks = StartTicks, X = 7 });
        Assert.AreEqual(7, noFrequency.FindSampleNearest(1.0)!.Value.X);
    }

    [TestMethod]
    public void RepeatedTimestamps_AreHandled()
    {
        // Two threads append samples, and the recorder clamps out-of-order ones up to their
        // predecessor, so runs of identical timestamps are normal in real recordings.
        var data = Recording(0.0, 1.0, 1.0, 1.0, 2.0);

        var hit = data.FindSampleNearest(1.0);
        Assert.IsNotNull(hit);
        Assert.AreEqual(1.0,
            (hit!.Value.TimestampTicks - data.StartTimestampTicks) / TickFrequency, 1e-9);
    }

    /// <summary>
    /// The binary search must agree with the linear scan it replaced on realistic data, which is
    /// the only thing that makes the swap safe. Seeded, so a failure reproduces exactly.
    /// </summary>
    [TestMethod]
    public void MatchesTheLinearScanItReplaced()
    {
        var rng = new Random(7);

        for (int trial = 0; trial < 200; trial++)
        {
            // Non-decreasing timestamps with duplicates and irregular gaps, as recorded.
            int count = 1 + rng.Next(400);
            var seconds = new double[count];
            double t = 0;
            for (int i = 0; i < count; i++)
            {
                if (rng.Next(6) != 0) t += rng.NextDouble() * 0.05;
                seconds[i] = t;
            }

            var data = Recording(seconds);
            double span = seconds[^1];

            for (int probe = 0; probe < 25; probe++)
            {
                double query = (rng.NextDouble() * (span + 1.0)) - 0.5;

                var expected = LinearNearest(data, query);
                var actual = data.FindSampleNearest(query);

                Assert.IsNotNull(actual);
                Assert.AreEqual(expected!.Value.TimestampTicks, actual!.Value.TimestampTicks,
                    $"trial {trial}: binary search disagreed with the linear scan at {query:F4}s.");
            }
        }
    }
}
