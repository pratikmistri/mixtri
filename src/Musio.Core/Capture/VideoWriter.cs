using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Musio.Core.Capture;

/// <summary>
/// Captures Direct3D frames to JPEG images during recording, then assembles
/// them into a valid MP4 file in <see cref="FinalizeAsync"/>.
/// </summary>
public sealed class VideoWriter : IDisposable
{
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// <c>CODECAPI_AVEncMPVGOPSize</c>. Bounds how many frames a decoder must rewind and
    /// re-decode to satisfy a seek, which is what makes the finalized MP4 usable as the
    /// editor's scrubbing source.
    /// </summary>
    private static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    /// <summary>
    /// Suffix for the in-progress MP4. The finished file is moved into place only after
    /// finalization fully succeeds, so <c>video.mp4</c> existing always means "complete".
    /// </summary>
    private const string PartialSuffix = ".partial";

    private readonly string _outputPath;
    private readonly string _framesDir;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly CaptureQuality _quality;
    private readonly CanvasDevice _device;
    private readonly Rect? _cropRect;

    private CanvasRenderTarget? _cropTarget;

    private long _frameCount;
    private int _writesInFlight;
    private volatile bool _stopAccepting;
    private bool _finalized;
    private bool _finalizeSucceeded;
    private bool _disposed;

    // Track actual timestamps for each frame to handle variable capture rate
    private readonly List<TimeSpan> _frameTimestamps = new(1000);
    private readonly object _tsLock = new();

    // Serializes all frame writes (WriteFrame + FillGapFrames) so concurrent
    // capture callbacks cannot interleave frame indices.
    private readonly object _writeLock = new();

    public string OutputPath => _outputPath;
    public int Width => _width;
    public int Height => _height;
    public int Fps => _fps;
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>Directory holding the transient captured JPEGs for this recording.</summary>
    public string FramesDirectory => _framesDir;

    /// <summary>
    /// True once <see cref="FinalizeAsync"/> has produced a complete, playable MP4.
    /// Until this flips, the captured JPEGs are the only copy of the recording and must
    /// not be deleted.
    /// </summary>
    public bool FinalizeSucceeded => _finalizeSucceeded;

    /// <summary>Actual recording duration based on frame timestamps (diagnostic only).</summary>
    public TimeSpan ActualDuration
    {
        get
        {
            lock (_tsLock)
            {
                if (_frameTimestamps.Count <= 1)
                    return TimeSpan.Zero;

                // Include the last frame's display time so the duration covers
                // all captured frames, not just the span between first and last.
                var frameDuration = _frameTimestamps.Count >= 2
                    ? _frameTimestamps[^1] - _frameTimestamps[^2]
                    : TimeSpan.FromSeconds(1.0 / _fps);

                return _frameTimestamps[^1] - _frameTimestamps[0] + frameDuration;
            }
        }
    }

    /// <summary>Actual average FPS based on frame timestamps (diagnostic only).</summary>
    public double ActualFps
    {
        get
        {
            var dur = ActualDuration.TotalSeconds;
            return dur > 0 ? (FrameCount - 1) / dur : _fps;
        }
    }

    /// <summary>CFR duration: FrameCount / FPS. Use this for project metadata.</summary>
    public TimeSpan CfrDuration => _fps > 0
        ? TimeSpan.FromSeconds((double)Interlocked.Read(ref _frameCount) / _fps)
        : TimeSpan.Zero;

