using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Musio.Core.Diagnostics;
using Musio.Core.Models;
using Windows.UI;

namespace Musio.Core.Processing;

/// <summary>
/// Snapshot of the most recent background-image load failure. Cancellation is never
/// reported here — a cancelled load leaves no failure record at all.
/// </summary>
/// <param name="Path">Image path that failed to load.</param>
/// <param name="Reason">Human-readable reason (exception type/message, or "file not found").</param>
/// <param name="Attempts">Number of failed attempts recorded for this path/device key.</param>
/// <param name="WillRetry">
/// True while the render path is still allowed to retry this key on its own; false once
/// the attempt budget is exhausted, in which case only an explicit
/// <see cref="BackgroundCompositor.PreloadAsync"/> or
/// <see cref="BackgroundCompositor.InvalidateImageCache"/> resumes loading.
/// </param>
public sealed record BackgroundImageLoadFailure(string Path, string Reason, int Attempts, bool WillRetry);

public record BackgroundStyle
{
    public BackgroundType Type { get; init; } = BackgroundType.SolidColor;
    public string Color { get; init; } = "#1a1a2e";
    public string GradientEndColor { get; init; } = "#16213e";
    public double GradientAngle { get; init; } = 135;
    public string? BackgroundImagePath { get; init; }
    public int Padding { get; init; } = 48;
    public int CornerRadius { get; init; } = 12;
    public bool ShadowEnabled { get; init; } = true;
    public int ShadowBlur { get; init; } = 24;
    public double ShadowOpacity { get; init; } = 0.5;
    public string ShadowColor { get; init; } = "#000000";
    public int ShadowOffsetX { get; init; } = 0;
    public int ShadowOffsetY { get; init; } = 4;
    public bool BorderEnabled { get; init; } = false;
    public int BorderWidth { get; init; } = 1;
    public string BorderColor { get; init; } = "#333333";
}

/// <summary>
/// Renders background, padding, corner radius, shadow, and border around a captured screen frame.
/// Operates purely on a caller-provided <see cref="CanvasDrawingSession"/>.
/// </summary>
public sealed class BackgroundCompositor : IDisposable
{
    // Background-image cache. Every field below is guarded by _cacheLock, including the
    // draw itself, so a preload completing on the thread pool can never dispose a bitmap
    // out from under an in-flight DrawImage. Nothing awaits while the lock is held.
    private readonly object _cacheLock = new();

    private CanvasBitmap? _cachedBackgroundImage;
    private string? _cachedBackgroundPath;
    private CanvasDevice? _cachedDevice;

    private Task? _inflightLoad;
    private string? _inflightPath;
    private CanvasDevice? _inflightDevice;
    private long _currentLoadId;
    private long _nextLoadId;

    // Last key that failed to load, with a bounded exponential backoff. Remembered so the
    // render path does not re-request a doomed load every frame, while still recovering
    // from a transient failure (file briefly locked, momentary device loss) on its own.
    // Cancellation is deliberately NOT recorded here: it says nothing about the key.
    private string? _failedPath;
    private CanvasDevice? _failedDevice;
    private int _failedAttempts;
    private long _retryNotBeforeMs;
    private string? _failureReason;

    // Retry policy. Internal so tests can compress the schedule; the defaults keep the
    // render path from re-attempting more than a handful of times per key.
    internal const int MaxLoadAttempts = 4;
    internal TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(750);
    internal TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(10);

    // Test seam for the actual decode. Returning null means "no bitmap for this key"
    // (treated exactly like a missing file); throwing OperationCanceledException means
    // cancellation; any other exception is a retryable failure.
    internal Func<CanvasDevice, string, CancellationToken, Task<CanvasBitmap?>>? ImageLoaderOverride
    { get; set; }

    // Cancelled on Dispose so an in-flight load stops rather than running to completion
    // against a device the compositor no longer tracks. Deliberately never disposed: an
    // in-flight load still holds this token, and disposing it underneath that load would
    // surface an ObjectDisposedException instead of a clean cancellation.
    private readonly CancellationTokenSource _shutdownCts = new();

