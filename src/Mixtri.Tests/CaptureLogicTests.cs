using Mixtri.Core.Capture;
using Mixtri.Core.Services;
using Mixtri.Tests.TestSupport;
using NAudio.Wave;

namespace Mixtri.Tests;

/// <summary>
/// Pure-logic guards for the capture hardening: audio wall-clock alignment, letterboxing of
/// reshaped frames, the scaled finalize watchdog, password-field key redaction, the free
/// disk-space gate and discovery of recoverable (unfinalized) sessions.
/// </summary>
[TestClass]
public sealed class CaptureLogicTests
{
    private const int SampleRate = 48_000;
    private const long Freq = 10_000_000; // ticks per second, like Stopwatch on most hosts

    private static long Ticks(double seconds) => (long)Math.Round(seconds * Freq);

    // ── AudioClockAligner ──────────────────────────────────────────

    [TestMethod]
    public void Aligner_FirstBuffer_NeedsNoPadding()
    {
        var aligner = new AudioClockAligner(SampleRate, Freq);
        long origin = Ticks(10);

        // The first buffer is what defines the origin, so it arrives "at" it.
        long pad = aligner.ComputeLeadingSilence(origin, origin, incomingFrames: 480);

        Assert.AreEqual(0, pad);
    }

    [TestMethod]
    public void Aligner_BufferAfterSilentGap_IsPaddedToWallClock()
    {
        var aligner = new AudioClockAligner(SampleRate, Freq);
        long origin = Ticks(10);

        aligner.Commit(0, 480, origin); // 10 ms of audio at t=0

        // Loopback went quiet for ~3 s; the next 10 ms buffer arrives at t=3.01 s.
        long now = origin + Ticks(3.01);
        long pad = aligner.ComputeLeadingSilence(origin, now, incomingFrames: 480);

        // Expected end = 3.01 s = 144480 frames; written 480 + incoming 480.
        Assert.AreEqual(144_480 - 480 - 480, pad);

        aligner.Commit(pad, 480, now);
        Assert.AreEqual(144_480, aligner.FramesWritten, "after padding the stream ends on the wall clock");
    }

    [TestMethod]
    public void Aligner_BackToBackBuffers_AreNeverPadded()
    {
        var aligner = new AudioClockAligner(SampleRate, Freq);
        long origin = Ticks(1);
        aligner.Commit(0, 480, origin);

        // Buffers 10 ms apart but each one "short" — device clock slower than the counter.
        // That drift must not inject drop-outs into continuous audio.
        long now = origin;
        for (int i = 0; i < 100; i++)
        {
            now += Ticks(0.010);
            long pad = aligner.ComputeLeadingSilence(origin, now, incomingFrames: 470);
            Assert.AreEqual(0, pad, $"buffer {i} must not be padded");
            aligner.Commit(pad, 470, now);
        }
    }

    [TestMethod]
    public void Aligner_StreamThatStartsLate_GetsLeadingSilence()
    {
        // The other stream defined the origin 2 s ago; this one delivers its first buffer now.
        var aligner = new AudioClockAligner(SampleRate, Freq);
        long origin = Ticks(5);
        long now = origin + Ticks(2);

        long pad = aligner.ComputeLeadingSilence(origin, now, incomingFrames: 480);

        Assert.AreEqual(96_000 - 480, pad);
    }

    [TestMethod]
    public void Aligner_TrailingSilence_ReachesStopTime()
    {
        var aligner = new AudioClockAligner(SampleRate, Freq);
        long origin = Ticks(1);
        aligner.Commit(0, 48_000, origin + Ticks(1)); // 1 s of audio

        long pad = aligner.ComputeTrailingSilence(origin, origin + Ticks(4));

        Assert.AreEqual(3 * 48_000, pad);
    }

    [TestMethod]
    public void Aligner_UnknownOrigin_NeverPads()
    {
        var aligner = new AudioClockAligner(SampleRate, Freq);

        Assert.AreEqual(0, aligner.ComputeLeadingSilence(0, Ticks(100), 480));
        Assert.AreEqual(0, aligner.ComputeTrailingSilence(0, Ticks(100)));
    }

    [TestMethod]
    public void Aligner_AbsurdGap_IsTreatedAsClockErrorAndSkipped()
    {
        var aligner = new AudioClockAligner(SampleRate, Freq);
        long origin = Ticks(1);

        long pad = aligner.ComputeTrailingSilence(
            origin, origin + Ticks(AudioClockAligner.MaxPadSeconds + 60));

        Assert.AreEqual(0, pad);
    }

    // ── Pcm16CaptureWriter.WriteSilence ────────────────────────────