    public VideoWriter(string outputPath, int width, int height, int fps,
        IDirect3DDevice? captureDevice = null, Rect? cropRect = null,
        CaptureQuality quality = CaptureQuality.HighFidelity)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "FPS must be positive.");

        _outputPath = outputPath;
        _width = width;
        _height = height;
        _fps = fps;
        _quality = quality;
        _cropRect = cropRect;

        // Use the same D3D device as the capture engine to avoid cross-device failures
        if (captureDevice is not null)
            _device = CanvasDevice.CreateFromDirect3D11Device(captureDevice);
        else
            _device = CanvasDevice.GetSharedDevice();

        var dir = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("Output path must include a directory.", nameof(outputPath));
        _framesDir = Path.Combine(dir, VideoFrameReader.FramesDirectoryName);
        Directory.CreateDirectory(_framesDir);
    }

    /// <summary>
    /// Copies the GPU surface to a JPEG file on disk.
    /// Called from the capture engine's thread-pool callback.
    /// </summary>
    public void WriteFrame(IDirect3DSurface surface, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
            throw new InvalidOperationException("Writer has been finalized.");

        if (_stopAccepting)
            return;

        Interlocked.Increment(ref _writesInFlight);
        try
        {
            if (_stopAccepting)
                return;

            lock (_writeLock)
            {
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, surface);

                // Determine the image to save: cropped or full frame
                CanvasBitmap imageToSave;
                if (_cropRect is Rect crop)
                {
                    _cropTarget ??= new CanvasRenderTarget(_device, _width, _height, 96);
                    using (var ds = _cropTarget.CreateDrawingSession())
                    {
                        ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                        ds.DrawImage(bitmap,
                            new Rect(0, 0, _width, _height),
                            crop);
                    }
                    imageToSave = _cropTarget;
                }
                else
                {
                    imageToSave = bitmap;
                }

                long index = Interlocked.Increment(ref _frameCount) - 1;

                lock (_tsLock)
                {
                    _frameTimestamps.Add(timestamp);
                }

                string framePath = Path.Combine(_framesDir, $"frame_{index:D8}.jpg");

                using var stream = new FileStream(framePath, FileMode.Create, FileAccess.Write);
                imageToSave.SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, 0.85f)
                      .AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            // Log frame write failures prominently for debugging
            Debug.WriteLine($"[VideoWriter] ERROR frame {Interlocked.Read(ref _frameCount)}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _writesInFlight);
        }
    }

    public void StopAcceptingFrames()
    {
        _stopAccepting = true;
    }

    public async Task WaitForQuiescenceAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _writesInFlight) != 0)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed >= timeout)
                throw new TimeoutException($"Timed out waiting {timeout} for frame writes to finish.");

            await Task.Delay(10, ct).ConfigureAwait(false);
        }

        lock (_writeLock)
        {
            // Drain any queued FillGapFrames call, which also uses this lock.
        }
    }

    /// <summary>
    /// Fills missed frame slots by duplicating the most recently written JPEG.
    /// This preserves true CFR timing so frame N always corresponds to
    /// wall-clock time N/fps after the first frame. Must be called BEFORE
    /// <see cref="WriteFrame"/> for the current frame.
    /// </summary>
    public void FillGapFrames(int count)
    {
        if (count <= 0) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_writeLock)
        {
            long prevIndex = Interlocked.Read(ref _frameCount) - 1;
            if (prevIndex < 0) return;

            string srcPath = Path.Combine(_framesDir, $"frame_{prevIndex:D8}.jpg");
            if (!File.Exists(srcPath)) return;

            for (int i = 0; i < count; i++)
            {
                long gapIndex = Interlocked.Increment(ref _frameCount) - 1;
                string dstPath = Path.Combine(_framesDir, $"frame_{gapIndex:D8}.jpg");

                try
                {
                    File.Copy(srcPath, dstPath, overwrite: true);

                    // Synthetic timestamp: interpolate between previous and next slot
                    lock (_tsLock)
                    {
                        var lastTs = _frameTimestamps.Count > 0
                            ? _frameTimestamps[^1]
                            : TimeSpan.Zero;
                        _frameTimestamps.Add(lastTs + TimeSpan.FromSeconds(1.0 / _fps));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[VideoWriter] Gap fill failed at index {gapIndex}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Assembles all captured JPEG frames into an MP4 file by streaming BGRA8 samples to H.264.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
            return;

        _finalized = true;

        long totalFrames = Interlocked.Read(ref _frameCount);
        if (totalFrames == 0)
            return;

        // Log diagnostic info to file for debugging
        string logPath = Path.Combine(Path.GetDirectoryName(_outputPath)!, "finalize_debug.log");
        try
        {
            File.WriteAllText(logPath,
                $"Width={_width}, Height={_height}, FPS={_fps}, TotalFrames={totalFrames}\n" +
                $"CropRect={_cropRect}\n" +
                $"OutputPath={_outputPath}\n");
        }
        catch { /* best effort */ }

        // H.264 requires even dimensions
        uint profileWidth = (uint)(_width & ~1);
        uint profileHeight = (uint)(_height & ~1);
        if (profileWidth < 2) profileWidth = 2;
        if (profileHeight < 2) profileHeight = 2;

        // Use constant frame duration for true CFR output.
        // The slot-based capture throttling ensures frames arrive at ~1/fps intervals,
        // so constant duration matches real wall-clock time.
        var constantDuration = TimeSpan.FromSeconds(1.0 / _fps);

        try
        {
            File.AppendAllText(logPath,
                $"Profile: {profileWidth}x{profileHeight}, Frames={totalFrames}\n");
        }
        catch { }

        var videoProps = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8, profileWidth, profileHeight);
        videoProps.FrameRate.Numerator = (uint)_fps;
        videoProps.FrameRate.Denominator = 1;

        var videoDesc = new VideoStreamDescriptor(videoProps);
        var streamSource = new MediaStreamSource(videoDesc)
        {
            Duration = TimeSpan.FromSeconds((double)totalFrames / _fps),
            BufferTime = TimeSpan.Zero,
        };

        streamSource.Starting += (MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
        {
            args.Request.SetActualStartPosition(TimeSpan.Zero);
        };

        long currentFrame = -1;
        var pendingSamples = new List<Task>();
        var pendingSamplesLock = new object();
        Exception? firstFrameError = null;
        long firstFrameErrorIndex = -1;
        var frameErrorLock = new object();

        using var finalizeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        streamSource.SampleRequested += (MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args) =>
        {
            long frame = Interlocked.Increment(ref currentFrame);
            if (frame >= totalFrames)
            {
                args.Request.Sample = null;
                return;
            }

            var deferral = args.Request.GetDeferral();
            var task = ProduceFrameSampleAsync(
                args.Request, deferral, frame, (int)profileWidth, (int)profileHeight,
                constantDuration, finalizeCts.Token,
                onError: (ex, frameIdx) =>
                {
                    lock (frameErrorLock)
                    {
                        if (firstFrameError is null)
                        {
                            firstFrameError = ex;
                            firstFrameErrorIndex = frameIdx;
                        }
                    }
                });

            lock (pendingSamplesLock)
            {
                pendingSamples.RemoveAll(t => t.IsCompleted);
                pendingSamples.Add(task);
            }
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Video ??= new VideoEncodingProperties();
        profile.Video.Subtype = "H264";
        profile.Video.Width = profileWidth;
        profile.Video.Height = profileHeight;
        profile.Video.FrameRate.Numerator = (uint)_fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = ComputeCaptureBitrate(profileWidth, profileHeight);
        profile.Audio = null;

        // Cap the GOP at one second so scrubbing the finalized MP4 in the editor never
        // rewinds more than `fps` frames. Best effort — some encoders ignore the hint.
        try
        {
            profile.Video.Properties[AVEncMPVGOPSize] = (uint)_fps;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWriter] Could not set GOP size: {ex.Message}");
        }

        // Transcode into a sibling temp file and move it into place only after every
        // check below has passed. `video.mp4` existing must imply a complete, playable
        // recording: startup cleanup treats its presence as permission to delete the
        // captured JPEGs, and a half-written file from a timeout, a frame error or a
        // process kill would otherwise destroy the only copy of the recording.
        string dir = Path.GetDirectoryName(_outputPath)!;
        var partialPath = _outputPath + PartialSuffix;
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(dir));
        var outputFile = await folder.CreateFileAsync(
            Path.GetFileName(partialPath), CreationCollisionOption.ReplaceExisting);

        try
        {
            using (var outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                var transcoder = new MediaTranscoder
                {
                    HardwareAccelerationEnabled = false,
                };

                var prepResult = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                    streamSource, outputStream, profile);
                if (!prepResult.CanTranscode)
                    throw new InvalidOperationException($"Transcoder cannot encode: {prepResult.FailureReason}");

                var transcodeOp = prepResult.TranscodeAsync();
                using var cancelRegistration = finalizeCts.Token.Register(() => transcodeOp.Cancel());
                var transcodeTask = transcodeOp.AsTask(finalizeCts.Token);
                var timeoutTask = Task.Delay(FinalizeTimeout);

                if (await Task.WhenAny(transcodeTask, timeoutTask).ConfigureAwait(false) != transcodeTask)
                {
                    finalizeCts.Cancel();
                    try { await transcodeTask.ConfigureAwait(false); }
                    catch { /* timeout is reported below */ }
                    throw new TimeoutException($"MP4 finalization timed out after {FinalizeTimeout}.");
                }

                await transcodeTask.ConfigureAwait(false);

                Task[] snapshot;
                lock (pendingSamplesLock)
                {
                    snapshot = pendingSamples.ToArray();
                }
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }

            Exception? capturedError;
            long capturedIndex;
            lock (frameErrorLock)
            {
                capturedError = firstFrameError;
                capturedIndex = firstFrameErrorIndex;
            }
            if (capturedError is not null)
            {
                try { File.AppendAllText(logPath, $"Frame {capturedIndex} FAILED: {capturedError}\n"); }
                catch { }

                throw new InvalidOperationException(
                    $"MP4 finalization failed while decoding frame {capturedIndex}: {capturedError.Message}",
                    capturedError);
            }

            if (new FileInfo(partialPath).Length == 0)
                throw new InvalidOperationException("MP4 finalization produced an empty file.");

            File.Move(partialPath, _outputPath, overwrite: true);
        }
        catch
        {
            // Leave the captured JPEGs untouched; they are now the only copy.
            try { File.Delete(partialPath); }
            catch (Exception ex) { Debug.WriteLine($"[VideoWriter] Could not remove partial MP4: {ex.Message}"); }
            throw;
        }

        // The MP4 is now the durable master for this recording, so the captured JPEGs
        // have served their purpose as a write-ahead buffer.
        _finalizeSucceeded = true;
    }

    /// <summary>
    /// Computes the recording bitrate for the configured <see cref="CaptureQuality"/>,
    /// scaled by pixel count so high-resolution captures are not starved.
    /// </summary>
    /// <remarks>
    /// Base rates are quoted at 1080p. Even the lowest is a large multiple of what H.264
    /// needs for typical screen content, because this file gets composited and re-encoded
    /// on export and should not contribute visible generation loss.
    /// </remarks>
    internal static uint ComputeCaptureBitrate(uint width, uint height, CaptureQuality quality)
    {
        uint baseBitrate = quality switch
        {
            CaptureQuality.Balanced => 12_000_000,
            CaptureQuality.Master => 60_000_000,
            _ => 30_000_000,
        };

        const double ReferencePixels = 1920.0 * 1080.0;
        double scale = width * (double)height / ReferencePixels;
        scale = Math.Clamp(scale, 0.5, 8.0);

        return (uint)(baseBitrate * scale);
    }

    private uint ComputeCaptureBitrate(uint width, uint height)
        => ComputeCaptureBitrate(width, height, _quality);

    /// <summary>
    /// Deletes the transient captured-JPEG directory. Refuses to run until
    /// <see cref="FinalizeSucceeded"/> is true, because before that the JPEGs are the only
    /// copy of the recording.
    /// </summary>
    /// <returns>Bytes reclaimed, or 0 if nothing was deleted.</returns>
    public long DeleteCapturedFrames()
    {
        if (!_finalizeSucceeded || !Directory.Exists(_framesDir))
            return 0;

        try
        {
            long size = new DirectoryInfo(_framesDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);

            Directory.Delete(_framesDir, recursive: true);
            Debug.WriteLine($"[VideoWriter] Released captured frames ({size / (1024.0 * 1024.0):F1} MB).");
            return size;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWriter] Could not delete captured frames: {ex.Message}");
            return 0;
        }
    }

    private async Task ProduceFrameSampleAsync(
        MediaStreamSourceSampleRequest request,
        MediaStreamSourceSampleRequestDeferral deferral,
        long frameIndex,
        int width,
        int height,
        TimeSpan frameDuration,
        CancellationToken ct,
        Action<Exception, long>? onError)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            string framePath = Path.Combine(_framesDir, $"frame_{frameIndex:D8}.jpg");
            if (!File.Exists(framePath))
                throw new FileNotFoundException("Frame JPEG not found.", framePath);

            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(framePath));
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)width,
                ScaledHeight = (uint)height,
                InterpolationMode = BitmapInterpolationMode.Fant,

                // Media Foundation treats an uncompressed RGB media type with a positive
                // stride as BOTTOM-UP, and `MediaStreamSample.CreateFromBuffer` hands it a
                // raw buffer with no orientation metadata. The captured JPEGs are top-down,
                // so without this the encoded MP4 comes out vertically mirrored.
                // The export pipeline sidesteps the whole issue by passing a D3D surface
                // (`MediaStreamSample.CreateFromDirect3D11Surface` in VideoEncoder), which
                // carries its own orientation — this path cannot, so it flips explicitly.
                Flip = BitmapFlip.Vertical,
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var buffer = new Windows.Storage.Streams.Buffer((uint)((long)width * height * 4));
            bitmap.CopyToBuffer(buffer);

            var timestamp = TimeSpan.FromSeconds((double)frameIndex / _fps);
            var sample = MediaStreamSample.CreateFromBuffer(buffer, timestamp);
            sample.Duration = frameDuration;
            request.Sample = sample;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWriter] Finalize frame {frameIndex} error: {ex}");
            onError?.Invoke(ex, frameIndex);
            request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cropTarget?.Dispose();
        _cropTarget = null;
    }
}
