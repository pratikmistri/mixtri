using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;

namespace Musio.Core.Audio;

/// <summary>
/// Renders the slice of a recording that sits under a speed-adjusted segment into a
/// time-stretched WAV of exactly the duration that segment occupies on the output timeline.
/// </summary>
/// <remarks>
/// <para>
/// This is the I/O half of the segment time-stretch feature: it reads the source range with
/// NAudio, pushes it through the (pure) <see cref="WsolaTimeStretcher"/>, and writes PCM16.
/// Both the exporter and the editor preview render through this class, keyed identically, so
/// what the user hears while scrubbing is literally the same file the mux pass consumes.
/// </para>
/// <para>
/// <b>Everything is cached and content-addressed.</b> The key covers the source path, its
/// last-write time and length, the source interval, the speed and the requested output
/// duration — so re-exporting, reopening the project, or previewing the same segment reuses
/// the rendered file, while editing the segment's speed or trim misses the cache and renders
/// a new one. Stale entries are simply never referenced again; they live beside the
/// recording and are removed with it.
/// </para>
/// <para>
/// <b>Every failure is soft.</b> An unreadable, missing or exotic source returns
/// <c>null</c> rather than throwing, and callers fall back to placing the audio at its
/// native rate — the pre-existing behaviour. Losing the stretch must never lose the export.
/// </para>
/// </remarks>
public sealed class SegmentAudioRenderer
{
    /// <summary>Folder (beside the source recording, or under an explicit root) holding rendered WAVs.</summary>
    public const string CacheFolderName = ".stretched";

    /// <summary>Samples pulled from the decoder per iteration (~0.5s of 48kHz stereo).</summary>
    private const int ReadBlockSamples = 48000;

    private readonly string? _cacheRoot;

    /// <param name="cacheRoot">
    /// Where rendered files are written. When null (the normal case) each render caches
    /// beside its own source recording, so it is cleaned up with that recording's folder.
    /// </param>
    public SegmentAudioRenderer(string? cacheRoot = null) => _cacheRoot = cacheRoot;