    private bool _disposed;

    /// <summary>
    /// Returns the output canvas dimensions. Padding now insets the content within
    /// the canvas rather than extending it, so the canvas size matches the configured
    /// aspect ratio regardless of padding.
    /// </summary>
    public (int width, int height) CalculateOutputSize(int sourceWidth, int sourceHeight, BackgroundStyle style)
    {
        _ = style;
        return (sourceWidth, sourceHeight);
    }

    /// <summary>
    /// Renders one complete composited frame: background → shadow → screen content → border.
    /// Padding (user setting + any aspect-ratio fit gap) is implicit in the source rect's
    /// position and dimensions — there is no separate inner-content container.
    /// </summary>
    /// <param name="screenFrame">Cropped buffer sized exactly to (srcWidth, srcHeight).</param>
    /// <param name="srcX">X position of the source frame inside the output canvas.</param>
    /// <param name="srcY">Y position of the source frame inside the output canvas.</param>
    /// <param name="srcWidth">Width of the source frame inside the output canvas.</param>
    /// <param name="srcHeight">Height of the source frame inside the output canvas.</param>
    public void CompositeFrame(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        int outputWidth,
        int outputHeight,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        BackgroundStyle style)
    {
        float w = outputWidth;
        float h = outputHeight;
        float sx = srcX;
        float sy = srcY;
        float sw = srcWidth;
        float sh = srcHeight;
        float radius = style.CornerRadius;

        // 1. Background fills the entire output canvas — everything outside the
        //    source rect is one continuous background container.
        DrawBackground(session, screenFrame, w, h, sw, sh, sx, sy, style);

        // 2. Shadow under the source frame.
        if (style.ShadowEnabled)
        {
            DrawShadow(session, sx, sy, sw, sh, radius, style);
        }

        // 3. Source content drawn 1:1 at its rect; rounded-corner clip wraps the frame.
        DrawContent(session, screenFrame, sx, sy, sw, sh, radius);

        // 4. Border around the source frame.
        if (style.BorderEnabled && style.BorderWidth > 0)
        {
            DrawBorder(session, sx, sy, sw, sh, radius, style);
        }
    }

    private void DrawBackground(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        float w, float h,
        float contentW, float contentH,
        float contentX, float contentY,
        BackgroundStyle style)
    {
        switch (style.Type)
        {
            case BackgroundType.SolidColor:
                session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
                break;

            case BackgroundType.Gradient:
                DrawGradientBackground(session, w, h, style);
                break;

            case BackgroundType.Image:
                DrawImageBackground(session, w, h, style);
                break;

            case BackgroundType.Blur:
                DrawBlurBackground(session, screenFrame, w, h, contentW, contentH, contentX, contentY, style);
                break;
        }
    }

    private static void DrawGradientBackground(
        CanvasDrawingSession session, float w, float h, BackgroundStyle style)
    {
        double angleRad = style.GradientAngle * Math.PI / 180.0;
        float cx = w / 2f;
        float cy = h / 2f;
        float diag = MathF.Sqrt(w * w + h * h) / 2f;
        float dx = diag * MathF.Cos((float)angleRad);
        float dy = diag * MathF.Sin((float)angleRad);

        var startColor = ColorHelper.ParseColor(style.Color);
        var endColor = ColorHelper.ParseColor(style.GradientEndColor);

        using var brush = new CanvasLinearGradientBrush(session, startColor, endColor)
        {
            StartPoint = new Vector2(cx - dx, cy - dy),
            EndPoint = new Vector2(cx + dx, cy + dy)
        };

        session.FillRectangle(0, 0, w, h, brush);
    }

