using Microsoft.Graphics.Canvas;
using Mixtri.Core.AI;
using Mixtri.Core.Processing;
using Mixtri.Tests.TestSupport;
using Windows.UI;

namespace Mixtri.Tests;

/// <summary>
/// Regression guards for the second round of code-review fixes: preview frames accepted
/// without confirmation must never be cached, and overlapping subtitles must resolve the
/// same way regardless of playback history.
/// </summary>
[TestClass]
public sealed class ReviewFix2FrameAndSubtitleTests
{
    private static readonly Color Stale = Color.FromArgb(255, 200, 30, 30);
    private static readonly Color Fresh = Color.FromArgb(255, 30, 200, 30);

    // ── VideoFrameReader: unconfirmed frames ───────────────────────

    [TestMethod]
    public async Task UnconfirmedFrames_AreNeverCachedUnderTheirIndex()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var source = new ConfirmingTestSource(device, unconfirmedDeliveries: 2);
        using var reader = new VideoFrameReader(source, 10);

        // Two unconfirmed (possibly stale) deliveries of the same index. Without the guard
        // the second would earn a cache slot and be served for index 0 indefinitely.
        using (var first = await reader.LoadFrameAsync(0))
            Assert.AreEqual(Stale.R, first!.GetPixelColors()[0].R);
        using (var second = await reader.LoadFrameAsync(0))
            Assert.AreEqual(Stale.R, second!.GetPixelColors()[0].R);

        using var third = await reader.LoadFrameAsync(0);
        Assert.IsNotNull(third);
        Assert.AreEqual(Fresh.R, third.GetPixelColors()[0].R);
        Assert.AreEqual(3, source.LoadCount, "the stale frame must not have been served from cache");
    }

    [TestMethod]
    public async Task ConfirmedFrames_AreStillCachedOnRepeat()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var source = new ConfirmingTestSource(device, unconfirmedDeliveries: 0);
        using var reader = new VideoFrameReader(source, 10);

        using var first = await reader.LoadFrameAsync(0);
        using var second = await reader.LoadFrameAsync(0);
        using var third = await reader.LoadFrameAsync(0);

        Assert.IsNotNull(third);
        Assert.AreEqual(Fresh.R, third.GetPixelColors()[0].R);
        Assert.AreEqual(2, source.LoadCount, "the third request should hit the cache");
    }

    private sealed class ConfirmingTestSource(CanvasDevice device, int unconfirmedDeliveries)
        : IFrameSource, IConfirmingFrameSource
    {
        private int _remainingUnconfirmed = unconfirmedDeliveries;

        public int Width => 16;
        public int Height => 16;
        public int FrameCount => 4;
        public FrameSourceKind Kind => FrameSourceKind.EncodedVideo;
        public int LoadCount { get; private set; }

        public async Task<CanvasBitmap?> LoadFrameAsync(int frameIndex)
            => (await LoadFrameWithConfirmationAsync(frameIndex)).Frame;

        public Task<(CanvasBitmap? Frame, bool Confirmed)> LoadFrameWithConfirmationAsync(int frameIndex)
        {
            LoadCount++;
            bool confirmed = _remainingUnconfirmed <= 0;
            if (!confirmed)
                _remainingUnconfirmed--;

            var bitmap = CanvasBitmapTestHelpers.MakeBitmap(
                device, confirmed ? Fresh : Stale, Width, Height);
            return Task.FromResult<(CanvasBitmap? Frame, bool Confirmed)>((bitmap, confirmed));
        }

        public void Dispose() { }
    }

    // ── SubtitleBurner: overlap precedence ─────────────────────────

    private static SubtitleSegment Seg(double start, double end, string text)
        => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    [TestMethod]
    public void OverlappingSubtitles_PlaybackAndDirectSeek_PickTheSameSegment()
    {
        var a = Seg(0, 10, "A");
        var b = Seg(5, 15, "B");

        using var played = new SubtitleBurner([a, b], new SubtitleStyle());
        Assert.AreSame(a, played.FindActiveSegment(4));
        Assert.AreSame(b, played.FindActiveSegment(6), "playing from 4s into the overlap");

        using var seeked = new SubtitleBurner([a, b], new SubtitleStyle());
        Assert.AreSame(b, seeked.FindActiveSegment(6), "seeking straight into the overlap");

        // Leaving and re-entering the overlap from either side resolves the same way.
        Assert.AreSame(b, played.FindActiveSegment(12));
        Assert.AreSame(b, played.FindActiveSegment(6));
        Assert.AreSame(a, played.FindActiveSegment(2));
        Assert.AreSame(b, played.FindActiveSegment(9.5));
    }

    [TestMethod]
    public void NestedSubtitle_OuterResumesAfterInnerEnds()
    {
        var outer = Seg(0, 20, "outer");
        var inner = Seg(5, 10, "inner");

        using var burner = new SubtitleBurner([inner, outer], new SubtitleStyle());
        Assert.AreSame(outer, burner.FindActiveSegment(3));
        Assert.AreSame(inner, burner.FindActiveSegment(7));
        Assert.AreSame(outer, burner.FindActiveSegment(12));
        Assert.IsNull(burner.FindActiveSegment(25));
        Assert.AreSame(inner, burner.FindActiveSegment(7));
    }
}
