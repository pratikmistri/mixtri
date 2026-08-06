using System.Diagnostics;
using System.Threading.Channels;
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
/// Details of the first frame that could not be persisted during recording.
/// </summary>
/// <remarks>
/// Raised from the writer's background thread — handlers must be free-threaded.
/// </remarks>
public sealed class FrameWriteFailedEventArgs(Exception exception, long frameIndex, string framesDirectory)
    : EventArgs
{
    /// <summary>The failure that stopped the frame from reaching disk.</summary>
    public Exception Exception { get; } = exception;

    /// <summary>Index the frame would have occupied, or -1 if it failed before it had one.</summary>
    public long FrameIndex { get; } = frameIndex;

    /// <summary>Directory holding the frames that <em>were</em> captured, for user-facing recovery.</summary>
    public string FramesDirectory { get; } = framesDirectory;
}

/// <summary>
/// Captures Direct3D frames to JPEG images during recording, then assembles
/// them into a valid MP4 file in <see cref="FinalizeAsync"/>.
/// </summary>
/// <remarks>
/// Frames are accepted on the capture engine's free-threaded frame-pool callback, which
/// must never block on disk or on JPEG encoding. <see cref="TryWriteFrame"/> therefore only
/// copies the capture surface into an independently owned render target and hands it to a
/// bounded queue; a single background consumer does the encode and the write. Dropping is
/// explicit and never loses wall-clock time: a dropped frame becomes an owed CFR slot that
/// the next persisted frame fills with a duplicate.
/// </remarks>
public sealed class VideoWriter : IDisposable
{
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How long <see cref="FinalizeAsync"/> waits for a still-running writer to drain.</summary>
    private static readonly TimeSpan FinalizeDrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Grace period given to the writer loop to observe cancellation before it is abandoned.</summary>
    private static readonly TimeSpan WriterShutdownGrace = TimeSpan.FromSeconds(5);

    private const float JpegQuality = 0.85f;

    /// <summary>
    /// Upper bound on GPU memory held by queued frames. The queue exists to absorb disk and
    /// encoder jitter, not to buffer the recording, so it is deliberately shallow.
    /// </summary>
    private const long MaxQueuedFrameBytes = 192L * 1024 * 1024;

    private const int MinQueueCapacity = 2;
    private const int MaxQueueCapacity = 8;

    /// <summary>
    /// After this many consecutive write failures the writer stops accepting frames: a full
    /// disk or a lost device never recovers, and every further frame would burn GPU time to
    /// produce nothing. The retained failure is still reported.
    /// </summary>
    private const int MaxConsecutiveWriteFailures = 30;

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

    /// <summary>
    /// Marker file written beside <c>video.mp4</c> once finalization fully succeeds.
    /// Its presence is what makes the captured JPEGs safe to delete.
    /// </summary>
    public const string FinalizedMarkerName = "finalized.marker";

    /// <summary>
    /// Version of the recording encoder. Bumped when output changes in a way that makes
    /// earlier files unusable as the editable master — version 2 corrected the vertical
    /// flip that every earlier build produced.
    /// </summary>
    public const int EncoderVersion = 2;

    private readonly string _outputPath;
    private readonly string _framesDir;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly CaptureQuality _quality;
    private readonly CanvasDevice _device;
    private readonly Rect? _cropRect;

    private long _frameCount;
    private volatile bool _stopAccepting;
    private bool _finalized;
    private bool _finalizeSucceeded;
    private volatile bool _disposed;

    // Track actual timestamps for each frame to handle variable capture rate
    private readonly List<TimeSpan> _frameTimestamps = new(1000);
    private readonly object _tsLock = new();

    // ── Bounded producer/consumer ───────────────────────────────────
    // Producer: the free-threaded capture callback (copy + enqueue only).
    // Consumer: a single background loop that owns all frame indices, JPEG
    // encoding, gap filling and disk writes, so ordering needs no lock.

    private readonly Channel<PendingFrame> _frameQueue;
    private readonly CancellationTokenSource _writerCts = new();
    private readonly Task _writerLoop;
    private readonly int _queueCapacity;
    private int _queueDepth;
    private int _writerCompleted;

    private long _droppedFrames;
    private long _failedWrites;
    private int _consecutiveFailures;

