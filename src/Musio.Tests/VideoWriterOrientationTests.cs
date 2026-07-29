using Microsoft.Graphics.Canvas;
using Musio.Core.Capture;
using Musio.Core.Processing;
using Windows.UI;

namespace Musio.Tests;

/// <summary>
/// End-to-end orientation guard for the recording pipeline.
/// </summary>
/// <remarks>
/// The recorded MP4 used to come out vertically mirrored, and nothing caught it because
/// nothing ever displayed that file — the editor and exporter both read the <c>.frames/</c>
/// JPEGs instead. The moment the MP4 became the editor's frame source the bug was visible
/// on screen. These tests drive the real capture path (D3D surface in, MP4 out, decoded
/// back through the real reader) so orientation is asserted rather than assumed.
/// </remarks>
[TestClass]
public class VideoWriterOrientationTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 10;

    // Microsoft.UI.Colors needs WinUI activation, which the test host does not have.
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color Red = Color.FromArgb(255, 255, 0, 0);
    private static readonly Color Blue = Color.FromArgb(255, 0, 0, 255);

    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "musio_orient_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Builds a frame whose top half is red and bottom half is blue, so a vertical flip
    /// is unmistakable.
    /// </summary>
    private static CanvasRenderTarget CreateMarkedFrame(CanvasDevice device)
    {
        var target = new CanvasRenderTarget(device, Width, Height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Black);
            ds.FillRectangle(0, 0, Width, Height / 2f, Red);
            ds.FillRectangle(0, Height / 2f, Width, Height / 2f, Blue);
        }
        return target;
    }

    private static (Color Top, Color Bottom) SampleTopAndBottom(CanvasBitmap frame)
    {
        var pixels = frame.GetPixelColors();
        int w = (int)frame.SizeInPixels.Width;
        int h = (int)frame.SizeInPixels.Height;

        // Sample well inside each band to stay clear of codec ringing at the boundary.
        var top = pixels[(h / 4) * w + (w / 2)];
        var bottom = pixels[(3 * h / 4) * w + (w / 2)];
        return (top, bottom);
    }

    private static bool IsReddish(Color c) => c.R > 120 && c.G < 90 && c.B < 90;
    private static bool IsBluish(Color c) => c.B > 120 && c.R < 90 && c.G < 90;

    [TestMethod]
    public async Task RecordedMp4_IsNotVerticallyFlipped()
    {
        var device = CanvasDevice.GetSharedDevice();
        var videoPath = Path.Combine(_root, "video.mp4");

        using (var writer = new VideoWriter(videoPath, Width, Height, Fps, captureDevice: null))
        {
            using var frame = CreateMarkedFrame(device);
            for (int i = 0; i < Fps; i++)
                writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

            writer.StopAcceptingFrames();
            await writer.WaitForQuiescenceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await writer.FinalizeAsync();

            Assert.IsTrue(writer.FinalizeSucceeded, "the MP4 should have finalized");
        }

        var reader = await Mp4FrameSource.OpenAsync(videoPath, Fps, device);
        Assert.IsNotNull(reader, "the finalized MP4 should be decodable");

        try
        {
            using var decoded = await reader.LoadFrameAsync(reader.FrameCount / 2);
            Assert.IsNotNull(decoded, "a mid-file frame should decode");

            var (top, bottom) = SampleTopAndBottom(decoded);

            Assert.IsTrue(IsReddish(top),
                $"the top of the recording must stay at the top, but it decoded as {top}");
            Assert.IsTrue(IsBluish(bottom),
                $"the bottom of the recording must stay at the bottom, but it decoded as {bottom}");
        }
        finally
        {
            reader.Dispose();
        }
    }

    [TestMethod]
    public async Task CapturedJpegAndDecodedMp4_AgreeOnOrientation()
    {
        // The two frame sources must be interchangeable; if they disagree, the preview
        // silently changes orientation the moment .frames/ is released.
        var device = CanvasDevice.GetSharedDevice();
        var videoPath = Path.Combine(_root, "video.mp4");

        Color jpegTop, jpegBottom;

        using (var writer = new VideoWriter(videoPath, Width, Height, Fps, captureDevice: null))
        {
            using var frame = CreateMarkedFrame(device);
            for (int i = 0; i < Fps; i++)
                writer.WriteFrame(frame, TimeSpan.FromSeconds(i / (double)Fps));

            writer.StopAcceptingFrames();
            await writer.WaitForQuiescenceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

            // Read a captured JPEG before finalization releases them.
            var jpegSource = JpegFrameSource.Open(_root, device);
            Assert.IsNotNull(jpegSource, "captured frames should exist before finalize");
            using (var jpegFrame = await jpegSource.LoadFrameAsync(0))
            {
                Assert.IsNotNull(jpegFrame);
                (jpegTop, jpegBottom) = SampleTopAndBottom(jpegFrame);
            }
            jpegSource.Dispose();

            await writer.FinalizeAsync();
        }

        var reader = await Mp4FrameSource.OpenAsync(videoPath, Fps, device);
        Assert.IsNotNull(reader);

        try
        {
            using var decoded = await reader.LoadFrameAsync(reader.FrameCount / 2);
            Assert.IsNotNull(decoded);
            var (mp4Top, mp4Bottom) = SampleTopAndBottom(decoded);

            Assert.AreEqual(IsReddish(jpegTop), IsReddish(mp4Top),
                "JPEG and MP4 sources disagree about which band is on top");
            Assert.AreEqual(IsBluish(jpegBottom), IsBluish(mp4Bottom),
                "JPEG and MP4 sources disagree about which band is on the bottom");
        }
        finally
        {
            reader.Dispose();
        }
    }
}