    [TestMethod]
    public void Pcm16Writer_WriteSilence_WritesWholeZeroFrames()
    {
        using var temp = new TempDirectoryFixture("mixtri_capture_silence_");
        var path = Path.Combine(temp.Path, "silence.wav");

        // Float source → 16-bit stereo output: 4 bytes per frame.
        using (var writer = new Pcm16CaptureWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)))
        {
            writer.WriteSilence(100_000); // spans several internal chunks
        }

        using var reader = new WaveFileReader(path);
        Assert.AreEqual(16, reader.WaveFormat.BitsPerSample);
        Assert.AreEqual(100_000L * 4, reader.Length);

        var bytes = new byte[reader.Length];
        int read = reader.Read(bytes, 0, bytes.Length);
        Assert.AreEqual(bytes.Length, read);
        Assert.IsTrue(bytes.All(b => b == 0), "silence must be digital zero");
    }

    // ── Letterboxing reshaped frames ───────────────────────────────

    [TestMethod]
    public void Letterbox_SameSize_FillsFrame()
    {
        Assert.AreEqual((0, 0, 1920, 1080), VideoWriter.ComputeLetterboxRect(1920, 1080, 1920, 1080));
    }

    [TestMethod]
    public void Letterbox_OddSizedSource_IsScaledNotSeamed()
    {
        // The encoder rounds 1001x601 down to 1000x600; a 1px bar would be a visible seam.
        Assert.AreEqual((0, 0, 1000, 600), VideoWriter.ComputeLetterboxRect(1001, 601, 1000, 600));
    }

    [TestMethod]
    public void Letterbox_NarrowerSource_IsPillarboxed()
    {
        // Window resized from 1600x900 to 800x900 mid-recording.
        var rect = VideoWriter.ComputeLetterboxRect(800, 900, 1600, 900);

        Assert.AreEqual((400, 0, 800, 900), rect);
    }

    [TestMethod]
    public void Letterbox_WiderSource_IsLetterboxed()
    {
        var rect = VideoWriter.ComputeLetterboxRect(1600, 400, 800, 800);

        Assert.AreEqual(800, rect.Width);
        Assert.AreEqual(200, rect.Height);
        Assert.AreEqual(0, rect.X);
        Assert.AreEqual(300, rect.Y);
    }

    [TestMethod]
    public void Letterbox_PreservesAspectRatio()
    {
        var rect = VideoWriter.ComputeLetterboxRect(1280, 720, 1000, 1000);

        double sourceAspect = 1280 / 720.0;
        double fittedAspect = rect.Width / (double)rect.Height;
        Assert.AreEqual(sourceAspect, fittedAspect, 0.01);
        Assert.IsTrue(rect.X + rect.Width <= 1000 && rect.Y + rect.Height <= 1000);
    }

    [TestMethod]
    public void ComposeLetterboxed_PlacesImageOnOpaqueBlack()
    {
        // 2x1 white source into a 4x3 canvas at (1,1).
        var source = Enumerable.Repeat((byte)255, 2 * 1 * 4).ToArray();
        var dest = new byte[4 * 3 * 4];

        VideoWriter.ComposeLetterboxed(source, 2, 1, dest, 4, 3, 1, 1);

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 4 + x) * 4;
                bool inside = y == 1 && (x == 1 || x == 2);
                byte expectedColor = inside ? (byte)255 : (byte)0;
                Assert.AreEqual(expectedColor, dest[i], $"B at ({x},{y})");
                Assert.AreEqual(expectedColor, dest[i + 1], $"G at ({x},{y})");
                Assert.AreEqual(expectedColor, dest[i + 2], $"R at ({x},{y})");
                Assert.AreEqual((byte)255, dest[i + 3], $"A at ({x},{y}) must be opaque");
            }
        }
    }

    // ── Finalize watchdog ──────────────────────────────────────────

    [TestMethod]
    public void FinalizeTimeout_ShortRecording_UsesFloor()
    {
        Assert.AreEqual(VideoWriter.MinFinalizeTimeout, VideoWriter.ComputeFinalizeTimeout(0));
        Assert.AreEqual(VideoWriter.MinFinalizeTimeout, VideoWriter.ComputeFinalizeTimeout(300));
    }

    [TestMethod]
    public void FinalizeTimeout_LongRecording_ScalesWithFrames()
    {
        long twoHoursAt60 = 2L * 3600 * 60;

        var timeout = VideoWriter.ComputeFinalizeTimeout(twoHoursAt60);

        Assert.IsTrue(timeout > VideoWriter.MinFinalizeTimeout);
        Assert.AreEqual(
            twoHoursAt60 * VideoWriter.FinalizeBudgetPerFrame.TotalSeconds,
            timeout.TotalSeconds, 1.0);
    }

    [TestMethod]
    public void FinalizeTimeout_HugeFrameCount_StaysUsableByTaskDelay()
    {
        var timeout = VideoWriter.ComputeFinalizeTimeout(long.MaxValue / 2);

        Assert.IsTrue(timeout.TotalMilliseconds < int.MaxValue,
            "Task.Delay/CancelAfter reject delays beyond Int32.MaxValue milliseconds");
    }

    // ── Password-field redaction ───────────────────────────────────

    private static KeyPressEvent Key(int vk, string name, bool shift = false, bool ctrl = false)
        => new(12345, vk, name, IsDown: true, IsCtrl: ctrl, IsAlt: false, IsShift: shift, IsWin: false,
            TextFocus: new TextInputFocus(1, 2, 0, 0, 10, 10));

    [TestMethod]
    public void Redact_CharacterKey_LosesIdentityButKeepsTiming()
    {
        var redacted = KeyboardHookRecorder.RedactSecureKey(Key(0x51, "Q", shift: true));

        Assert.IsNotNull(redacted);
        Assert.AreEqual(KeyboardHookRecorder.RedactedVirtualKeyCode, redacted!.VirtualKeyCode);
        Assert.AreEqual(KeyboardHookRecorder.RedactedKeyName, redacted.KeyName);
        Assert.IsFalse(redacted.IsShift, "Shift would reveal case");
        Assert.AreEqual(12345, redacted.TimestampTicks);
        Assert.IsTrue(redacted.IsDown);
        Assert.IsNotNull(redacted.TextFocus, "caret position is not secret and drives typing zoom");
    }

    [TestMethod]
    public void Redact_DigitsAndPunctuation_AreRedacted()
    {
        foreach (int vk in new[] { 0x30, 0x39, 0x20, 0x60, 0x6B, 0xBA, 0xC0, 0xDB, 0xDE, 0xE2 })
        {
            var redacted = KeyboardHookRecorder.RedactSecureKey(Key(vk, $"VK_{vk:X2}"));
            Assert.AreEqual(KeyboardHookRecorder.RedactedVirtualKeyCode, redacted!.VirtualKeyCode,
                $"VK 0x{vk:X2} must be redacted");
        }
    }

    [TestMethod]
    public void Redact_ShiftPress_IsDropped()
    {
        Assert.IsNull(KeyboardHookRecorder.RedactSecureKey(Key(0xA0, "Shift")));
        Assert.IsNull(KeyboardHookRecorder.RedactSecureKey(Key(0x10, "Shift")));
    }

    [TestMethod]
    public void Redact_NavigationEditingAndShortcuts_AreKept()
    {
        var enter = Key(0x0D, "Enter");
        var backspace = Key(0x08, "Backspace");
        var paste = Key(0x56, "V", ctrl: true);

        Assert.AreEqual(enter, KeyboardHookRecorder.RedactSecureKey(enter));
        Assert.AreEqual(backspace, KeyboardHookRecorder.RedactSecureKey(backspace));
        Assert.AreEqual(paste, KeyboardHookRecorder.RedactSecureKey(paste));
    }

    [TestMethod]
    public void Redact_AltGrCharacterChord_IsRedactedWithModifiersCleared()
    {
        // AltGr reports Ctrl+Alt but types a character (German AltGr+Q = '@').
        var altGrQ = Key(0x51, "Q", ctrl: true) with { IsAlt = true };

        var redacted = KeyboardHookRecorder.RedactSecureKey(altGrQ);

        Assert.IsNotNull(redacted);
        Assert.AreEqual(KeyboardHookRecorder.RedactedVirtualKeyCode, redacted!.VirtualKeyCode);
        Assert.AreEqual(KeyboardHookRecorder.RedactedKeyName, redacted.KeyName);
        Assert.IsFalse(redacted.IsCtrl);
        Assert.IsFalse(redacted.IsAlt);
    }

    [TestMethod]
    public void Redact_AltNumpadCode_IsRedactedWithModifiersCleared()
    {
        foreach (int vk in new[] { 0x60, 0x64, 0x69, 0x23, 0x0C, 0x21 })
        {
            var altCode = Key(vk, $"VK_{vk:X2}") with { IsAlt = true };

            var redacted = KeyboardHookRecorder.RedactSecureKey(altCode);

            Assert.AreEqual(KeyboardHookRecorder.RedactedVirtualKeyCode, redacted!.VirtualKeyCode,
                $"Alt+VK 0x{vk:X2} can enter a character and must be redacted");
            Assert.IsFalse(redacted.IsAlt);
        }

        // Plain navigation without Alt stays visible.
        var home = Key(0x24, "Home");
        Assert.AreEqual(home, KeyboardHookRecorder.RedactSecureKey(home));
    }

    [TestMethod]
    public void EditClassNames_AreRecognised()
    {
        Assert.IsTrue(KeyboardHookRecorder.IsEditClassName("Edit"));
        Assert.IsTrue(KeyboardHookRecorder.IsEditClassName("RICHEDIT50W"));
        Assert.IsTrue(KeyboardHookRecorder.IsEditClassName("RichEdit20W"));
        Assert.IsFalse(KeyboardHookRecorder.IsEditClassName("Button"));
        Assert.IsFalse(KeyboardHookRecorder.IsEditClassName(""));
        Assert.IsFalse(KeyboardHookRecorder.IsEditClassName(null));
    }

    // ── Disk space ─────────────────────────────────────────────────

    [TestMethod]
    public void LowDiskSpace_OnlyWhenKnownAndBelowThreshold()
    {
        Assert.IsTrue(RecordingSession.IsLowOnDiskSpace(RecordingSession.MinFreeDiskBytes - 1));
        Assert.IsFalse(RecordingSession.IsLowOnDiskSpace(RecordingSession.MinFreeDiskBytes));
        Assert.IsFalse(RecordingSession.IsLowOnDiskSpace(null), "unknown free space must not block recording");
    }

    [TestMethod]
    public void FreeSpace_IsReadableForTempFolder()
    {
        var free = RecordingSession.TryGetAvailableFreeSpace(Path.GetTempPath());

        Assert.IsNotNull(free);
        Assert.IsTrue(free > 0);
    }

    [TestMethod]
    public void FreeSpace_BadPath_IsUnknownNotAThrow()
    {
        Assert.IsNull(RecordingSession.TryGetAvailableFreeSpace(""));
    }

    // ── Recoverable sessions ───────────────────────────────────────

    [TestMethod]
    public void FindRecoverableSessions_ListsOnlyFramesWithoutVideo()
    {
        using var temp = new TempDirectoryFixture("mixtri_capture_recover_");
        string frames = Mixtri.Core.Processing.VideoFrameReader.FramesDirectoryName;

        // Crashed before finalize: frames, no MP4 → recoverable.
        var crashed = Path.Combine(temp.Path, "session_20260101_000001");
        Directory.CreateDirectory(Path.Combine(crashed, frames));
        File.WriteAllBytes(Path.Combine(crashed, frames, "frame_00000000.jpg"), [1, 2, 3]);

        // Finalized: MP4 plus completion marker → not recoverable even though frames remain.
        var finished = Path.Combine(temp.Path, "session_20260101_000002");
        Directory.CreateDirectory(Path.Combine(finished, frames));
        File.WriteAllBytes(Path.Combine(finished, frames, "frame_00000000.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(finished, "video.mp4"), [1, 2, 3]);
        File.WriteAllText(
            Path.Combine(finished, VideoWriter.FinalizedMarkerName),
            RecordingMarker.BuildContent(VideoWriter.EncoderVersion));

        // Empty MP4 from an interrupted encode counts as no video.
        var emptyVideo = Path.Combine(temp.Path, "session_20260101_000003");
        Directory.CreateDirectory(Path.Combine(emptyVideo, frames));
        File.WriteAllBytes(Path.Combine(emptyVideo, frames, "frame_00000000.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(emptyVideo, "video.mp4"), []);

        // No frames at all → nothing to recover.
        var noFrames = Path.Combine(temp.Path, "session_20260101_000004");
        Directory.CreateDirectory(noFrames);

        // Not a session folder.
        var other = Path.Combine(temp.Path, "unrelated");
        Directory.CreateDirectory(Path.Combine(other, frames));
        File.WriteAllBytes(Path.Combine(other, frames, "frame_00000000.jpg"), [1, 2, 3]);

        // Interrupted legacy in-place encode: non-empty MP4 but no completion marker. Its
        // header may never have been finalized, so the JPEGs are still the recording.
        var unfinalized = Path.Combine(temp.Path, "session_20260101_000005");
        Directory.CreateDirectory(Path.Combine(unfinalized, frames));
        File.WriteAllBytes(Path.Combine(unfinalized, frames, "frame_00000000.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(unfinalized, "video.mp4"), [1, 2, 3]);

        var found = SessionCleanupService.FindRecoverableSessions(temp.Path);

        CollectionAssert.AreEqual(new[] { crashed, emptyVideo, unfinalized }, found.ToArray());

        // And cleanup must still leave the recoverable frames alone.
        SessionCleanupService.CleanupExportedSessions(temp.Path);
        Assert.IsTrue(File.Exists(Path.Combine(crashed, frames, "frame_00000000.jpg")));
        Assert.IsTrue(File.Exists(Path.Combine(unfinalized, frames, "frame_00000000.jpg")));
    }

    [TestMethod]
    public void FindRecoverableSessions_MissingFolder_IsEmpty()
    {
        var found = SessionCleanupService.FindRecoverableSessions(
            Path.Combine(Path.GetTempPath(), "mixtri_missing_" + Guid.NewGuid().ToString("N")));

        Assert.AreEqual(0, found.Count);
    }
}