    /// <summary>CFR slots owed by dropped frames, applied to the next queued frame.</summary>
    private int _pendingSkippedSlots;

    private Exception? _firstWriteError;
    private long _firstWriteErrorIndex = -1;
    private readonly object _errorLock = new();

    // Render targets are recycled between frames to avoid churning VRAM at 60 FPS.
    // Ownership is strictly single-threaded: a target is either idle in the pool, owned
    // by the producer that is filling it, or owned by the consumer that is encoding it.
    private readonly Stack<CanvasRenderTarget> _targetPool = new();
    private readonly object _poolLock = new();
    private int _poolWidth;
    private int _poolHeight;

    /// <summary>A captured frame waiting to be encoded. A null bitmap is a gap-only marker.</summary>
    private readonly record struct PendingFrame(
        CanvasRenderTarget? Bitmap, TimeSpan Timestamp, int SkippedSlots);

    public string OutputPath => _outputPath;
    public int Width => _width;
    public int Height => _height;
    public int Fps => _fps;

    /// <summary>
    /// Frames durably on disk. A frame is only counted once its JPEG exists, so
    /// <c>frame_{i}.jpg</c> is guaranteed present for every <c>i &lt; FrameCount</c>.
    /// </summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>Maximum number of frames that may be queued for encoding at once.</summary>
    public int QueueCapacity => _queueCapacity;

    /// <summary>Frames currently queued or being encoded (diagnostic).</summary>
    public int QueuedFrames => Volatile.Read(ref _queueDepth);

    /// <summary>
    /// Frames refused because the writer queue was saturated. Each one is replayed as a
    /// duplicate of the previous frame so CFR timing still matches wall clock.
    /// </summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>Frames that reached the writer but could not be persisted.</summary>
    public long FailedFrameWrites => Interlocked.Read(ref _failedWrites);

    /// <summary>
    /// The first failure that prevented a frame from reaching disk, retained so the session
    /// can report it instead of discovering it as a missing JPEG at finalization time.
    /// </summary>
    public Exception? FirstWriteError
    {
        get { lock (_errorLock) { return _firstWriteError; } }
    }

    /// <summary>Index of the frame that hit <see cref="FirstWriteError"/>, or -1.</summary>
    public long FirstWriteErrorFrameIndex
    {
        get { lock (_errorLock) { return _firstWriteErrorIndex; } }
    }

    public bool HasWriteFailure => FirstWriteError is not null;

    /// <summary>Raised once, on the writer thread, for the first frame that fails to persist.</summary>
    public event EventHandler<FrameWriteFailedEventArgs>? WriteFailed;

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

        _queueCapacity = ComputeQueueCapacity(width, height);
        _frameQueue = Channel.CreateBounded<PendingFrame>(CreateQueueOptions(_queueCapacity));

