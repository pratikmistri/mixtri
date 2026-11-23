using Mixtri.Core.Export;
using Mixtri.Core.Settings;

namespace Mixtri.Tests;

[TestClass]
public class ExportOutputHandlingTests
{
    private static string NewTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mixtri_export_output_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [TestMethod]
    public void ResolveOutputPath_UsesChosenFileVerbatim_EvenWhenItExists()
    {
        string folder = NewTempFolder();
        try
        {
            // The save picker confirms the overwrite and may pre-create an empty file.
            string chosen = Path.Combine(folder, "My Demo.mp4");
            File.WriteAllBytes(chosen, []);

            string resolved = ExportEngine.ResolveOutputPath(folder, chosen, "Project", VideoFormat.MP4);

            Assert.AreEqual(Path.GetFullPath(chosen), resolved);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ResolveOutputPath_FixesMismatchedExtension_WithoutOverwritingAnotherFile()
    {
        string folder = NewTempFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "clip.gif"), [1]);

            string resolved = ExportEngine.ResolveOutputPath(
                folder, Path.Combine(folder, "clip.mp4"), "Project", VideoFormat.GIF);

            Assert.AreEqual(Path.Combine(folder, "clip (1).gif"), resolved);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ResolveOutputPath_WebMIsWrittenAsMp4()
    {
        string folder = NewTempFolder();
        try
        {
            string resolved = ExportEngine.ResolveOutputPath(
                folder, Path.Combine(folder, "clip.webm"), "Project", VideoFormat.WebM);

            Assert.AreEqual(".mp4", Path.GetExtension(resolved));
            Assert.AreEqual(VideoFormat.MP4, ExportEngine.GetEffectiveFormat(VideoFormat.WebM));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ResolveOutputPath_FallsBackToUniqueProjectNameInFolder_AndIsIdempotent()
    {
        string folder = NewTempFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "Demo.mp4"), [1]);

            string resolved = ExportEngine.ResolveOutputPath(folder, null, "Demo", VideoFormat.MP4);

            Assert.AreEqual(Path.Combine(folder, "Demo (1).mp4"), resolved);
            Assert.AreEqual(resolved, ExportEngine.ResolveOutputPath(folder, resolved, "Demo", VideoFormat.MP4));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void RemovePartialOutput_DeletesThisExportsFilesButKeepsOlderOnes()
    {
        string folder = NewTempFolder();
        try
        {
            string planned = Path.Combine(folder, "Demo.mp4");
            File.WriteAllBytes(planned, [1, 2, 3]);
            string temp = Path.Combine(folder, $"{VideoEncoder.TempVideoPrefix}Demo_{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(temp, [1]);
            string otherExportTemp = Path.Combine(folder, $"{VideoEncoder.TempVideoPrefix}Other_{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(otherExportTemp, [1]);

            ExportWorkerClient.RemovePartialOutput(planned, DateTime.UtcNow.AddMinutes(-1));

            Assert.IsFalse(File.Exists(planned));
            Assert.IsFalse(File.Exists(temp));
            Assert.IsTrue(File.Exists(otherExportTemp), "Another export's temp file must not be swept.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void RemovePartialOutput_KeepsNonEmptyFileTheExportNeverWrote()
    {
        string folder = NewTempFolder();
        try
        {
            string planned = Path.Combine(folder, "Existing.mp4");
            File.WriteAllBytes(planned, [1, 2, 3]);
            File.SetLastWriteTimeUtc(planned, DateTime.UtcNow.AddHours(-1));

            ExportWorkerClient.RemovePartialOutput(planned, DateTime.UtcNow);

            Assert.IsTrue(File.Exists(planned));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void GifFrameDelays_AccumulateErrorSoAverageIsExact()
    {
        var delays = Enumerable.Range(0, 30).Select(i => (int)GifEncoder.GetFrameDelayCentiseconds(i, 30)).ToArray();

        CollectionAssert.AreEqual(new[] { 3, 3, 4 }, delays.Take(3).ToArray());
        Assert.AreEqual(100, delays.Sum(), "30 frames at 30 fps must last exactly one second.");
        Assert.IsTrue(delays.All(d => d is 3 or 4));
    }

    [TestMethod]
    public void GifFrameDelays_ExactRatesStayConstant_AndLongRunsDoNotDrift()
    {
        Assert.IsTrue(Enumerable.Range(0, 50).All(i => GifEncoder.GetFrameDelayCentiseconds(i, 25) == 4));
        Assert.IsTrue(Enumerable.Range(0, 20).All(i => GifEncoder.GetFrameDelayCentiseconds(i, 10) == 10));

        int frames = 30 * 60 * 10; // ten minutes at 30 fps
        long total = 0;
        for (int i = 0; i < frames; i++) total += GifEncoder.GetFrameDelayCentiseconds(i, 30);
        Assert.AreEqual(60_000L, total);
    }

    [TestMethod]
    public void EtaEstimator_UsesRecentRate_AndRejectsOutOfOrderReports()
    {
        var eta = new ExportEtaEstimator(TimeSpan.FromSeconds(10));
        // Slow start (setup) followed by a steady 1%/s.
        Assert.IsTrue(eta.TryAdd(TimeSpan.FromSeconds(0), 0));
        Assert.IsTrue(eta.TryAdd(TimeSpan.FromSeconds(30), 1));
        for (int s = 31; s <= 50; s++)
            Assert.IsTrue(eta.TryAdd(TimeSpan.FromSeconds(s), s - 29));

        Assert.IsFalse(eta.TryAdd(TimeSpan.FromSeconds(50.5), 5), "Backwards progress must be dropped.");
        Assert.AreEqual(21, eta.LastPercent, 1e-9);

        var remaining = eta.EstimateRemaining();
        Assert.IsNotNull(remaining);
        Assert.AreEqual(79, remaining.Value.TotalSeconds, 1.0);
    }

    [TestMethod]
    public void EtaEstimator_PhaseChangeRestartsTheWindow()
    {
        var eta = new ExportEtaEstimator();
        eta.TryAdd(TimeSpan.FromSeconds(0), 0, "Preparing audio…");
        eta.TryAdd(TimeSpan.FromSeconds(20), 2, "Preparing audio…");
        eta.TryAdd(TimeSpan.FromSeconds(21), 3);

        Assert.IsNull(eta.EstimateRemaining(), "One sample in the new phase is not a rate.");

        eta.TryAdd(TimeSpan.FromSeconds(22), 5);
        Assert.AreEqual(47.5, eta.EstimateRemaining()!.Value.TotalSeconds, 0.5);
    }
}
