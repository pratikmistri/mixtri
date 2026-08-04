using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Musio.Core.Diagnostics;
using Musio.Core.Processing;

namespace Musio_App.Services;

/// <summary>
/// Owns the shared Win2D <see cref="CanvasDevice"/> attach/detach lifecycle plus the
/// queued/re-entrant retry machinery that recovers the editor after the device raises
/// <see cref="CanvasDevice.DeviceLost"/> (GPU driver restart / TDR). The actual recovery work
/// — tearing down and rebuilding the preview pipeline, renderers, and timeline thumbnails —
/// stays owned by the editor page and is supplied via the <c>recoverAsync</c> delegate; this
/// class only extracts the device subscription and the
/// queued/in-progress/requested flag dance around it, so it complements (rather than
/// duplicates) <see cref="GpuContext"/>, which centralizes shared-device acquisition for the
/// Core-side renderers.
/// </summary>
public sealed class EditorGraphicsDeviceManager
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<Task> _recoverAsync;
    private readonly Func<bool> _isUnloaded;

    private CanvasDevice? _graphicsDevice;
    private int _graphicsRecoveryQueued;
    private int _graphicsRecoveryRequested;
    private bool _graphicsRecoveryInProgress;

    /// <param name="dispatcherQueue">
    /// The owning page's <see cref="DispatcherQueue"/>, used to marshal recovery back onto the
    /// UI thread from the (potentially non-UI) <see cref="CanvasDevice.DeviceLost"/> callback.
    /// </param>
    /// <param name="recoverAsync">
    /// Performs the actual device recovery (tearing down and rebuilding renderers/preview
    /// state). Always invoked on the UI thread.
    /// </param>
    /// <param name="isUnloaded">
    /// Returns true once the owning page has unloaded, so a queued or in-flight recovery does
    /// not keep retrying or touch torn-down page state.
    /// </param>
    public EditorGraphicsDeviceManager(DispatcherQueue dispatcherQueue, Func<Task> recoverAsync, Func<bool> isUnloaded)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _recoverAsync = recoverAsync ?? throw new ArgumentNullException(nameof(recoverAsync));
        _isUnloaded = isUnloaded ?? throw new ArgumentNullException(nameof(isUnloaded));
    }

    /// <summary>True while a device-lost recovery is running. Callers that render off the
    /// shared device (e.g. the preview) should defer/coalesce rather than render while this is
    /// true, since the device and dependent GPU resources are mid-teardown/rebuild.</summary>
    public bool IsRecoveryInProgress => _graphicsRecoveryInProgress;

    public void Attach()
    {
        try
        {
            var device = GpuContext.GetSharedDevice();
            if (ReferenceEquals(_graphicsDevice, device))
                return;

            Detach();
            _graphicsDevice = device;
            _graphicsDevice.DeviceLost += OnGraphicsDeviceLost;
        }
        catch (Exception ex)
        {
            DiagLog.Write("Editor", $"failed to attach graphics-device recovery: {ex.Message}");
        }
    }

    public void Detach()
    {
        if (_graphicsDevice is null)
            return;

        _graphicsDevice.DeviceLost -= OnGraphicsDeviceLost;
        _graphicsDevice = null;
    }

    private void OnGraphicsDeviceLost(CanvasDevice sender, object args)
    {
        DiagLog.Write("Editor", "shared CanvasDevice lost; scheduling editor graphics recovery");

        Interlocked.Exchange(ref _graphicsRecoveryRequested, 1);
        if (Interlocked.Exchange(ref _graphicsRecoveryQueued, 1) != 0)
            return;

        if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    do
                    {
                        Interlocked.Exchange(ref _graphicsRecoveryRequested, 0);
                        await RunRecoveryAsync();
                    }
                    while (!_isUnloaded()
                        && Interlocked.CompareExchange(
                            ref _graphicsRecoveryRequested, 0, 0) != 0);
                }
                catch (Exception ex)
                {
                    DiagLog.Write("Editor", $"graphics recovery failed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _graphicsRecoveryQueued, 0);
                }
            }))
        {
            // TryEnqueue returning false means nothing will ever run the loop above, so the
            // queued flag must be reset here — otherwise it stays latched and every future
            // DeviceLost is silently dropped by the `!= 0` check above for the life of the page.
            Interlocked.Exchange(ref _graphicsRecoveryQueued, 0);
        }
    }

    private async Task RunRecoveryAsync()
    {
        if (_isUnloaded() || _graphicsRecoveryInProgress)
            return;

        _graphicsRecoveryInProgress = true;
        try
        {
            await _recoverAsync();
        }
        finally
        {
            _graphicsRecoveryInProgress = false;
        }
    }
}
