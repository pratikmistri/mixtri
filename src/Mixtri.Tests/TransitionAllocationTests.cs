using Mixtri.Core.Timeline;

namespace Mixtri.Tests;

[TestClass]
public class TransitionAllocationTests
{
    [TestMethod]
    public void ResolutionMatchesPreviousFieldsForAllEffectsEasingAndBoundaries()
    {
        var configurations = new List<TransitionConfig?> { null };
        foreach (var type in Enum.GetValues<TransitionType>())
        foreach (var easing in Enum.GetValues<TransitionEasing>())
            configurations.Add(new TransitionConfig { Type = type, Easing = easing, Duration = TimeSpan.FromTicks(8_000_003) });

        foreach (var config in configurations)
        {
            var model = Model(config);
            var incoming = model.Segments[^1];
            foreach (long offset in new[] { -1L, 0, 1, 1_500_000, 3_000_000, 3_000_001, 9_000_000, 9_000_001 })
            foreach (TimeSpan? cap in new TimeSpan?[] { null, TimeSpan.Zero, TimeSpan.FromTicks(1_234_567) })
            {
                var time = incoming.Start + TimeSpan.FromTicks(offset);
                Assert.AreEqual(Previous(model, time, cap), TransitionResolver.Resolve(model, time, cap));
            }
        }
    }

    [TestMethod]
    public void LegacyOverrideStillIgnoresExplicitConfiguration()
    {
        foreach (var type in Enum.GetValues<TransitionType>())
        {
            var model = Model(new TransitionConfig { Type = type, Easing = TransitionEasing.EaseOut });
            foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromTicks(100_001), TimeSpan.FromSeconds(2) })
            foreach (long ticks in new[] { 0L, 1, 100_000, 3_000_000, 4_000_000 })
            {
                var time = model.Segments[^1].Start + TimeSpan.FromTicks(ticks);
                Assert.AreEqual(Previous(model, time, null, true, duration),
                    TransitionResolver.ResolveLegacyOnly(model, time, duration));
            }
        }
    }

    [TestMethod]
    public void LegacyFrameResolutionAllocatesNothing()
    {
        var model = Model(null);
        var time = model.Segments[^1].Start + TimeSpan.FromMilliseconds(200);
        for (int i = 0; i < 1000; i++)
        {
            TransitionResolver.Resolve(model, time);
            Previous(model, time, null);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        TransitionResolution current = default;
        for (int i = 0; i < 100000; i++) current = TransitionResolver.Resolve(model, time);
        long currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        TransitionResolution previous = default;
        for (int i = 0; i < 100000; i++) previous = Previous(model, time, null);
        long previousBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(previous, current);
        Assert.AreEqual(0L, currentBytes);
        Assert.IsTrue(previousBytes > 1_000_000);
        Console.WriteLine($"100,000 transition resolutions: {previousBytes:N0} previous bytes, {currentBytes:N0} current bytes.");
    }

    private static TimelineModel Model(TransitionConfig? configuration)
    {
        var model = new TimelineModel();
        model.Segments.Add(new TextSlideSegment { Duration = TimeSpan.FromTicks(6_000_001) });
        model.Segments.Add(new VideoSegment
        {
            TrackIndex = 2, Start = TimeSpan.Zero, Duration = TimeSpan.FromSeconds(10),
        });
        model.Segments.Add(new VideoSegment
        {
            Start = TimeSpan.FromTicks(6_000_001), Duration = TimeSpan.FromTicks(9_000_001),
            InTransition = configuration,
        });
        return model;
    }

    private static TransitionResolution Previous(TimelineModel model, TimeSpan time, TimeSpan? cap,
        bool ignoreExplicit = false, TimeSpan? legacyDuration = null)
    {
        TimelineSegment? incoming = null, outgoing = null;
        foreach (var segment in model.Segments)
        {
            if (segment.TrackIndex != TimelineModel.BaseTrackIndex) continue;
            if (time >= segment.Start && time < segment.End) { incoming = segment; break; }
            outgoing = segment;
        }
        if (incoming is null || outgoing is null) return TransitionResolution.None;
        TransitionConfig config;
        bool legacy;
        if (!ignoreExplicit && incoming.InTransition is { } explicitConfig)
        {
            if (explicitConfig.Type == TransitionType.None) return TransitionResolution.None;
            config = explicitConfig;
            legacy = false;
        }
        else
        {
            if (incoming is not TextSlideSegment && outgoing is not TextSlideSegment) return TransitionResolution.None;
            config = new TransitionConfig
            {
                Type = TransitionType.CrossFade,
                Duration = legacyDuration ?? TimeSpan.FromMilliseconds(500),
                Easing = TransitionEasing.Linear,
            };
            legacy = true;
        }
        var duration = TimeSpan.FromTicks(Math.Min(incoming.Duration.Ticks / 2, outgoing.Duration.Ticks / 2));
        duration = duration < config.Duration ? duration : config.Duration;
        if (cap is { } maximum && maximum < duration) duration = maximum;
        if (duration <= TimeSpan.Zero) return TransitionResolution.None;
        var local = time - incoming.Start;
        if (local < TimeSpan.Zero || local >= duration) return TransitionResolution.None;
        double raw = Math.Clamp((double)local.Ticks / duration.Ticks, 0, 1);
        var outgoingOffset = legacy
            ? TimeSpan.FromTicks(Math.Max(0, outgoing.Duration.Ticks - 1))
            : outgoing.Duration + local;
        return new(true, config.Type, duration, raw, TransitionResolver.Ease(config.Easing, raw),
            incoming, outgoing, outgoingOffset);
    }
}