    private void DrawImageBackground(
        CanvasDrawingSession session, float w, float h, BackgroundStyle style)
    {
        var path = style.BackgroundImagePath;
        if (string.IsNullOrEmpty(path))
        {
            // Fallback to solid color when no image path is set
            session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
            return;
        }

        // The render path NEVER blocks on file I/O or GPU decode. It draws the wallpaper
        // only when a bitmap for this exact (path, device) key is already cached by
        // PreloadAsync; otherwise it falls back to the solid colour and asks for a warm
        // so a later frame can pick the image up. A failed warm is retried on a bounded
        // backoff, so a transient failure recovers without re-attempting every frame.
        lock (_cacheLock)
        {
            if (_cachedBackgroundImage is not null
                && PathMatches(_cachedBackgroundPath, path)
                && ReferenceEquals(_cachedDevice, session.Device))
            {
                DrawScaledToFill(session, _cachedBackgroundImage, w, h);
                return;
            }
        }

        session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
        RequestWarm(session.Device, path);
    }

    private static void DrawScaledToFill(
        CanvasDrawingSession session, CanvasBitmap image, float w, float h)
    {
        var srcSize = image.SizeInPixels;
        if (srcSize.Width == 0 || srcSize.Height == 0) return;

        // Scale-to-fill: compute scale so image covers entire output
        float scaleX = w / srcSize.Width;
        float scaleY = h / srcSize.Height;
        float scale = Math.Max(scaleX, scaleY);
        float drawW = srcSize.Width * scale;
        float drawH = srcSize.Height * scale;
        float drawX = (w - drawW) / 2f;
        float drawY = (h - drawH) / 2f;

        session.DrawImage(image, new Windows.Foundation.Rect(drawX, drawY, drawW, drawH));
    }

    #region Background image preload / invalidation

    /// <summary>
    /// Asynchronously loads and caches the style's background image for
    /// <paramref name="device"/> so the synchronous render path never has to block on
    /// file I/O or GPU decode. Call this from every preview/export construction or
    /// configuration path before the first frame is composited.
    /// <para>
    /// The cache is keyed by (image path, <see cref="CanvasDevice"/>). Requesting a
    /// different key — or a style that no longer uses an image — deterministically
    /// disposes the previously cached bitmap. Failures (missing/unreadable/corrupt file,
    /// lost device) do not throw: rendering uses the solid-colour fallback, the failure
    /// is recorded in <see cref="LastImageLoadFailure"/>, and the render path retries the
    /// key on a bounded backoff. Cancelling <paramref name="cancellationToken"/> abandons
    /// the caller's wait without recording a failure, so the key stays loadable.
    /// </para>
    /// </summary>
    public Task PreloadAsync(
        CanvasDevice device, BackgroundStyle style, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(style);

        if (style.Type != BackgroundType.Image || string.IsNullOrEmpty(style.BackgroundImagePath))
        {
            // The style no longer needs an image — release the cached bitmap now rather
            // than holding a full-resolution surface for the compositor's lifetime.
            InvalidateImageCache();
            return Task.CompletedTask;
        }

        lock (_cacheLock)
        {
            // An explicit preload is also an explicit retry: it clears the backoff and
            // the exhausted attempt budget for this key.
            ClearFailureFor(style.BackgroundImagePath, device);
        }

        return EnsureImageLoadedAsync(device, style.BackgroundImagePath, cancellationToken);
    }

    /// <summary>
    /// The most recent background-image load failure, or null when nothing has failed
    /// since the last success, invalidation, or explicit preload. Callers that must not
    /// silently fall back (export setup in particular) can surface this to the user or a
    /// log instead of assuming the wallpaper was simply not configured.
    /// </summary>
    public BackgroundImageLoadFailure? LastImageLoadFailure
    {
        get
        {
            lock (_cacheLock)
            {
                if (_failedPath is null || _failureReason is null) return null;
                return new BackgroundImageLoadFailure(
                    _failedPath, _failureReason, _failedAttempts, _failedAttempts < MaxLoadAttempts);
            }
        }
    }

