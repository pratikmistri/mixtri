using Mixtri.Core.Capture;
using Mixtri.Tests.TestSupport;

namespace Mixtri.Tests;

[TestClass]
public sealed class CaptureFrameFilesTests
{
    private static byte[] Pixels(int seed)
    {
        var bytes = new byte[65536];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [TestMethod]
    public void HeldFrames_ShareBytesAndRemainIndependentlyNamed()
    {
        using var directory = new TempDirectoryFixture("mixtri_frame_links_");
        var expected = Pixels(1);
        string source = Path.Combine(directory.Path, "frame_00000000.jpg");
        File.WriteAllBytes(source, expected);
        var files = new CaptureFrameFiles();
        for (int index = 1; index <= 20; index++)
        {
            string destination = Path.Combine(directory.Path, $"frame_{index:D8}.jpg");
            files.Duplicate(source, destination);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(destination));
            source = destination;
        }
        Assert.AreEqual(21, Directory.EnumerateFiles(directory.Path, "*.jpg").Count());
        if (new DriveInfo(Path.GetPathRoot(directory.Path)!).DriveFormat == "NTFS")
        {
            Assert.AreEqual(20L, files.LinkedFrames);
            Assert.AreEqual(0L, files.CopiedBytes);
        }
    }

    [TestMethod]
    public void PublishingReplacement_DoesNotMutatePreviouslyLinkedFrames()
    {
        using var directory = new TempDirectoryFixture("mixtri_frame_publish_");
        string source = Path.Combine(directory.Path, "frame_00000000.jpg");
        string held = Path.Combine(directory.Path, "frame_00000001.jpg");
        string temporary = source + ".tmp";
        var original = Pixels(2);
        var replacement = Pixels(3);
        File.WriteAllBytes(source, original);
        new CaptureFrameFiles().Duplicate(source, held);
        File.WriteAllBytes(temporary, replacement);
        CaptureFrameFiles.Publish(temporary, source);
        CollectionAssert.AreEqual(replacement, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(held));
        Assert.IsFalse(File.Exists(temporary));
    }

    [TestMethod]
    public void UnsupportedLinks_FallBackOnceWithoutChangingFrameBytes()
    {
        using var directory = new TempDirectoryFixture("mixtri_frame_fallback_");
        int attempts = 0;
        var files = new CaptureFrameFiles((_, _) => { attempts++; return 50; });
        var bytes = Pixels(4);
        string source = Path.Combine(directory.Path, "source.jpg");
        File.WriteAllBytes(source, bytes);
        for (int index = 0; index < 4; index++)
        {
            string destination = Path.Combine(directory.Path, $"{index}.jpg");
            files.Duplicate(source, destination);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(destination));
            source = destination;
        }
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0L, files.LinkedFrames);
        Assert.AreEqual(4L * bytes.Length, files.CopiedBytes);
    }

    [TestMethod]
    public void LinkLimit_CopiesAnAnchorButDoesNotDisableFutureLinks()
    {
        using var directory = new TempDirectoryFixture("mixtri_frame_limit_");
        int attempts = 0;
        var files = new CaptureFrameFiles((_, _) => { attempts++; return 1142; });
        string source = Path.Combine(directory.Path, "source.jpg");
        File.WriteAllBytes(source, Pixels(5));
        for (int index = 0; index < 3; index++)
        {
            string destination = Path.Combine(directory.Path, $"{index}.jpg");
            files.Duplicate(source, destination);
            source = destination;
        }
        Assert.AreEqual(3, attempts);
        Assert.AreEqual(3L * 65536, files.CopiedBytes);
    }

    [TestMethod]
    public void LongHold_RollsOverNativeLinkLimitWithoutHoles()
    {
        using var directory = new TempDirectoryFixture("mixtri_frame_longhold_");
        if (new DriveInfo(Path.GetPathRoot(directory.Path)!).DriveFormat != "NTFS")
            Assert.Inconclusive("Native link-limit coverage requires NTFS.");
        var files = new CaptureFrameFiles();
        var bytes = Pixels(6);
        string source = Path.Combine(directory.Path, "frame_00000000.jpg");
        File.WriteAllBytes(source, bytes);
        const int heldFrames = 1050;
        for (int index = 1; index <= heldFrames; index++)
        {
            string destination = Path.Combine(directory.Path, $"frame_{index:D8}.jpg");
            files.Duplicate(source, destination);
            source = destination;
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        Assert.AreEqual(heldFrames + 1, Directory.EnumerateFiles(directory.Path).Count());
        Assert.IsTrue(files.LinkedFrames >= heldFrames - 2);
        Assert.IsTrue(files.CopiedBytes <= 2L * bytes.Length);
    }

    [TestMethod]
    public void DuplicateSamePath_IsRejectedWithoutDeletingTheSource()
    {
        using var directory = new TempDirectoryFixture("mixtri_frame_same_");
        string source = Path.Combine(directory.Path, "source.jpg");
        var bytes = Pixels(7);
        File.WriteAllBytes(source, bytes);
        Assert.ThrowsException<ArgumentException>(() => new CaptureFrameFiles().Duplicate(source, source));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
    }
}