    /// <summary>
    /// Renders — or returns the cached — stretched audio for one segment's source range.
    /// </summary>
    /// <param name="sourcePath">Recording to read (WAV, or anything NAudio can open).</param>
    /// <param name="sourceStart">Where the segment's audio begins inside that file.</param>
    /// <param name="sourceDuration">How much source audio the segment covers.</param>
    /// <param name="speed">The segment's speed factor; &gt;1 shortens, &lt;1 lengthens.</param>
    /// <param name="outputDuration">
    /// Exact length the result must have — the span the segment occupies on the output
    /// timeline. The stretcher lands within a few tens of milliseconds of this on its own
    /// (see <see cref="WsolaTimeStretcher.Flush"/>); the last scrap is trimmed or padded here
    /// so the caller can place the file without re-checking its length.
    /// </param>
    /// <returns>Path to the rendered WAV, or <c>null</c> if it could not be produced.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled. Deliberately NOT folded into the
    /// <c>null</c> "could not render" result: a cancelled export must stop, not quietly fall
    /// back to native-rate audio and carry on muxing.
    /// </exception>
    public string? Render(
        string sourcePath,
        TimeSpan sourceStart,
        TimeSpan sourceDuration,
        double speed,
        TimeSpan outputDuration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        if (sourceDuration <= TimeSpan.Zero || outputDuration <= TimeSpan.Zero) return null;
        if (!WsolaTimeStretcher.IsStretchNeeded(speed)) return null;

        try
        {
            var source = new FileInfo(sourcePath);
            if (!source.Exists || source.Length == 0) return null;

            string cacheDirectory = _cacheRoot
                ?? Path.Combine(source.DirectoryName ?? Path.GetTempPath(), CacheFolderName);
            string destination = Path.Combine(
                cacheDirectory,
                BuildCacheFileName(source, sourceStart, sourceDuration, speed, outputDuration));

            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                return destination;

            Directory.CreateDirectory(cacheDirectory);

            // Render to a scratch name and move into place, so a cancelled or crashed
            // render can never leave a half-written file that the next call would treat as
            // a valid cache hit.
            string scratch = Path.Combine(cacheDirectory, $".{Guid.NewGuid():N}.tmp.wav");
            try
            {
                RenderCore(sourcePath, sourceStart, sourceDuration, speed, outputDuration, scratch, cancellationToken);
                File.Move(scratch, destination, overwrite: true);
                return destination;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(scratch);

                // Losing the race is not a failure. The editor preview and an export can ask
                // for the same key at the same time; whoever finishes first publishes the
                // file and then holds it open through an AudioFileReader, which makes the
                // other's move fail with a sharing violation. The destination is already
                // exactly what this call was about to produce, so use it.
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                    return destination;

                throw;
            }
            catch
            {
                TryDelete(scratch);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.DiagLog.Write("Audio",
                $"segment audio stretch failed for '{Path.GetFileName(sourcePath)}' " +
                $"@{speed:0.###}x: {ex.Message}");
            return null;
        }
    }

    /// <summary><see cref="Render"/> on a background thread.</summary>
    public Task<string?> RenderAsync(
        string sourcePath,
        TimeSpan sourceStart,
        TimeSpan sourceDuration,
        double speed,
        TimeSpan outputDuration,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => Render(sourcePath, sourceStart, sourceDuration, speed, outputDuration, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Deterministic cache file name. Every input that changes a single output sample is in
    /// the key — including the source's last-write time and length, so re-recording over the
    /// same path invalidates it.
    /// </summary>
    private static string BuildCacheFileName(
        FileInfo source, TimeSpan sourceStart, TimeSpan sourceDuration, double speed, TimeSpan outputDuration)
    {
        string material = string.Join(
            '|',
            source.FullName.ToLowerInvariant(),
            source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            source.Length.ToString(CultureInfo.InvariantCulture),
            sourceStart.Ticks.ToString(CultureInfo.InvariantCulture),
            sourceDuration.Ticks.ToString(CultureInfo.InvariantCulture),
            speed.ToString("R", CultureInfo.InvariantCulture),
            outputDuration.Ticks.ToString(CultureInfo.InvariantCulture));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"stretch_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}.wav";
    }

    private static void RenderCore(
        string sourcePath,
        TimeSpan sourceStart,
        TimeSpan sourceDuration,
        double speed,
        TimeSpan outputDuration,
        string destination,
        CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(sourcePath);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;

        if (sourceStart > TimeSpan.Zero)
            reader.CurrentTime = sourceStart;

        long remainingSamples = (long)Math.Round(sourceDuration.TotalSeconds * sampleRate) * channels;
        long targetSamples = (long)Math.Round(outputDuration.TotalSeconds * sampleRate) * channels;

        var stretcher = new WsolaTimeStretcher(sampleRate, channels, speed);
        using var writer = new WaveFileWriter(destination, new WaveFormat(sampleRate, 16, channels));

        long written = 0;
        var scratch = new float[Math.Max(ReadBlockSamples, channels)];

        void Write(ReadOnlySpan<float> block)
        {
            if (written >= targetSamples) return;

            int count = (int)Math.Min(block.Length, targetSamples - written);
            int index = 0;
            while (index < count)
            {
                int chunk = Math.Min(scratch.Length, count - index);
                for (int i = 0; i < chunk; i++)
                {
                    // PCM16 conversion multiplies by short.MaxValue and casts; an
                    // out-of-range sample would wrap to the opposite rail and click loudly.
                    scratch[i] = Math.Clamp(block[index + i], -1f, 1f);
                }

                writer.WriteSamples(scratch, 0, chunk);
                index += chunk;
            }

            written += count;
        }

        var input = new float[Math.Max(ReadBlockSamples, channels)];
        while (remainingSamples > 0 && written < targetSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int want = (int)Math.Min(input.Length, remainingSamples);
            want -= want % channels;
            if (want <= 0) break;

            int read = reader.Read(input, 0, want);
            if (read <= 0) break;

            read -= read % channels;
            if (read <= 0) break;

            remainingSamples -= read;
            stretcher.Process(input.AsSpan(0, read), Write);
        }

        stretcher.Flush(Write);

        // Pad the remainder. The stretcher can land a few tens of milliseconds short at the
        // very end when slowing down (its final scrap is shorter than one grain), and the
        // source itself can run out early — a segment whose recording stops mid-way. Either
        // way the placement's duration is already fixed, so the file has to fill it.
        if (written < targetSamples)
        {
            var silence = new float[Math.Min(targetSamples - written, ReadBlockSamples)];
            while (written < targetSamples)
            {
                int count = (int)Math.Min(silence.Length, targetSamples - written);
                writer.WriteSamples(silence, 0, count);
                written += count;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort: a leftover scratch file is inert (it is never a cache hit).
        }
    }
}