    /// <summary>
    /// Returns true when a background image for this style is cached for
    /// <paramref name="device"/> and can be drawn without blocking. Styles that do not
    /// use an image are trivially "ready".
    /// </summary>
    public bool IsBackgroundImageReady(CanvasDevice device, BackgroundStyle style)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(style);

        if (style.Type != BackgroundType.Image || string.IsNullOrEmpty(style.BackgroundImagePath))
            return true;

        lock (_cacheLock)
        {
            return _cachedBackgroundImage is not null
                && PathMatches(_cachedBackgroundPath, style.BackgroundImagePath)
                && ReferenceEquals(_cachedDevice, device);
        }
    }

    /// <summary>
    /// Drops and disposes the cached background image. Call on device loss (the bitmap
    /// belongs to the lost device) or when the background style stops using an image.
    /// In-flight loads are orphaned: their result is discarded when it lands.
    /// </summary>
    public void InvalidateImageCache()
    {
        CanvasBitmap? stale;
        lock (_cacheLock)
        {
            stale = _cachedBackgroundImage;
            _cachedBackgroundImage = null;
            _cachedBackgroundPath = null;
            _cachedDevice = null;
            _inflightLoad = null;
            _inflightPath = null;
            _inflightDevice = null;
            _currentLoadId = 0;
            ClearFailure();
        }

        SafeDispose(stale);
    }

    private Task EnsureImageLoadedAsync(CanvasDevice device, string path, CancellationToken ct)
    {
        CanvasBitmap? stale = null;
        Task inflight;

        lock (_cacheLock)
        {
            if (_disposed) return Task.CompletedTask;

            if (_cachedBackgroundImage is not null
                && PathMatches(_cachedBackgroundPath, path)
                && ReferenceEquals(_cachedDevice, device))
            {
                return Task.CompletedTask;
            }

            if (_inflightLoad is not null
                && PathMatches(_inflightPath, path)
                && ReferenceEquals(_inflightDevice, device))
            {
                inflight = _inflightLoad;
            }
            else
            {
                // The cached bitmap belongs to a different path or device — release it now
                // rather than holding a full-resolution surface until the new load lands
                // (or forever, if the new load fails).
                stale = _cachedBackgroundImage;
                _cachedBackgroundImage = null;
                _cachedBackgroundPath = null;
                _cachedDevice = null;

                long loadId = ++_nextLoadId;
                _inflightPath = path;
                _inflightDevice = device;
                _currentLoadId = loadId;
                // LoadImageAsync yields to the thread pool before touching the file system,
                // so no I/O happens while this lock is held or on the calling thread. The
                // load runs on the compositor's own shutdown token, never on a caller's
                // token: one caller cancelling must not abort the shared load for everyone.
                inflight = LoadImageAsync(device, path, loadId, _shutdownCts.Token);

                // A load that completed synchronously has already published its result and
                // cleared the in-flight key; only record it while it is genuinely current.
                if (_currentLoadId == loadId
                    && PathMatches(_inflightPath, path)
                    && ReferenceEquals(_inflightDevice, device))
                {
                    _inflightLoad = inflight;
                }
            }
        }

        SafeDispose(stale);
        return ObserveLoad(inflight, ct);
    }

    /// <summary>
    /// Lets a caller stop waiting on the shared load when its own token is cancelled.
    /// The shared load keeps running, so a cancelled caller neither aborts nor poisons
    /// the key for anyone else.
    /// </summary>
    private static Task ObserveLoad(Task load, CancellationToken ct) =>
        !ct.CanBeCanceled || load.IsCompleted ? load : load.WaitAsync(ct);

    /// <summary>
    /// Fire-and-forget warm requested from the render path after a cache miss. Deduplicated
    /// by the in-flight key, and gated by the failure backoff so a failing key is retried
    /// a bounded number of times rather than once per frame.
    /// </summary>
    private void RequestWarm(CanvasDevice device, string path)
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            if (!CanAttempt(path, device)) return;
            if (_inflightLoad is not null
                && PathMatches(_inflightPath, path)
                && ReferenceEquals(_inflightDevice, device))
            {
                return;
            }
        }

        try
        {
            _ = EnsureImageLoadedAsync(device, path, CancellationToken.None);
        }
        catch
        {
            // Warming is best-effort; the solid-colour fallback already covers this frame.
        }
    }

    /// <summary>
    /// True when the render path may start a load for this key: either nothing has failed
    /// for it, or its backoff has elapsed and the attempt budget is not exhausted.
    /// Must be called under <see cref="_cacheLock"/>.
    /// </summary>
    private bool CanAttempt(string path, CanvasDevice device)
    {
        if (!PathMatches(_failedPath, path) || !ReferenceEquals(_failedDevice, device))
            return true;
        if (_failedAttempts >= MaxLoadAttempts)
            return false;
        return Environment.TickCount64 >= _retryNotBeforeMs;
    }

    /// <summary>Must be called under <see cref="_cacheLock"/>.</summary>
    private void ClearFailure()
    {
        _failedPath = null;
        _failedDevice = null;
        _failedAttempts = 0;
        _retryNotBeforeMs = 0;
        _failureReason = null;
    }

    /// <summary>Must be called under <see cref="_cacheLock"/>.</summary>
    private void ClearFailureFor(string path, CanvasDevice device)
    {
        if (PathMatches(_failedPath, path) && ReferenceEquals(_failedDevice, device))
            ClearFailure();
    }

    /// <summary>
    /// Records a failed attempt and schedules the next one. Must be called under
    /// <see cref="_cacheLock"/>. Returns the attempt count after recording.
    /// </summary>
    private int MarkFailed(string path, CanvasDevice device, string reason)
    {
        if (!PathMatches(_failedPath, path) || !ReferenceEquals(_failedDevice, device))
        {
            _failedPath = path;
            _failedDevice = device;
            _failedAttempts = 0;
        }

        _failedAttempts++;
        _failureReason = reason;

        double baseMs = RetryBaseDelay.TotalMilliseconds;
        double maxMs = Math.Max(baseMs, RetryMaxDelay.TotalMilliseconds);
        double delayMs = Math.Min(maxMs, baseMs * Math.Pow(2, _failedAttempts - 1));
        _retryNotBeforeMs = Environment.TickCount64 + (long)delayMs;

        return _failedAttempts;
    }

    private async Task LoadImageAsync(
        CanvasDevice device, string path, long loadId, CancellationToken ct)
    {
        CanvasBitmap? loaded = null;
        string? failure = null;
        bool canceled = false;
        try
        {
            var loader = ImageLoaderOverride;
            if (loader is not null)
            {
                loaded = await loader(device, path, ct).ConfigureAwait(false);
                if (loaded is null) failure = "loader returned no bitmap";
            }
            else
            {
                // Hop off the caller's thread (UI thread in preview, encoder thread in
                // export) before any file-system access.
                bool exists = await Task.Run(() => File.Exists(path), ct).ConfigureAwait(false);
                if (exists)
                    loaded = await CanvasBitmap.LoadAsync(device, path).AsTask(ct).ConfigureAwait(false);
                else
                    failure = "file not found";
            }
        }
        catch (Exception ex)
        {
            // Cancellation says nothing about the key — it must never be recorded as a
            // failure, or a cancelled preload would permanently force the solid-colour
            // fallback. Everything else (unreadable/corrupt file, lost device) is treated
            // as potentially transient and retried on a bounded backoff.
            if (ex is OperationCanceledException || ct.IsCancellationRequested)
                canceled = true;
            else
                failure = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Publish(device, path, loadId, loaded, failure, canceled);
        }
    }

    private void Publish(
        CanvasDevice device, string path, long loadId,
        CanvasBitmap? loaded, string? failure, bool canceled)
    {
        CanvasBitmap? stale = null;
        bool discard = false;
        int attempts = 0;
        bool exhausted = false;

        lock (_cacheLock)
        {
            if (_currentLoadId == loadId
                && PathMatches(_inflightPath, path)
                && ReferenceEquals(_inflightDevice, device))
            {
                _inflightLoad = null;
                _inflightPath = null;
                _inflightDevice = null;
                _currentLoadId = 0;
            }
            else
            {
                // A newer request superseded this one — drop the result.
                discard = true;
            }

            if (!discard && loaded is not null && !_disposed)
            {
                // A bitmap that arrived is always kept, even if the load was cancelled
                // after the decode completed — throwing it away would waste the work and
                // leave the render path on the fallback colour.
                stale = _cachedBackgroundImage;
                _cachedBackgroundImage = loaded;
                _cachedBackgroundPath = path;
                _cachedDevice = device;
                ClearFailureFor(path, device);
            }
            else if (!discard && loaded is null && !canceled && failure is not null && !_disposed)
            {
                attempts = MarkFailed(path, device, failure);
                exhausted = attempts >= MaxLoadAttempts;
            }
        }

        if (discard || (_disposed && loaded is not null))
            SafeDispose(loaded);

        SafeDispose(stale);

        if (attempts > 0)
        {
            DiagLog.Write(
                "BackgroundCompositor",
                exhausted
                    ? $"Background image '{path}' failed to load after {attempts} attempts ({failure}); "
                      + "rendering the solid-colour fallback until the style is preloaded again."
                    : $"Background image '{path}' failed to load (attempt {attempts}: {failure}); "
                      + "will retry after backoff.");
        }
    }

    private static bool PathMatches(string? cached, string requested) =>
        cached is not null && string.Equals(cached, requested, StringComparison.OrdinalIgnoreCase);

    private static void SafeDispose(CanvasBitmap? bitmap)
    {
        if (bitmap is null) return;
        try { bitmap.Dispose(); }
        catch { /* device already lost — nothing to release */ }
    }

    #endregion

    private static void DrawBlurBackground(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        float w, float h,
        float contentW, float contentH,
        float contentX, float contentY,
        BackgroundStyle style)
    {
        // Scale screen frame to fill entire output, then blur it
        float scaleX = w / contentW;
        float scaleY = h / contentH;
        float scale = Math.Max(scaleX, scaleY);

        using var scaleEffect = new Transform2DEffect
        {
            Source = screenFrame,
            TransformMatrix = Matrix3x2.CreateScale(scale)
        };

        using var blurEffect = new GaussianBlurEffect
        {
            Source = scaleEffect,
            BlurAmount = 30f,
            BorderMode = EffectBorderMode.Hard
        };

        session.DrawImage(blurEffect, new Vector2(0, 0));
    }

    private static void DrawShadow(
        CanvasDrawingSession session,
        float x, float y, float w, float h,
        float radius, BackgroundStyle style)
    {
        var device = session.Device;

        // Create a rounded rectangle mask as the shadow source
        using var geometry = CanvasGeometry.CreateRoundedRectangle(device, x, y, w, h, radius, radius);

        using var commandList = new CanvasCommandList(device);
        using (var maskSession = commandList.CreateDrawingSession())
        {
            var shadowBaseColor = ColorHelper.ParseColor(style.ShadowColor);
            var shadowColor = ColorHelper.WithOpacity(shadowBaseColor, style.ShadowOpacity);
            maskSession.FillGeometry(geometry, shadowColor);
        }

        using var shadowEffect = new ShadowEffect
        {
            Source = commandList,
            BlurAmount = style.ShadowBlur,
            ShadowColor = ColorHelper.WithOpacity(
                ColorHelper.ParseColor(style.ShadowColor), style.ShadowOpacity)
        };

        session.DrawImage(shadowEffect, new Vector2(style.ShadowOffsetX, style.ShadowOffsetY));
    }

    private static void DrawContent(
        CanvasDrawingSession session,
        CanvasBitmap screenFrame,
        float x, float y, float w, float h,
        float radius)
    {
        var destRect = new Windows.Foundation.Rect(x, y, w, h);

        if (radius > 0)
        {
            using var clipGeometry = CanvasGeometry.CreateRoundedRectangle(
                session.Device, x, y, w, h, radius, radius);
            using var layer = session.CreateLayer(1.0f, clipGeometry);
            session.DrawImage(screenFrame, destRect);
        }
        else
        {
            session.DrawImage(screenFrame, destRect);
        }
    }

    private static void DrawBorder(
        CanvasDrawingSession session,
        float x, float y, float w, float h,
        float radius, BackgroundStyle style)
    {
        float halfBorder = style.BorderWidth / 2f;
        var borderColor = ColorHelper.ParseColor(style.BorderColor);

        // Inset the stroke so it aligns with the content edge
        session.DrawRoundedRectangle(
            x - halfBorder,
            y - halfBorder,
            w + style.BorderWidth,
            h + style.BorderWidth,
            radius,
            radius,
            borderColor,
            style.BorderWidth);
    }

    /// <summary>
    /// Fills the given rectangle with the background style — without any shadow,
    /// content, border, clipping, or padding. Used to fill letterbox/pillarbox bars
    /// inside the content buffer when the canvas is larger than the source frame
    /// (FitMode = Contain).
    /// </summary>
    /// <param name="session">Drawing session for the buffer being filled.</param>
    /// <param name="screenFrame">
    /// Optional screen frame used as the source for blur backgrounds. May be null
    /// when the style is not a blur background.
    /// </param>
    public void FillBackgroundRect(
        CanvasDrawingSession session,
        CanvasBitmap? screenFrame,
        float x, float y, float w, float h,
        BackgroundStyle style)
    {
        if (w <= 0f || h <= 0f) return;

        var previousTransform = session.Transform;
        session.Transform = Matrix3x2.CreateTranslation(x, y) * previousTransform;
        try
        {
            switch (style.Type)
            {
                case BackgroundType.SolidColor:
                    session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
                    break;

                case BackgroundType.Gradient:
                    DrawGradientBackground(session, w, h, style);
                    break;

                case BackgroundType.Image:
                    DrawImageBackground(session, w, h, style);
                    break;

                case BackgroundType.Blur:
                    if (screenFrame is not null)
                        DrawBlurFill(session, screenFrame, w, h);
                    else
                        session.FillRectangle(0, 0, w, h, ColorHelper.ParseColor(style.Color));
                    break;
            }
        }
        finally
        {
            session.Transform = previousTransform;
        }
    }

    private static void DrawBlurFill(
        CanvasDrawingSession session, CanvasBitmap screenFrame, float w, float h)
    {
        var src = screenFrame.SizeInPixels;
        float scaleX = w / src.Width;
        float scaleY = h / src.Height;
        float scale = Math.Max(scaleX, scaleY);

        using var scaleEffect = new Transform2DEffect
        {
            Source = screenFrame,
            TransformMatrix = Matrix3x2.CreateScale(scale)
        };
        using var blurEffect = new GaussianBlurEffect
        {
            Source = scaleEffect,
            BlurAmount = 30f,
            BorderMode = EffectBorderMode.Hard
        };
        session.DrawImage(blurEffect, new Vector2(0, 0));
    }

    public void Dispose()
    {
        CanvasBitmap? stale;
        lock (_cacheLock)
        {
            if (_disposed) return;
            _disposed = true;

            stale = _cachedBackgroundImage;
            _cachedBackgroundImage = null;
            _cachedBackgroundPath = null;
            _cachedDevice = null;
            // In-flight loads are orphaned; Publish discards their result once disposed.
            _inflightLoad = null;
            _inflightPath = null;
            _inflightDevice = null;
            _currentLoadId = 0;
            ClearFailure();
        }

        // Stop any in-flight load. Cancellation is never recorded as a failure, and the
        // token source is intentionally not disposed while that load may still read it.
        try { _shutdownCts.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }

        SafeDispose(stale);
    }
}
