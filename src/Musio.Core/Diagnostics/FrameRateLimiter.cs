using System.Diagnostics;

namespace Musio.Core.Diagnostics;

/// <summary>
/// Precise frame-rate limiter that uses <see cref="Stopwatch"/> timing
/// with a spin-wait strategy for sub-millisecond accuracy.
/// </summary>
public class FrameRateLimiter
{
    private readonly long _targetTicksPerFrame;
    private readonly Stopwatch _stopwatch = new();

    private long _lastFrameTick;
    private long _frameCount;
    private long _fpsWindowStartTick;

    /// <summary>
    /// Measured actual frames per second over a rolling window.
    /// </summary>
    public double ActualFps { get; private set; }

    /// <summary>
    /// Creates a limiter targeting <paramref name="targetFps"/> frames per second.
    /// </summary>
    public FrameRateLimiter(int targetFps)
    {
        if (targetFps <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetFps), "Target FPS must be positive.");

        _targetTicksPerFrame = Stopwatch.Frequency / targetFps;
        _stopwatch.Start();
        _lastFrameTick = _stopwatch.ElapsedTicks;
        _fpsWindowStartTick = _lastFrameTick;
    }

    /// <summary>
    /// Blocks until the next frame time to maintain the target frame rate.
    /// Uses <c>Thread.Sleep</c> for coarse waits and spin-waits for the
    /// final sub-millisecond portion for high precision.
    /// </summary>
    public void WaitForNextFrame()
    {
        var targetTick = _lastFrameTick + _targetTicksPerFrame;

        // Coarse sleep: yield time back to the OS while we're far from the target
        while (true)
        {
            var remaining = targetTick - _stopwatch.ElapsedTicks;
            if (remaining <= 0) break;

            var remainingMs = (remaining * 1000.0) / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                Thread.Sleep(1);
            }
            else
            {
                // Spin-wait for sub-millisecond precision
                while (_stopwatch.ElapsedTicks < targetTick)
                {
                    Thread.SpinWait(10);
                }
                break;
            }
        }

        var now = _stopwatch.ElapsedTicks;
        _lastFrameTick = now;

        // Update measured FPS every second
        _frameCount++;
        var elapsedSinceWindow = now - _fpsWindowStartTick;
        if (elapsedSinceWindow >= Stopwatch.Frequency)
        {
            ActualFps = _frameCount * (double)Stopwatch.Frequency / elapsedSinceWindow;
            _frameCount = 0;
            _fpsWindowStartTick = now;
        }
    }
}
