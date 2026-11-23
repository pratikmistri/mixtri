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
    public void RemoveJobFiles_DeletesExactlyThisJobsFiles_NeverTheDestination()
    {
        string folder = NewTempFolder();
        try
        {
            string jobId = ExportJobFiles.NewJobId();
            string planned = Path.Combine(folder, "Demo.mp4");
            // A pre-existing file the user chose to overwrite, written moments ago: no
            // timestamp heuristic may treat it as this export's partial output.
            File.WriteAllBytes(planned, [1, 2, 3]);
            var files = ExportJobFiles.For(planned, jobId);
            File.WriteAllBytes(files.StagingOutputPath, [1]);
            File.WriteAllBytes(files.TempVideoPath, [1]);

            // Same stem, other job; a longer stem sharing the prefix; another output entirely.
            var otherJob = ExportJobFiles.For(planned, ExportJobFiles.NewJobId());
            File.WriteAllBytes(otherJob.StagingOutputPath, [1]);
            File.WriteAllBytes(otherJob.TempVideoPath, [1]);
            var preview = ExportJobFiles.For(Path.Combine(folder, "Demo_preview.mp4"), ExportJobFiles.NewJobId());
            File.WriteAllBytes(preview.TempVideoPath, [1]);
            string otherOutputTemp = Path.Combine(folder, $"{VideoEncoder.TempVideoPrefix}Other_{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(otherOutputTemp, [1]);

            int removed = ExportWorkerClient.RemoveJobFiles(planned, jobId);

            Assert.AreEqual(2, removed);
            Assert.IsFalse(File.Exists(files.StagingOutputPath));
            Assert.IsFalse(File.Exists(files.TempVideoPath));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(planned),
                "The destination is never deleted by cleanup.");
            Assert.IsTrue(File.Exists(otherJob.StagingOutputPath), "A concurrent export's staging file must survive.");
            Assert.IsTrue(File.Exists(otherJob.TempVideoPath), "A concurrent export's temp file must survive.");
            Assert.IsTrue(File.Exists(preview.TempVideoPath), "Demo_preview's temp file must not match Demo.");
            Assert.IsTrue(File.Exists(otherOutputTemp));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void RemoveJobFiles_InvalidJobId_DeletesNothing()
    {
        string folder = NewTempFolder();
        try
        {
            string planned = Path.Combine(folder, "Demo.mp4");
            string wildcardLike = Path.Combine(folder, $"{VideoEncoder.TempVideoPrefix}Demo_{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(wildcardLike, [1]);

            Assert.AreEqual(0, ExportWorkerClient.RemoveJobFiles(planned, "*"));
            Assert.AreEqual(0, ExportWorkerClient.RemoveJobFiles(planned, @"..\..\x"));
            Assert.IsTrue(File.Exists(wildcardLike));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ExportJobFiles_NamesEmbedTheExactStemAndJobId_BesideTheOutput()
    {
        string folder = NewTempFolder();
        try
        {
            string jobId = new string('a', 16) + new string('0', 16);
            var files = ExportJobFiles.For(Path.Combine(folder, "My Demo.gif"), jobId);

            Assert.AreEqual(Path.Combine(folder, "My Demo.gif"), files.OutputPath);
            Assert.AreEqual(Path.Combine(folder, $".mixtri_out_My Demo_{jobId}.gif"), files.StagingOutputPath);
            Assert.AreEqual(Path.Combine(folder, $".mixtri_video_My Demo_{jobId}.mp4"), files.TempVideoPath);
            CollectionAssert.DoesNotContain(files.OwnedTempPaths.ToList(), files.OutputPath);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ExportJobFiles_JobIdMustBe32LowercaseHexDigits()
    {
        Assert.IsTrue(ExportJobFiles.IsValidJobId(ExportJobFiles.NewJobId()));
        Assert.IsTrue(ExportJobFiles.IsValidJobId(Guid.NewGuid().ToString("N")));

        Assert.IsFalse(ExportJobFiles.IsValidJobId(null));
        Assert.IsFalse(ExportJobFiles.IsValidJobId(string.Empty));
        Assert.IsFalse(ExportJobFiles.IsValidJobId(Guid.NewGuid().ToString("D")), "dashes");
        Assert.IsFalse(ExportJobFiles.IsValidJobId(Guid.NewGuid().ToString("N").ToUpperInvariant()), "uppercase");
        Assert.IsFalse(ExportJobFiles.IsValidJobId(new string('a', 31)), "too short");
        Assert.IsFalse(ExportJobFiles.IsValidJobId(new string('a', 33)), "too long");
        Assert.IsFalse(ExportJobFiles.IsValidJobId(new string('g', 32)), "not hex");
        Assert.IsFalse(ExportJobFiles.IsValidJobId("*"));
        Assert.IsFalse(ExportJobFiles.IsValidJobId(@"..\" + new string('a', 29)), "path separators");

        Assert.ThrowsException<ArgumentException>(() =>
            ExportJobFiles.For(Path.Combine(Path.GetTempPath(), "Demo.mp4"), "*"));
    }

    [TestMethod]
    public void ExportJobFiles_PublishReplacesTheDestinationOnlyWithTheFinishedStagingFile()
    {
        string folder = NewTempFolder();
        try
        {
            string destination = Path.Combine(folder, "Existing.mp4");
            File.WriteAllBytes(destination, [9, 9]);
            var files = ExportJobFiles.For(destination, ExportJobFiles.NewJobId());
            File.WriteAllBytes(files.StagingOutputPath, [1, 2, 3, 4]);

            // A failed/cancelled job: cleanup leaves the user's existing file untouched.
            files.DeleteOwnedTempFiles();
            CollectionAssert.AreEqual(new byte[] { 9, 9 }, File.ReadAllBytes(destination));

            // A successful job: the staging file atomically replaces the destination.
            File.WriteAllBytes(files.StagingOutputPath, [1, 2, 3, 4]);
            files.PublishStagedOutput();
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(destination));
            Assert.IsFalse(File.Exists(files.StagingOutputPath));
            Assert.AreEqual(0, files.DeleteOwnedTempFiles());
            Assert.IsTrue(File.Exists(destination));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ExportJobFiles_PublishToLockedDestinationFallsBackToSiblingName()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Only Windows blocks replacing an open file.");
        string folder = NewTempFolder();
        try
        {
            string destination = Path.Combine(folder, "Locked.mp4");
            File.WriteAllBytes(destination, [9, 9]);
            var files = ExportJobFiles.For(destination, ExportJobFiles.NewJobId());
            File.WriteAllBytes(files.StagingOutputPath, [1, 2, 3, 4]);

            string published;
            using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
                published = files.PublishStagedOutput();

            Assert.AreEqual(Path.Combine(folder, "Locked (2).mp4"), published);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(published));
            CollectionAssert.AreEqual(new byte[] { 9, 9 }, File.ReadAllBytes(destination));
            Assert.IsFalse(File.Exists(files.StagingOutputPath));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ExportJobFiles_SweepStale_RemovesOnlyOldStagingFiles()
    {
        string folder = NewTempFolder();
        try
        {
            string oldStaging = Path.Combine(folder, ".mixtri_out_a_" + new string('a', 32) + ".mp4");
            string oldVideo = Path.Combine(folder, ".mixtri_video_a_" + new string('a', 32) + ".mp4");
            string freshStaging = Path.Combine(folder, ".mixtri_out_b_" + new string('b', 32) + ".mp4");
            string userFile = Path.Combine(folder, "keep.mp4");
            foreach (var path in new[] { oldStaging, oldVideo, freshStaging, userFile })
                File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(oldStaging, DateTime.UtcNow.AddHours(-10));
            File.SetLastWriteTimeUtc(oldVideo, DateTime.UtcNow.AddHours(-10));
            File.SetLastWriteTimeUtc(userFile, DateTime.UtcNow.AddHours(-10));

            int removed = ExportJobFiles.SweepStale(folder, TimeSpan.FromHours(6));

            Assert.AreEqual(2, removed);
            Assert.IsFalse(File.Exists(oldStaging));
            Assert.IsFalse(File.Exists(oldVideo));
            Assert.IsTrue(File.Exists(freshStaging));
            Assert.IsTrue(File.Exists(userFile));
            Assert.AreEqual(0, ExportJobFiles.SweepStale(Path.Combine(folder, "missing"), TimeSpan.Zero));
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

    [TestMethod]
    public void JobFiles_LongDestinationStem_KeepsEveryNameWithinComponentLimit()
    {
        string folder = Path.GetTempPath();
        string stem = new string('a', 220);
        string jobId = ExportJobFiles.NewJobId();

        var files = ExportJobFiles.For(Path.Combine(folder, stem + ".mp4"), jobId);
        var again = ExportJobFiles.For(Path.Combine(folder, stem + ".mp4"), jobId);

        foreach (var path in new[] { files.StagingOutputPath, files.TempVideoPath })
            Assert.IsTrue(Path.GetFileName(path).Length <= ExportJobFiles.MaxFileNameComponent, path);

        // Deterministic: worker and client cleanup must derive the same paths.
        Assert.AreEqual(files.StagingOutputPath, again.StagingOutputPath);
        Assert.AreEqual(files.TempVideoPath, again.TempVideoPath);
        // The destination itself is untouched.
        Assert.AreEqual(Path.GetFullPath(Path.Combine(folder, stem + ".mp4")), files.OutputPath);
    }

    [TestMethod]
    public void ExportJobFiles_FallbackStem_LeavesRoomForCounterNearNameLimit()
    {
        string stem = new string('a', 251); // 251 + ".mp4" = 255: a valid destination name
        string bounded = ExportJobFiles.BoundFallbackStem(stem, ".mp4");
        string candidate = $"{bounded} (1000).mp4";
        Assert.IsTrue(candidate.Length <= ExportJobFiles.MaxFileNameComponent,
            $"fallback name is {candidate.Length} characters");
        Assert.AreEqual("short", ExportJobFiles.BoundFallbackStem("short", ".mp4"));
    }
}