        _writerLoop = Task.Run(ProcessQueuedFramesAsync);
    }

    internal static BoundedChannelOptions CreateQueueOptions(int queueCapacity)
        // +1 reserves a slot for the final gap-only marker enqueued by StopAcceptingFrames().
        => new(queueCapacity + 1)
        {
            FullMode = BoundedChannelFullMode.Wait,

            // AbortWriterAsync and Dispose may drain pending items after their bounded
            // wait expires while the writer is still releasing its in-flight frame.
            SingleReader = false,
            SingleWriter = false,
        };

    /// <summary>
    /// Depth of the writer queue, capped so a 4K capture cannot pin an unbounded amount of VRAM.
    /// </summary>
    internal static int ComputeQueueCapacity(int width, int height)
    {
        long frameBytes = Math.Max(1L, (long)Math.Max(1, width) * Math.Max(1, height) * 4);
        long affordable = MaxQueuedFrameBytes / frameBytes;
        return (int)Math.Clamp(affordable, MinQueueCapacity, MaxQueueCapacity);
    }

    /// <summary>
    /// Copies the capture surface into an independently owned buffer and queues it for
    /// encoding. Never blocks on disk, on the GPU readback or on JPEG encoding, so it is
    /// safe to call from the free-threaded frame-pool callback.
    /// </summary>
    /// <param name="surface">
    /// Capture surface. It is read only for the duration of this call — the caller may
    /// return it to the frame pool as soon as this method returns.
    /// </param>
    /// <param name="timestamp">Capture time of this frame, relative to capture start.</param>
    /// <param name="skippedSlots">CFR slots missed since the previous accepted frame.</param>
    /// <returns>False when the frame was dropped because the writer queue was saturated.</returns>
    public bool TryWriteFrame(IDirect3DSurface surface, TimeSpan timestamp, int skippedSlots = 0)
        => WriteFrameCore(surface, timestamp, skippedSlots, block: false);

    /// <summary>
    /// Queues a frame, waiting for writer capacity instead of dropping. For offline
    /// producers that own their own pacing; never call this from a capture callback.
    /// </summary>
    public void WriteFrame(IDirect3DSurface surface, TimeSpan timestamp, int skippedSlots = 0)
        => WriteFrameCore(surface, timestamp, skippedSlots, block: true);

    private bool WriteFrameCore(
        IDirect3DSurface surface, TimeSpan timestamp, int skippedSlots, bool block)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Frames still in flight when the capture stops are expected, not an error.
        if (_stopAccepting)
            return false;

        if (_finalized)
            throw new InvalidOperationException("Writer has been finalized.");

        if (skippedSlots < 0)
            skippedSlots = 0;

        if (!TryReserveQueueSlot(block))
        {
            DropFrame(skippedSlots);
            return false;
        }

        CanvasRenderTarget? copy = null;
        try
        {
            copy = CopyFrame(surface);

            int owed = Interlocked.Exchange(ref _pendingSkippedSlots, 0);
            if (!_frameQueue.Writer.TryWrite(new PendingFrame(copy, timestamp, skippedSlots + owed)))
            {
                // The queue closed underneath us (stop/dispose raced this callback).
                Interlocked.Add(ref _pendingSkippedSlots, skippedSlots + owed);
                ReleaseFrame(new PendingFrame(copy, timestamp, 0));
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // A failed copy is a lost frame, not a lost recording: keep the CFR slot owed
            // so the duration stays honest, and retain the failure for the session to report.
            if (copy is not null)
                ReleaseFrame(new PendingFrame(copy, timestamp, 0));
            else
                Interlocked.Decrement(ref _queueDepth);

            Interlocked.Add(ref _pendingSkippedSlots, skippedSlots + 1);
            RecordWriteFailure(ex, -1);
            return false;
        }
    }

    /// <summary>
    /// Reserves queue depth before any GPU work happens, so a saturated writer costs the
    /// capture callback nothing.
    /// </summary>
    private bool TryReserveQueueSlot(bool block)
    {
        while (true)
        {
            int depth = Volatile.Read(ref _queueDepth);
            if (depth < _queueCapacity)
            {
                if (Interlocked.CompareExchange(ref _queueDepth, depth + 1, depth) == depth)
                    return true;
                continue;
            }

            if (!block || _stopAccepting || _disposed || _writerLoop.IsCompleted)
                return false;

            Thread.Sleep(1);
        }
    }

    private void DropFrame(int skippedSlots)
    {
        Interlocked.Increment(ref _droppedFrames);

        // The dropped frame still occupied a CFR slot. Owe it to the next frame that makes
        // it through, which fills it with a duplicate — the recording keeps wall-clock length.
        Interlocked.Add(ref _pendingSkippedSlots, skippedSlots + 1);
    }

    /// <summary>
    /// Copies (and crops) the capture surface into a buffer this writer owns outright.
    /// The frame pool recycles its surfaces as soon as the callback returns, so nothing
    /// downstream may reference the source surface.
    /// </summary>
    private CanvasRenderTarget CopyFrame(IDirect3DSurface surface)
    {
        using var source = CanvasBitmap.CreateFromDirect3D11Surface(_device, surface);

        if (_cropRect is Rect crop)
        {
            var target = RentTarget(_width, _height);
            try
            {
                using var ds = target.CreateDrawingSession();
                ds.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                ds.DrawImage(source, new Rect(0, 0, _width, _height), crop);
            }
            catch
            {
                target.Dispose();
                throw;
            }

            return target;
        }

        // Uncropped captures are stored at their native size, exactly as the previous
        // synchronous path saved the wrapped surface.
        int width = (int)source.SizeInPixels.Width;
        int height = (int)source.SizeInPixels.Height;
        var copy = RentTarget(width, height);
        try
        {
            using var ds = copy.CreateDrawingSession();

            // Copy blend so the source pixels (including alpha) survive verbatim rather
            // than being composited onto the render target's transparent black.
            ds.Blend = CanvasBlend.Copy;
            ds.DrawImage(source, new Rect(0, 0, width, height), new Rect(0, 0, width, height));
        }
        catch
        {
            copy.Dispose();
            throw;
        }

        return copy;
    }

    private CanvasRenderTarget RentTarget(int width, int height)
    {
        lock (_poolLock)
        {
            if (_poolWidth != width || _poolHeight != height)
            {
                ClearTargetPoolLocked();
                _poolWidth = width;
                _poolHeight = height;
            }
            else if (_targetPool.Count > 0)
            {
                return _targetPool.Pop();
            }
        }

        return Win2DUtils.CreateRenderTarget(_device, width, height, 96, "video writer target pool");
    }

    private void ReturnTarget(CanvasRenderTarget target)
    {
        lock (_poolLock)
        {
            if (!_disposed
                && (int)target.SizeInPixels.Width == _poolWidth
                && (int)target.SizeInPixels.Height == _poolHeight
                && _targetPool.Count <= _queueCapacity)
            {
                _targetPool.Push(target);
                return;
            }
        }

        target.Dispose();
    }

    private void ClearTargetPool()
    {
        lock (_poolLock)
        {
            ClearTargetPoolLocked();
        }
    }

    private void ClearTargetPoolLocked()
    {
        while (_targetPool.Count > 0)
        {
            try { _targetPool.Pop().Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[VideoWriter] Target dispose failed: {ex.Message}"); }
        }
    }

    // ── Writer loop ─────────────────────────────────────────────────

    private async Task ProcessQueuedFramesAsync()
    {
        var ct = _writerCts.Token;
        var reader = _frameQueue.Reader;

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var frame))
                {
                    try
                    {
                        await WriteQueuedFrameAsync(frame, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        ReleaseFrame(frame);
                    }

                    if (ct.IsCancellationRequested)
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            RecordWriteFailure(ex, -1);
        }
        finally
        {
            DrainQueue();
        }
    }

    private async Task WriteQueuedFrameAsync(PendingFrame frame, CancellationToken ct)
    {
        if (frame.SkippedSlots > 0)
            FillGapFramesCore(frame.SkippedSlots);

        if (frame.Bitmap is null)
            return;

        long index = Interlocked.Read(ref _frameCount);
        string framePath = Path.Combine(_framesDir, $"frame_{index:D8}.jpg");

        try
        {
            ct.ThrowIfCancellationRequested();

            using (var stream = new FileStream(framePath, FileMode.Create, FileAccess.Write))
            {
                await frame.Bitmap
                    .SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, JpegQuality)
                    .AsTask(ct).ConfigureAwait(false);
            }

            lock (_tsLock)
            {
                _frameTimestamps.Add(frame.Timestamp);
            }

            // Publish the frame only once its JPEG is on disk: FrameCount is the
            // finalizer's guarantee that every index below it can be decoded.
            Interlocked.Increment(ref _frameCount);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFrameFile(framePath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFrameFile(framePath);
            RecordWriteFailure(ex, index);
        }
    }

    /// <summary>
    /// Fills missed frame slots by duplicating the most recently written JPEG so frame N
    /// always corresponds to wall-clock time N/fps. Writer-thread only.
    /// </summary>
    private void FillGapFramesCore(int count)
    {
        long prevIndex = Interlocked.Read(ref _frameCount) - 1;
        if (prevIndex < 0) return;

        string srcPath = Path.Combine(_framesDir, $"frame_{prevIndex:D8}.jpg");
        if (!File.Exists(srcPath)) return;

        for (int i = 0; i < count; i++)
        {
            long gapIndex = Interlocked.Read(ref _frameCount);
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

                Interlocked.Increment(ref _frameCount);
            }
            catch (Exception ex)
            {
                // Leaving the slot unfilled shortens the recording slightly; leaving a hole
                // in the sequence would break finalization outright.
                TryDeleteFrameFile(dstPath);
                RecordWriteFailure(ex, gapIndex);
                return;
            }
        }
    }

    private void ReleaseFrame(PendingFrame frame)
    {
        if (frame.Bitmap is not null)
        {
            try { ReturnTarget(frame.Bitmap); }
            catch (Exception ex) { Debug.WriteLine($"[VideoWriter] Frame release failed: {ex.Message}"); }
        }

        Interlocked.Decrement(ref _queueDepth);
    }

    private void DrainQueue()
    {
        while (_frameQueue.Reader.TryRead(out var frame))
            ReleaseFrame(frame);
    }

    private static void TryDeleteFrameFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Debug.WriteLine($"[VideoWriter] Could not remove partial frame: {ex.Message}"); }
    }

    private void RecordWriteFailure(Exception ex, long frameIndex)
    {
        Interlocked.Increment(ref _failedWrites);
        int consecutive = Interlocked.Increment(ref _consecutiveFailures);

        bool isFirst = false;
        lock (_errorLock)
        {
            if (_firstWriteError is null)
            {
                _firstWriteError = ex;
                _firstWriteErrorIndex = frameIndex;
                isFirst = true;
            }
        }

        Debug.WriteLine(
            $"[VideoWriter] ERROR writing frame {frameIndex}: {ex.GetType().Name}: {ex.Message}");

        if (consecutive >= MaxConsecutiveWriteFailures && !_stopAccepting)
        {
            _stopAccepting = true;
            Debug.WriteLine(
                $"[VideoWriter] Giving up after {consecutive} consecutive write failures; " +
                $"{Interlocked.Read(ref _frameCount)} frames preserved in {_framesDir}.");
        }

        if (!isFirst)
            return;

        try
        {
            WriteFailed?.Invoke(this, new FrameWriteFailedEventArgs(ex, frameIndex, _framesDir));
        }
        catch (Exception handlerEx)
        {
            // A subscriber must never take down the writer thread.
            Debug.WriteLine($"[VideoWriter] WriteFailed handler threw: {handlerEx.Message}");
        }
    }

    // ── Shutdown ────────────────────────────────────────────────────

    /// <summary>
    /// Closes the frame gate. Already-queued frames are still written; anything the capture
    /// engine hands over afterwards is ignored. Idempotent.
    /// </summary>
    public void StopAcceptingFrames()
    {
        _stopAccepting = true;

        if (Interlocked.Exchange(ref _writerCompleted, 1) != 0)
            return;

        // Flush the CFR slots owed by dropped frames so a recording whose tail was dropped
        // still ends at the right wall-clock time.
        int owed = Interlocked.Exchange(ref _pendingSkippedSlots, 0);
        if (owed > 0)
        {
            Interlocked.Increment(ref _queueDepth);
            if (!_frameQueue.Writer.TryWrite(new PendingFrame(null, TimeSpan.Zero, owed)))
                Interlocked.Decrement(ref _queueDepth);
        }

        _frameQueue.Writer.TryComplete();
    }

    /// <summary>
    /// Waits for every accepted frame to be encoded and written. On return (normal or
    /// faulted) the writer loop is guaranteed to have stopped touching the frames
    /// directory, so finalization can never race a pending write.
    /// </summary>
    public async Task WaitForQuiescenceAsync(TimeSpan timeout, CancellationToken ct)
    {
        StopAcceptingFrames();

        try
        {
            await _writerLoop.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await AbortWriterAsync().ConfigureAwait(false);
            throw new TimeoutException($"Timed out waiting {timeout} for frame writes to finish.");
        }
        catch (OperationCanceledException)
        {
            await AbortWriterAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AbortWriterAsync()
    {
        try { _writerCts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Debug.WriteLine($"[VideoWriter] Writer cancel failed: {ex.Message}"); }

        try
        {
            await _writerLoop.WaitAsync(WriterShutdownGrace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[VideoWriter] Writer loop did not stop within {WriterShutdownGrace}: {ex.Message}");
        }

        DrainQueue();
    }

    /// <summary>
    /// Accrues CFR gap slots to be filled just before the next queued frame is written.
    /// Retained for producers that compute gaps separately from the frame they precede;
    /// <see cref="TryWriteFrame"/> carries its own <c>skippedSlots</c>.
    /// </summary>
    public void FillGapFrames(int count)
    {
        if (count <= 0) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stopAccepting)
            return;

        Interlocked.Add(ref _pendingSkippedSlots, count);
    }

    /// <summary>
    /// Assembles all captured JPEG frames into an MP4 file by streaming BGRA8 samples to H.264.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_finalized)
            return;

        // Close the gate before flagging finalization so a capture callback that is still
        // in flight sees a refusal rather than "writer has been finalized".
        StopAcceptingFrames();
        _finalized = true;

        // Finalization reads the frames directory, so the writer must be done with it.
        // Callers normally reach quiescence first; this makes it a guarantee either way.
        await DrainWriterForFinalizeAsync(ct).ConfigureAwait(false);

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
                $"OutputPath={_outputPath}\n" +
                $"DroppedFrames={DroppedFrames}, FailedWrites={FailedFrameWrites}\n" +
                $"FirstWriteError={FirstWriteError?.Message ?? "none"}\n");
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
            WriteFinalizedMarker();
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
    /// Records that this session's MP4 was produced by the current, orientation-correct
    /// encoder.
    /// </summary>
    /// <remarks>
    /// Older builds wrote a vertically flipped MP4 (see the <c>BitmapFlip.Vertical</c>
    /// comment in <see cref="ProduceFrameSampleAsync"/>). Nothing displayed that file at
    /// the time, so the defect was invisible — but it means a legacy session's captured
    /// JPEGs are its only correctly oriented copy. Cleanup keys off this marker so those
    /// sessions keep their frames.
    /// </remarks>
    private void WriteFinalizedMarker()
    {
        try
        {
            var markerPath = Path.Combine(
                Path.GetDirectoryName(_outputPath)!, FinalizedMarkerName);
            File.WriteAllText(markerPath, RecordingMarker.BuildContent(EncoderVersion));
        }
        catch (Exception ex)
        {
            // Losing the marker only costs disk space (frames are retained), never data.
            Debug.WriteLine($"[VideoWriter] Could not write finalized marker: {ex.Message}");
        }
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
    /// <remarks>
    /// The marker is required as well as the flag. Writing it is best effort, and a
    /// marker-less MP4 is indistinguishable from one produced by the pre-orientation-fix
    /// encoder — so releasing the frames without it would leave a recording that later
    /// gets flipped on the assumption it is legacy. Keeping the frames costs disk space;
    /// dropping them here costs the only correctly oriented copy.
    /// </remarks>
    /// <returns>Bytes reclaimed, or 0 if nothing was deleted.</returns>
    public long DeleteCapturedFrames()
    {
        if (!_finalizeSucceeded || !Directory.Exists(_framesDir))
            return 0;

        if (!RecordingMarker.Exists(_outputPath))
        {
            Debug.WriteLine(
                "[VideoWriter] Keeping captured frames: no finalized marker was written.");
            return 0;
        }

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

    /// <summary>
    /// Stops the frame gate and waits for the writer loop to finish, so finalization never
    /// races a pending JPEG write. Falls back to cancelling the loop if it will not drain.
    /// </summary>
    private async Task DrainWriterForFinalizeAsync(CancellationToken ct)
    {
        StopAcceptingFrames();

        if (_writerLoop.IsCompleted)
            return;

        try
        {
            await _writerLoop.WaitAsync(FinalizeDrainTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWriter] Writer did not drain before finalize: {ex.Message}");
            await AbortWriterAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        StopAcceptingFrames();

        try { _writerCts.Cancel(); }
        catch (Exception ex) { Debug.WriteLine($"[VideoWriter] Writer cancel failed: {ex.Message}"); }

        try
        {
            if (!_writerLoop.Wait(WriterShutdownGrace))
                Debug.WriteLine($"[VideoWriter] Writer loop still running after {WriterShutdownGrace}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWriter] Writer loop shutdown failed: {ex.Message}");
        }

        DrainQueue();
        ClearTargetPool();

        // Only safe once nothing can still observe the token.
        if (_writerLoop.IsCompleted)
            _writerCts.Dispose();
    }
}
